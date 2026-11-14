using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.WebClients.Wasabi;

/// <summary>
/// Gives up on a server that does not start to answer in time, and on one that falls silent in the middle of its
/// answer. How long the whole answer takes is not limited: a big answer over a slow connection, like a block that
/// comes through Tor, gets all the time it needs as long as it keeps coming.
/// </summary>
/// <remarks>The client that sends through this handler must not have a timeout of its own, see <see cref="HttpClient.Timeout"/>.</remarks>
public class StallTimeoutHandler : HttpMessageHandler
{
	private readonly HttpMessageInvoker _innerHandler;
	private readonly TimeSpan _answerTimeout;
	private readonly TimeSpan _silenceTimeout;

	/// <param name="innerHandler">The handler that sends the requests. It stays with whoever created it and is not disposed with this one.</param>
	/// <param name="answerTimeout">How long the server may take until its answer starts to arrive.</param>
	/// <param name="silenceTimeout">How long nothing may arrive while the answer is on its way.</param>
	public StallTimeoutHandler(HttpMessageHandler innerHandler, TimeSpan answerTimeout, TimeSpan silenceTimeout)
	{
		_innerHandler = new HttpMessageInvoker(innerHandler, disposeHandler: false);
		_answerTimeout = answerTimeout;
		_silenceTimeout = silenceTimeout;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		HttpResponseMessage response;

		using (var answerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			answerCts.CancelAfter(_answerTimeout);

			try
			{
				// This returns as soon as the head of the answer is here. The body is read by whoever asks for it.
				response = await _innerHandler.SendAsync(request, answerCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException($"The server did not start to answer within {_answerTimeout.TotalSeconds:0} seconds.");
			}
		}

		response.Content = new StallTimeoutContent(response.Content, _silenceTimeout);
		return response;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_innerHandler.Dispose();
		}

		base.Dispose(disposing);
	}

	/// <summary>The body of an answer. Reading it fails when nothing arrives for too long.</summary>
	private sealed class StallTimeoutContent : HttpContent
	{
		private readonly HttpContent _content;
		private readonly TimeSpan _silenceTimeout;

		public StallTimeoutContent(HttpContent content, TimeSpan silenceTimeout)
		{
			_content = content;
			_silenceTimeout = silenceTimeout;

			foreach (var header in content.Headers)
			{
				Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
			SerializeToStreamAsync(stream, context, CancellationToken.None);

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
		{
			var source = await _content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			await using var disposeSource = source.ConfigureAwait(false);

			using var silenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var buffer = ArrayPool<byte>.Shared.Rent(81_920);

			try
			{
				while (true)
				{
					// Every piece that arrives starts the wait anew.
					silenceCts.CancelAfter(_silenceTimeout);
					int read;

					try
					{
						read = await source.ReadAsync(buffer, silenceCts.Token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
					{
						throw new TimeoutException($"The server sent nothing for {_silenceTimeout.TotalSeconds:0} seconds in the middle of its answer.");
					}

					if (read == 0)
					{
						return;
					}

					await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		protected override bool TryComputeLength(out long length)
		{
			length = _content.Headers.ContentLength ?? 0;
			return _content.Headers.ContentLength is not null;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_content.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
