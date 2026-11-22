using System.Collections.Generic;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.Tests.Helpers;

/// <summary>
/// What a coordinator looks like to somebody who asks it for its rounds: an HTTP server on this computer that answers
/// the status request with no rounds at all. Everything else it is asked for it does not know.
/// </summary>
public sealed class TestCoordinatorServer : IAsyncDisposable
{
	private readonly HttpListener _listener = new();
	private readonly Task _serveLoop;
	private readonly Lock _lock = new();
	private readonly List<string> _requests = [];

	public TestCoordinatorServer()
	{
		Uri = new Uri($"http://127.0.0.1:{TestNostrRelay.GetFreePort()}/");
		_listener.Prefixes.Add(Uri.ToString());
		_listener.Start();
		_serveLoop = Task.Run(ServeLoopAsync);
	}

	/// <summary>The address of the coordinator.</summary>
	public Uri Uri { get; }

	/// <summary>What the server answers the status request with. It is the answer of a coordinator without rounds unless something else is put here.</summary>
	public string StatusAnswer { get; set; } = """{"roundStates":[],"coinJoinFeeRateMedians":[]}""";

	/// <summary>The paths that were asked for, in the order of arrival.</summary>
	public IReadOnlyList<string> Requests
	{
		get
		{
			lock (_lock)
			{
				return [.. _requests];
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		_listener.Stop();

		try
		{
			await _serveLoop;
		}
		catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
		{
		}

		_listener.Close();
	}

	private async Task ServeLoopAsync()
	{
		while (_listener.IsListening)
		{
			var context = await _listener.GetContextAsync();
			var path = context.Request.Url?.AbsolutePath ?? "";

			lock (_lock)
			{
				_requests.Add(path);
			}

			var isStatus = context.Request.HttpMethod == "POST" && path == "/wabisabi/status";
			var body = Encoding.UTF8.GetBytes(isStatus ? StatusAnswer : "Not here.");

			context.Response.StatusCode = (int)(isStatus ? HttpStatusCode.OK : HttpStatusCode.NotFound);
			context.Response.ContentType = isStatus ? MediaTypeNames.Application.Json : MediaTypeNames.Text.Plain;
			context.Response.ContentLength64 = body.Length;
			await context.Response.OutputStream.WriteAsync(body);
			context.Response.Close();
		}
	}
}
