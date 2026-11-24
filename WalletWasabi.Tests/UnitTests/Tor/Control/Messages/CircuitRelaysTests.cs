using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Tor.Control;
using WalletWasabi.Tor.Control.Messages;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Tor.Control.Messages;

/// <summary>
/// The relays that the circuits of a Tor go through, and their addresses, as the control port of Tor tells them. The
/// Tor page of the settings answers with how many different addresses these are.
/// </summary>
public class CircuitRelaysTests
{
	private const string Guard = "E9F71AC06F29B2110E3FC09016B0E50407444EE2";
	private const string Middle = "D0423D3A13C18D2ED0F3D5BFD90E13E77C9AD239";
	private const string Exit = "3A9559477D72F71215850C97FA62A0DA7380964B";
	private const string OtherMiddle = "A0FA50A070CFB4B89737A27F3259F92C118A0AF0";
	private const string OtherExit = "7E77CC94D94C08609D70B517FF938CC61C9F8232";
	private const string NotBuiltYet = "706A7674A217BA905FE677E82236B7B968A23DB7";
	private const string Failed = "4D4938B725B89561773A161215D88B7C45C43C35";

	private static readonly string Circuits =
		"250+circuit-status=\r\n" +
		$"1 BUILT ${Guard}~libertas,${Middle}~d0xkb,${Exit}~PawNetBlue BUILD_FLAGS=NEED_CAPACITY PURPOSE=GENERAL TIME_CREATED=2021-05-15T14:04:17.615384\r\n" +
		$"2 BUILT ${Guard}~libertas,${OtherMiddle}~pipiska,${OtherExit}~pitfall BUILD_FLAGS=NEED_CAPACITY PURPOSE=GENERAL TIME_CREATED=2021-05-15T14:04:18.628885\r\n" +
		$"3 EXTENDED ${Guard}~libertas,${NotBuiltYet}~rofltor04 BUILD_FLAGS=IS_INTERNAL,NEED_CAPACITY PURPOSE=MEASURE_TIMEOUT TIME_CREATED=2021-05-15T14:04:19.631228\r\n" +
		$"4 FAILED ${Failed}~TheGreenDynamo BUILD_FLAGS=NEED_CAPACITY PURPOSE=GENERAL TIME_CREATED=2021-05-15T14:04:20.631228 REASON=TIMEOUT\r\n" +
		$"5 BUILT ${Guard}=libertas,${Middle}~d0xkb,${OtherExit} BUILD_FLAGS=IS_INTERNAL,NEED_CAPACITY PURPOSE=HS_CLIENT_REND TIME_CREATED=2021-05-15T14:04:21.631228\r\n" +
		".\r\n" +
		"250 OK\r\n";

	/// <summary>
	/// Every relay of a circuit that is built counts once, however many circuits go through it: first hops, middle
	/// relays and exits. A circuit that is still being built, or that failed, is no connection.
	/// </summary>
	[Fact]
	public async Task RelaysOfBuiltCircuitsAreToldOnceAsync()
	{
		var reply = await TorControlReplyReaderTest.ParseAsync(Circuits);

		Assert.Equal([Guard, Middle, Exit, OtherMiddle, OtherExit], CircuitRelays.GetBuiltRelayFingerprints(reply));
	}

	/// <summary>A Tor without a circuit answers on one line.</summary>
	[Fact]
	public async Task TorWithoutCircuitsGoesThroughNoRelayAsync()
	{
		var reply = await TorControlReplyReaderTest.ParseAsync("250-circuit-status=\r\n250 OK\r\n");

		Assert.Empty(CircuitRelays.GetBuiltRelayFingerprints(reply));
	}

	/// <summary>The entry of a relay in the directory has the address of the relay on the line that names it.</summary>
	[Theory]
	[InlineData("r libertas 6fcaxxLoremIpsumBase64Identity cO2rDescriptorDigestBase64 2038-01-01 00:00:00 192.0.2.5 9001 0", "192.0.2.5")]
	[InlineData("r pitfall 6fcaxxLoremIpsumBase64Identity 2038-01-01 00:00:00 198.51.100.77 443 80", "198.51.100.77")]
	public async Task AddressOfARelayIsOnTheLineThatNamesItAsync(string line, string expected)
	{
		var reply = await TorControlReplyReaderTest.ParseAsync(
			$"250+ns/id/{Guard}=\r\n{line}\r\na [2001:db8::5]:9001\r\ns Fast Guard Running Stable V2Dir Valid\r\nw Bandwidth=12000\r\n.\r\n250 OK\r\n");

		Assert.True(CircuitRelays.TryParseRelayAddress(reply, out var address));
		Assert.Equal(IPAddress.Parse(expected), address);
	}

	[Theory]
	[InlineData("552 Unrecognized key \"ns/id/E9F71AC06F29B2110E3FC09016B0E50407444EE2\"\r\n")]
	[InlineData("250+ns/id/E9F71AC06F29B2110E3FC09016B0E50407444EE2=\r\ns Fast Running Valid\r\n.\r\n250 OK\r\n")]
	[InlineData("250+ns/id/E9F71AC06F29B2110E3FC09016B0E50407444EE2=\r\nr libertas 6fcaxx cO2r 2038-01-01 00:00:00 not.an.ip.address 9001 0\r\n.\r\n250 OK\r\n")]
	[InlineData("250+ns/id/E9F71AC06F29B2110E3FC09016B0E50407444EE2=\r\nr libertas 6fcaxx cO2r 2038-01-01 00:00:00 999.1.2.3 9001 0\r\n.\r\n250 OK\r\n")]
	public async Task RelayThatTheDirectoryDoesNotTellHasNoAddressAsync(string data)
	{
		var reply = await TorControlReplyReaderTest.ParseAsync(data);

		Assert.False(CircuitRelays.TryParseRelayAddress(reply, out var address));
		Assert.Null(address);
	}

	/// <summary>
	/// The whole question, as Tor is asked: the circuits first, then the directory entry of every relay in them. Two
	/// relays at one address are one address, and a relay that the directory does not have is left out.
	/// </summary>
	[Fact(Timeout = 60_000)]
	public async Task AddressesOfTheRelaysOfTheCircuitsAreToldEveryOneOnceAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeoutCts.CancelAfter(TimeSpan.FromSeconds(50));

		var toServer = new Pipe();
		var toClient = new Pipe();

		await using var client = new TorControlClient(pipeReader: toClient.Reader, pipeWriter: toServer.Writer);

		var addresses = new Dictionary<string, string?>
		{
			[Guard] = "192.0.2.5",
			[Middle] = "198.51.100.77",
			[Exit] = "203.0.113.9",

			// A relay next to the first exit, at the same address.
			[OtherExit] = "203.0.113.9",

			// A bridge: the directory does not have it.
			[OtherMiddle] = null
		};

		var asked = new List<string>();

		var serverTask = Task.Run(async () =>
		{
			Assert.Equal("GETINFO circuit-status", await toServer.Reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false));
			await toClient.Writer.WriteAsciiAndFlushAsync(Circuits, timeoutCts.Token).ConfigureAwait(false);

			for (var i = 0; i < addresses.Count; i++)
			{
				var command = await toServer.Reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
				Assert.StartsWith("GETINFO ns/id/", command);

				var fingerprint = command["GETINFO ns/id/".Length..];
				asked.Add(fingerprint);

				var answer = addresses[fingerprint] is { } address
					? $"250+ns/id/{fingerprint}=\r\nr relay 6fcaxx cO2r 2038-01-01 00:00:00 {address} 9001 0\r\ns Fast Running Valid\r\n.\r\n250 OK\r\n"
					: $"552 Unrecognized key \"ns/id/{fingerprint}\"\r\n";

				await toClient.Writer.WriteAsciiAndFlushAsync(answer, timeoutCts.Token).ConfigureAwait(false);
			}
		});

		var told = await client.GetCircuitRelayAddressesAsync(timeoutCts.Token);
		await serverTask;

		// The relays of the built circuits were asked about, every one once, and no other.
		Assert.Equal([Guard, Middle, Exit, OtherMiddle, OtherExit], asked);

		Assert.Equal(3, told.Count);
		Assert.Equal(
			new[] { "192.0.2.5", "198.51.100.77", "203.0.113.9" },
			told.Select(address => address.ToString()).Order());
	}
}
