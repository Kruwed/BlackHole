using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Tor.Control;
using WalletWasabi.Tor.Control.Exceptions;
using WalletWasabi.Tor.Control.Messages;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Tor.Control.Messages;

/// <summary>
/// How Tor is asked for an onion service that leads to a port of this computer, and what it answers. The Nostr relay
/// of the application is reached at such a service, and nowhere else.
/// </summary>
public class OnionServiceTests
{
	/// <summary>A name as onion services have them. It is made up: no service has it.</summary>
	private static readonly string ServiceId = new string('a', 55) + "d";

	/// <summary>A key in the form in which Tor tells keys. It is made up as well.</summary>
	private static readonly string Key = "ED25519-V3:" + Convert.ToBase64String([.. Enumerable.Range(0, 64).Select(i => (byte)i)]);

	private static readonly IPEndPoint Target = new(IPAddress.Loopback, 45123);

	/// <summary>A new service is asked for with a new key, and Tor is to tell that key.</summary>
	[Fact]
	public void NewServiceIsAskedForWithANewKey()
	{
		var options = new OnionServiceOptions(80, Target);

		Assert.Equal("ADD_ONION NEW:ED25519-V3 Port=80,127.0.0.1:45123\r\n", options.ToCommand());
	}

	/// <summary>A service that was there before is asked for with its key, which Tor need not tell back.</summary>
	[Fact]
	public void KnownServiceIsAskedForWithItsKey()
	{
		var options = new OnionServiceOptions(80, Target) { PrivateKey = Key };

		Assert.Equal($"ADD_ONION {Key} Flags=DiscardPK Port=80,127.0.0.1:45123\r\n", options.ToCommand());
	}

	/// <summary>
	/// The guards of a service are part of the command: how many connections one circuit may hold, and that visitors
	/// work for their turn when too many come.
	/// </summary>
	[Fact]
	public void GuardsOfTheServiceAreAskedFor()
	{
		var options = new OnionServiceOptions(80, Target)
		{
			PrivateKey = Key,
			MaxStreamsPerCircuit = 4,
			ProofOfWork = new OnionServiceProofOfWork(QueueRate: 5, QueueBurst: 40)
		};

		Assert.Equal(
			$"ADD_ONION {Key} Flags=DiscardPK,MaxStreamsCloseCircuit MaxStreams=4 PoWDefensesEnabled=1 PoWQueueRate=5 PoWQueueBurst=40 Port=80,127.0.0.1:45123\r\n",
			options.ToCommand());

		var newService = options with { PrivateKey = null, ProofOfWork = null };

		Assert.Equal("ADD_ONION NEW:ED25519-V3 Flags=MaxStreamsCloseCircuit MaxStreams=4 Port=80,127.0.0.1:45123\r\n", newService.ToCommand());
	}

	/// <summary>
	/// A key is read from a file and written into a command. What is not a key is not sent to Tor: it could be a
	/// second command.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("ED25519-V3:")]
	[InlineData("ED25519-V3:short")]
	[InlineData("RSA1024:MIICXAIBAAKBgQDKHkkw5VqSHvZ3Y4lYCzGhRdvnMHCpMsaHbNyP0LmkxiWGbJBf5eeOW14tN7KX6XcTGPfO")]
	[InlineData("NEW:ED25519-V3")]
	[InlineData("ED25519-V3:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw== Port=22,22")]
	[InlineData("ED25519-V3:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==\r\nSIGNAL SHUTDOWN")]
	[InlineData(" ED25519-V3:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==")]
	[InlineData("ED25519-V3:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==\n")]
	public void WhatIsNoKeyIsNotSentToTor(string key)
	{
		Assert.False(OnionServiceOptions.IsPrivateKey(key));
		Assert.Throws<ArgumentException>(() => new OnionServiceOptions(80, Target) { PrivateKey = key }.ToCommand());
	}

	[Fact]
	public void KeyOfTorIsAKey()
	{
		Assert.True(OnionServiceOptions.IsPrivateKey(Key));
		Assert.False(OnionServiceOptions.IsPrivateKey(null));
	}

	[Theory]
	[InlineData(0, 45123)]
	[InlineData(65536, 45123)]
	[InlineData(80, 0)]
	public void WhatIsNoPortIsNotAskedFor(int virtualPort, int targetPort)
	{
		Assert.Throws<ArgumentException>(() => new OnionServiceOptions(virtualPort, new IPEndPoint(IPAddress.Loopback, targetPort)).ToCommand());
	}

	[Fact]
	public void LimitsHaveToBeLimits()
	{
		Assert.Throws<ArgumentException>(() => new OnionServiceOptions(80, Target) { MaxStreamsPerCircuit = -1 }.ToCommand());
		Assert.Throws<ArgumentException>(() => new OnionServiceOptions(80, Target) { ProofOfWork = new OnionServiceProofOfWork(0, 40) }.ToCommand());
		Assert.Throws<ArgumentException>(() => new OnionServiceOptions(80, Target) { ProofOfWork = new OnionServiceProofOfWork(5, 0) }.ToCommand());
	}

	/// <summary>Tor tells the name of a new service and its key.</summary>
	[Fact]
	public async Task NewServiceComesWithItsKeyAsync()
	{
		var reply = await TorControlReplyReaderTest.ParseAsync($"250-ServiceID={ServiceId}\r\n250-PrivateKey={Key}\r\n250 OK\r\n");

		var service = OnionService.FromReply(reply, givenKey: null);

		Assert.Equal(ServiceId, service.ServiceId);
		Assert.Equal(Key, service.PrivateKey);
		Assert.Equal($"{ServiceId}.onion", service.Host);
	}

	/// <summary>Of a service that was asked for with its key, Tor tells the name only. The key is the one that was given.</summary>
	[Fact]
	public async Task KnownServiceKeepsItsKeyAsync()
	{
		var reply = await TorControlReplyReaderTest.ParseAsync($"250-ServiceID={ServiceId}\r\n250 OK\r\n");

		var service = OnionService.FromReply(reply, givenKey: Key);

		Assert.Equal(ServiceId, service.ServiceId);
		Assert.Equal(Key, service.PrivateKey);
	}

	/// <summary>What Tor refuses is told with the words of Tor.</summary>
	[Fact]
	public async Task RefusalOfTorIsToldAsync()
	{
		var reply = await TorControlReplyReaderTest.ParseAsync("550 Onion address collision\r\n");

		var exception = Assert.Throws<TorControlException>(() => OnionService.FromReply(reply, givenKey: Key));

		Assert.Contains("550 Onion address collision", exception.Message, StringComparison.Ordinal);
	}

	/// <summary>An answer without a name, with a name that is none, or without the key of a new service, is no service.</summary>
	[Theory]
	[InlineData("250 OK\r\n", true)]
	[InlineData("250-ServiceID=short\r\n250 OK\r\n", true)]
	[InlineData("250-ServiceID=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD\r\n250 OK\r\n", true)]
	[InlineData("250-ServiceID=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad\r\n250 OK\r\n", false)]
	[InlineData("250-ServiceID=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad\r\n250-PrivateKey=nothing\r\n250 OK\r\n", false)]
	public async Task AnswerThatTellsNoServiceIsNoneAsync(string answer, bool withKey)
	{
		var reply = await TorControlReplyReaderTest.ParseAsync(answer);

		Assert.Throws<TorControlException>(() => OnionService.FromReply(reply, withKey ? Key : null));
	}

	/// <summary>
	/// Tor tells when it has handed the description of a service to a relay that keeps such descriptions: visitors can
	/// find the service from then on. Other events, and events about other services, do not tell that.
	/// </summary>
	[Theory]
	[InlineData("650 HS_DESC UPLOADED aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad UNKNOWN $E9F71AC06F29B2110E3FC09016B0E50407444EE2~libertas\r\n", true)]
	[InlineData("650 HS_DESC UPLOAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad UNKNOWN $E9F71AC06F29B2110E3FC09016B0E50407444EE2~libertas dGhlIGRlc2NyaXB0b3I HSDIR_INDEX=1 \r\n", false)]
	[InlineData("650 HS_DESC CREATED aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad UNKNOWN UNKNOWN dGhlIGRlc2NyaXB0b3I\r\n", false)]
	[InlineData("650 HS_DESC FAILED aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad UNKNOWN $E9F71AC06F29B2110E3FC09016B0E50407444EE2~libertas REASON=UPLOAD_REJECTED\r\n", false)]
	[InlineData("650 HS_DESC UPLOADED bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbd UNKNOWN $E9F71AC06F29B2110E3FC09016B0E50407444EE2~libertas\r\n", false)]
	[InlineData("650 CIRC 1000 EXTENDED moria1,moria2\r\n", false)]
	[InlineData("250 HS_DESC UPLOADED aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad UNKNOWN x\r\n", false)]
	public async Task PublishedServiceIsToldByItsEventAsync(string torEvent, bool isPublished)
	{
		var reply = await TorControlReplyReaderTest.ParseAsync(torEvent);

		Assert.Equal(isPublished, OnionService.IsPublished(reply, ServiceId));
	}

	/// <summary>The whole exchange: the client asks Tor for a service and reads which one it got.</summary>
	[Fact(Timeout = 120000)] // 2 minutes
	public async Task ClientAsksTorForTheServiceAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

		var toServer = new Pipe();
		var toClient = new Pipe();

		await using var client = new TorControlClient(pipeReader: toClient.Reader, pipeWriter: toServer.Writer);

		var createTask = client.CreateOnionServiceAsync(new OnionServiceOptions(80, Target) { MaxStreamsPerCircuit = 4 }, timeoutCts.Token);

		Assert.Equal(
			"ADD_ONION NEW:ED25519-V3 Flags=MaxStreamsCloseCircuit MaxStreams=4 Port=80,127.0.0.1:45123",
			await toServer.Reader.ReadLineAsync(timeoutCts.Token));
		await toClient.Writer.WriteAsciiAndFlushAsync($"250-ServiceID={ServiceId}\r\n250-PrivateKey={Key}\r\n250 OK\r\n", timeoutCts.Token);

		var service = await createTask;

		Assert.Equal(new OnionService(ServiceId, Key), service);

		// A Tor that knows none of this says so, and that is what the caller is told.
		var refusedTask = client.CreateOnionServiceAsync(new OnionServiceOptions(80, Target) { PrivateKey = Key }, timeoutCts.Token);

		Assert.Equal($"ADD_ONION {Key} Flags=DiscardPK Port=80,127.0.0.1:45123", await toServer.Reader.ReadLineAsync(timeoutCts.Token));
		await toClient.Writer.WriteAsciiAndFlushAsync("510 Unrecognized command \"ADD_ONION\"\r\n", timeoutCts.Token);

		await Assert.ThrowsAsync<TorControlException>(async () => await refusedTask);
	}
}
