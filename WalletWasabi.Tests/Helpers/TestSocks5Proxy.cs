using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.Tests.Helpers;

/// <summary>What a client asked a SOCKS5 proxy for: who it said it is, and where it wanted to be connected to.</summary>
/// <param name="UserName">The user name that the client gave, or null if it gave none.</param>
/// <param name="Host">The destination as the client named it: a host name if the client left the resolving to the proxy, an address otherwise.</param>
/// <param name="IsHostName">Whether the client named the destination by its name.</param>
public record SocksRequest(string? UserName, string? Password, string Host, int Port, bool IsHostName);

/// <summary>
/// A SOCKS5 proxy for tests, standing in for Tor: it takes note of what it is asked for and connects every client to
/// one place, wherever the client wanted to go.
/// </summary>
public sealed class TestSocks5Proxy : IAsyncDisposable
{
	private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
	private readonly CancellationTokenSource _cts = new();
	private readonly IPEndPoint _destination;
	private readonly Task _acceptLoop;
	private readonly Lock _lock = new();
	private readonly List<SocksRequest> _requests = [];

	/// <param name="destination">Where every client is connected to.</param>
	public TestSocks5Proxy(IPEndPoint destination)
	{
		_destination = destination;
		_listener.Start();
		EndPoint = (IPEndPoint)_listener.LocalEndpoint;
		_acceptLoop = Task.Run(AcceptLoopAsync);
	}

	public IPEndPoint EndPoint { get; }

	public IReadOnlyList<SocksRequest> Requests
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
		await _cts.CancelAsync();
		_listener.Stop();

		try
		{
			await _acceptLoop;
		}
		catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
		{
		}

		_cts.Dispose();
	}

	private async Task AcceptLoopAsync()
	{
		while (!_cts.IsCancellationRequested)
		{
			var client = await _listener.AcceptTcpClientAsync(_cts.Token);
			_ = Task.Run(() => ServeAsync(client));
		}
	}

	private async Task ServeAsync(TcpClient client)
	{
		try
		{
			using var _ = client;
			var stream = client.GetStream();

			// The client names the ways it can say who it is: 5, count, the ways.
			var greeting = await ReadAsync(stream, 2);
			var methods = await ReadAsync(stream, greeting[1]);

			string? userName = null;
			string? password = null;

			// Like Tor, the proxy asks for a name and a password when the client offers them.
			if (Array.IndexOf(methods, (byte)0x02) >= 0)
			{
				await stream.WriteAsync(new byte[] { 5, 2 }, _cts.Token);

				var nameLength = (await ReadAsync(stream, 2))[1];
				userName = Encoding.UTF8.GetString(await ReadAsync(stream, nameLength));
				var passwordLength = (await ReadAsync(stream, 1))[0];
				password = Encoding.UTF8.GetString(await ReadAsync(stream, passwordLength));

				await stream.WriteAsync(new byte[] { 1, 0 }, _cts.Token);
			}
			else
			{
				await stream.WriteAsync(new byte[] { 5, 0 }, _cts.Token);
			}

			// The request: 5, command, 0, kind of address, address, port.
			var request = await ReadAsync(stream, 4);
			string host;
			var isHostName = false;

			switch (request[3])
			{
				case 1:
					host = new IPAddress(await ReadAsync(stream, 4)).ToString();
					break;

				case 3:
					var length = (await ReadAsync(stream, 1))[0];
					host = Encoding.ASCII.GetString(await ReadAsync(stream, length));
					isHostName = true;
					break;

				case 4:
					host = new IPAddress(await ReadAsync(stream, 16)).ToString();
					break;

				default:
					throw new InvalidDataException("Unknown kind of address.");
			}

			var portBytes = await ReadAsync(stream, 2);

			lock (_lock)
			{
				_requests.Add(new SocksRequest(userName, password, host, (portBytes[0] << 8) | portBytes[1], isHostName));
			}

			using var destination = new TcpClient();
			await destination.ConnectAsync(_destination, _cts.Token);

			await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, _cts.Token);

			var destinationStream = destination.GetStream();
			await Task.WhenAny(stream.CopyToAsync(destinationStream, _cts.Token), destinationStream.CopyToAsync(stream, _cts.Token));
		}
		catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
		{
			// The client went away, or the proxy is closing down.
		}
	}

	private async Task<byte[]> ReadAsync(NetworkStream stream, int count)
	{
		var buffer = new byte[count];
		await stream.ReadExactlyAsync(buffer, _cts.Token);
		return buffer;
	}
}
