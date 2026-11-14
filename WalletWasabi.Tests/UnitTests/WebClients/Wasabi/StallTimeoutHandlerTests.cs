using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.WebClients.Wasabi;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WebClients.Wasabi;

/// <summary>
/// Tests for <see cref="StallTimeoutHandler"/>: an answer may take as long as it needs, as long as it keeps coming.
/// </summary>
public class StallTimeoutHandlerTests
{
	private static readonly Uri Server = new("http://node.example:8332/");

	[Fact]
	public async Task ReadsAnAnswerThatComesSlowlyToItsEndAsync()
	{
		// Twelve pieces, a quarter of a second apart: the answer takes longer than the server may be silent.
		var silenceTimeout = TimeSpan.FromSeconds(2);
		var pieces = Enumerable.Range(1, 12).Select(i => $"piece {i};").ToArray();
		using var body = new DripStream(pieces, gap: TimeSpan.FromMilliseconds(250));
		using var server = new FakeHandler((_, _) => Task.FromResult(Answer(body)));
		using var client = CreateClient(server, answerTimeout: TimeSpan.FromSeconds(30), silenceTimeout);

		var stopwatch = Stopwatch.StartNew();
		using var response = await AskAsync(client);
		var answer = await response.Content.ReadAsStringAsync();

		Assert.Equal(string.Concat(pieces), answer);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
		Assert.True(stopwatch.Elapsed > silenceTimeout, $"The answer took only {stopwatch.Elapsed}.");
	}

	[Fact]
	public async Task GivesUpOnAnAnswerThatStopsComingAsync()
	{
		// Two pieces arrive, then nothing. The server does not close the connection either.
		using var body = new DripStream(["piece 1;", "piece 2;"], gap: TimeSpan.FromMilliseconds(10), fallsSilent: true);
		using var server = new FakeHandler((_, _) => Task.FromResult(Answer(body)));
		using var client = CreateClient(server, answerTimeout: TimeSpan.FromSeconds(30), silenceTimeout: TimeSpan.FromMilliseconds(300));

		var exception = await Assert.ThrowsAnyAsync<Exception>(() => AskAsync(client));

		Assert.Contains("sent nothing", FindTimeout(exception).Message);
		Assert.True(body.ReadWasAbandoned);
	}

	[Fact]
	public async Task GivesUpOnAServerThatDoesNotStartToAnswerAsync()
	{
		using var server = new FakeHandler(async (_, cancellationToken) =>
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
			throw new UnreachableException();
		});
		using var client = CreateClient(server, answerTimeout: TimeSpan.FromMilliseconds(300), silenceTimeout: TimeSpan.FromSeconds(30));

		var exception = await Assert.ThrowsAnyAsync<Exception>(() => AskAsync(client));

		Assert.Contains("did not start to answer", FindTimeout(exception).Message);
	}

	[Fact]
	public async Task LeavesItToTheCallerToCancelAsync()
	{
		using var body = new DripStream(["piece 1;"], gap: TimeSpan.FromMilliseconds(10), fallsSilent: true);
		using var server = new FakeHandler((_, _) => Task.FromResult(Answer(body)));
		using var client = CreateClient(server, answerTimeout: TimeSpan.FromSeconds(30), silenceTimeout: TimeSpan.FromSeconds(30));
		using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

		// The caller gave up, nobody ran out of time.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(client, callerCts.Token));
	}

	[Fact]
	public void LeavesTheHandlerThatSendsAlone()
	{
		// The handler of the connections is shared by several clients. It lives on when one of them is disposed.
		using var server = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

		CreateClient(server, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)).Dispose();

		Assert.False(server.IsDisposed);
	}

	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client that is returned owns the handler.")]
	private static HttpClient CreateClient(HttpMessageHandler server, TimeSpan answerTimeout, TimeSpan silenceTimeout) =>
		new(new StallTimeoutHandler(server, answerTimeout, silenceTimeout)) { Timeout = Timeout.InfiniteTimeSpan };

	/// <summary>Sends a request the way the RPC client does: the whole answer is read before it is handed back.</summary>
	private static async Task<HttpResponseMessage> AskAsync(HttpClient client, CancellationToken cancellationToken = default)
	{
		using var request = new StringContent("{}");
		return await client.PostAsync(Server, request, cancellationToken);
	}

	private static HttpResponseMessage Answer(Stream body)
	{
		var content = new StreamContent(body);
		content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
	}

	/// <summary>The exception that tells that the server ran out of time, wherever it is in the chain.</summary>
	private static TimeoutException FindTimeout(Exception exception)
	{
		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			if (current is TimeoutException timeout)
			{
				return timeout;
			}
		}

		Assert.Fail($"Not a timeout: {exception}");
		throw new UnreachableException();
	}

	/// <summary>A server that is played by the test.</summary>
	private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
	{
		public bool IsDisposed { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			answer(request, cancellationToken);

		protected override void Dispose(bool disposing)
		{
			IsDisposed |= disposing;
			base.Dispose(disposing);
		}
	}

	/// <summary>The body of an answer that arrives piece by piece, with a pause before every piece.</summary>
	private sealed class DripStream(string[] pieces, TimeSpan gap, bool fallsSilent = false) : Stream
	{
		private int _nextPiece;

		/// <summary>Whether the reader stopped waiting for a piece that never came.</summary>
		public bool ReadWasAbandoned { get; private set; }

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_nextPiece == pieces.Length)
			{
				if (!fallsSilent)
				{
					return 0;
				}

				try
				{
					await Task.Delay(Timeout.Infinite, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					ReadWasAbandoned = true;
					throw;
				}
			}

			await Task.Delay(gap, cancellationToken);

			var piece = Encoding.ASCII.GetBytes(pieces[_nextPiece++]);
			piece.CopyTo(buffer);
			return piece.Length;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
