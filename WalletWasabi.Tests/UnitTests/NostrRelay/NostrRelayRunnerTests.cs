using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Models;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tor;
using WalletWasabi.Tor.Control;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The relay of the application is run the way its user chose: it is started and stopped, it is started again when
/// the user chose other events for it, and it keeps its address through all of that.
/// </summary>
public class NostrRelayRunnerTests
{
	private static readonly string Host = $"{FakeTor.DefaultServiceId}.onion";

	private static readonly NostrRelaySettings Custom = new(RelayEventChoice.Custom, KindList.FromStrings(["1", "30023"]));

	/// <summary>Reads from the command that asked Tor for the onion address where the relay listens.</summary>
	private static IPEndPoint GetRelayEndPoint(string addOnionCommand) =>
		IPEndPoint.Parse(addOnionCommand.Split(' ').Single(part => part.StartsWith("Port=80,", StringComparison.Ordinal))["Port=80,".Length..]);

	/// <summary>Asks the relay about itself the way a visitor of its onion address does.</summary>
	private static async Task<string> AskAboutItselfAsync(IPEndPoint relay, CancellationToken cancellationToken)
	{
		using var client = new TcpClient();
		await client.ConnectAsync(relay, cancellationToken);

		var stream = client.GetStream();
		await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {Host}\r\nAccept: application/nostr+json\r\n\r\n"), cancellationToken);

		using var answer = new MemoryStream();
		await stream.CopyToAsync(answer, cancellationToken);

		return Encoding.UTF8.GetString(answer.ToArray());
	}

	/// <summary>
	/// The relay is started, gets its onion address from Tor, and runs once the address is published. Started again
	/// with other events it is another relay at the same address. Stopped, it is gone.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayIsRunTheWayItsUserChoseAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var cancellationToken = timeoutCts.Token;
		var folder = Path.Combine(await Common.GetEmptyWorkDirAsync(), "NostrRelay", "RegTest");

		await using var tor = new FakeTor();
		await using var runner = new NostrRelayRunner(Network.RegTest, folder, _ => Task.FromResult(tor.Attempt), obstacle: null);

		var states = new List<NostrRelayState>();
		runner.StateChanged += (_, state) =>
		{
			lock (states)
			{
				states.Add(state);
			}
		};

		Assert.Equal(NostrRelayState.Off, runner.State);
		Assert.Null(runner.Settings);

		// Tor publishes the address a moment after it made it.
		var running = runner.RunAsync(NostrRelaySettings.Default, TimeSpan.FromSeconds(60), cancellationToken);
		var firstCommand = await tor.WaitForCommandAsync("ADD_ONION", cancellationToken);
		await tor.PublishAsync();

		Assert.Equal(new NostrRelayState(NostrRelayStatus.Running, new Uri($"ws://{Host}")), await running);
		Assert.Equal(NostrRelaySettings.Default, runner.Settings);
		Assert.StartsWith("ADD_ONION NEW:ED25519-V3 Flags=MaxStreamsCloseCircuit MaxStreams=4 PoWDefensesEnabled=1 PoWQueueRate=5 PoWQueueBurst=50 Port=80,127.0.0.1:", firstCommand, StringComparison.Ordinal);

		var firstEndPoint = GetRelayEndPoint(firstCommand);
		Assert.Contains("\"name\":\"Black Hole relay\"", await AskAboutItselfAsync(firstEndPoint, cancellationToken), StringComparison.Ordinal);

		// The events of Black Hole are kept in memory: the folder has the key of the address, and nothing else.
		Assert.Equal([NostrRelayRunner.KeyFileName], Directory.GetFiles(folder).Select(Path.GetFileName));

		// Asked for what it does already, the relay goes on as it is.
		Assert.Equal(NostrRelayStatus.Running, (await runner.RunAsync(NostrRelaySettings.Default, TimeSpan.FromSeconds(60), cancellationToken)).Status);
		Assert.Single(tor.Commands, command => command.StartsWith("ADD_ONION", StringComparison.Ordinal));

		// With other events it is started again, at the address that it had.
		var restarting = runner.RunAsync(Custom, TimeSpan.FromSeconds(60), cancellationToken);
		var secondCommand = await tor.WaitForCommandAsync("ADD_ONION", cancellationToken, occurrence: 2);
		await tor.PublishAsync();

		Assert.Equal(new NostrRelayState(NostrRelayStatus.Running, new Uri($"ws://{Host}")), await restarting);
		Assert.Equal(Custom, runner.Settings);
		Assert.StartsWith($"ADD_ONION {FakeTor.DefaultKey} Flags=DiscardPK,MaxStreamsCloseCircuit ", secondCommand, StringComparison.Ordinal);
		Assert.Contains($"DEL_ONION {FakeTor.DefaultServiceId}", tor.Commands);

		var secondEndPoint = GetRelayEndPoint(secondCommand);
		var information = await AskAboutItselfAsync(secondEndPoint, cancellationToken);
		Assert.Contains("\"name\":\"Nostr relay\"", information, StringComparison.Ordinal);
		Assert.Contains("1, 30023", information, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(folder, NostrRelayRunner.EventsFileName)));

		// The relay of before listens no more.
		if (!firstEndPoint.Equals(secondEndPoint))
		{
			using var probe = new TcpClient();
			await Assert.ThrowsAnyAsync<SocketException>(async () => await probe.ConnectAsync(firstEndPoint, cancellationToken));
		}

		await runner.StopAsync();

		Assert.Equal(NostrRelayState.Off, runner.State);
		Assert.Null(runner.Settings);
		Assert.Equal(2, tor.Commands.Count(command => command.StartsWith("DEL_ONION", StringComparison.Ordinal)));

		using (var probe = new TcpClient())
		{
			await Assert.ThrowsAnyAsync<SocketException>(async () => await probe.ConnectAsync(secondEndPoint, cancellationToken));
		}

		// What is kept stays for the next time: the key of the address, and the events of the user's list.
		Assert.True(File.Exists(Path.Combine(folder, NostrRelayRunner.KeyFileName)));
		Assert.True(File.Exists(Path.Combine(folder, NostrRelayRunner.EventsFileName)));

		lock (states)
		{
			Assert.Equal(NostrRelayState.Off, states[^1]);
			Assert.Contains(new NostrRelayState(NostrRelayStatus.Publishing, new Uri($"ws://{Host}")), states);
			Assert.Equal(2, states.Count(state => state.Status == NostrRelayStatus.Running));
		}

		// Stopping what is stopped changes nothing.
		await runner.StopAsync();
	}

	/// <summary>A relay whose address Tor does not publish in time is told as it stands, and goes on trying.</summary>
	[Fact(Timeout = 120000)]
	public async Task RelayThatIsNotPublishedInTimeGoesOnTryingAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var cancellationToken = timeoutCts.Token;
		var folder = Path.Combine(await Common.GetEmptyWorkDirAsync(), "NostrRelay", "RegTest");

		await using var tor = new FakeTor();
		await using var runner = new NostrRelayRunner(Network.RegTest, folder, _ => Task.FromResult(tor.Attempt), obstacle: null);

		// Started without waiting, the relay is on its way.
		var state = await runner.RunAsync(NostrRelaySettings.Default, TimeSpan.Zero, cancellationToken);

		Assert.Contains(state.Status, new[] { NostrRelayStatus.Starting, NostrRelayStatus.Publishing });
		Assert.Equal(NostrRelaySettings.Default, runner.Settings);

		// Tor makes the address, and does not publish it yet. Waiting for that a little while tells that it is not published.
		await tor.WaitForCommandAsync("ADD_ONION", cancellationToken);

		while (runner.State.Status == NostrRelayStatus.Starting)
		{
			await Task.Delay(10, cancellationToken);
		}

		state = await runner.RunAsync(NostrRelaySettings.Default, TimeSpan.FromMilliseconds(200), cancellationToken);

		Assert.Equal(new NostrRelayState(NostrRelayStatus.Publishing, new Uri($"ws://{Host}")), state);
		Assert.Single(tor.Commands, command => command.StartsWith("ADD_ONION", StringComparison.Ordinal));

		await tor.PublishAsync();

		Assert.Equal(NostrRelayStatus.Running, (await runner.RunAsync(NostrRelaySettings.Default, TimeSpan.FromSeconds(60), cancellationToken)).Status);
	}

	/// <summary>Where no relay can run, none is started, and the reason is what is told about it.</summary>
	[Fact(Timeout = 120000)]
	public async Task RelayThatCannotRunTellsWhyAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var cancellationToken = timeoutCts.Token;
		var folder = Path.Combine(await Common.GetEmptyWorkDirAsync(), "NostrRelay", "RegTest");

		await using var tor = new FakeTor();

		await using (var runner = new NostrRelayRunner(Network.RegTest, folder, _ => Task.FromResult(tor.Attempt), obstacle: "Tor is turned off."))
		{
			var state = await runner.RunAsync(NostrRelaySettings.Default, TimeSpan.FromSeconds(60), cancellationToken);

			Assert.Equal(new NostrRelayState(NostrRelayStatus.NotWorking, Problem: "Tor is turned off."), state);
			Assert.Equal(state, runner.State);
			Assert.True(state.IsOn);
			Assert.Null(runner.Settings);

			await runner.StopAsync();
			Assert.Equal(NostrRelayState.Off, runner.State);
		}

		await using (var runner = new NostrRelayRunner(Network.RegTest, folder, _ => Task.FromResult(tor.Attempt), obstacle: null))
		{
			// A list of the user's own without a kind in it.
			var state = await runner.RunAsync(new NostrRelaySettings(RelayEventChoice.Custom, KindList.Empty), TimeSpan.FromSeconds(60), cancellationToken);

			Assert.Equal(NostrRelayStatus.NotWorking, state.Status);
			Assert.Equal("Name one kind of event at least for the relay to keep, like 1.", state.Problem);

			// A file of events that is none.
			Directory.CreateDirectory(folder);
			await File.WriteAllTextAsync(Path.Combine(folder, NostrRelayRunner.EventsFileName), "this is no database, and it is long enough to be told from one that is empty", cancellationToken);

			state = await runner.RunAsync(Custom, TimeSpan.FromSeconds(60), cancellationToken);

			Assert.Equal(NostrRelayStatus.NotWorking, state.Status);
			Assert.StartsWith("The relay could not be started: ", state.Problem, StringComparison.Ordinal);

			// The events of Black Hole need no file, so that relay runs.
			var running = runner.RunAsync(NostrRelaySettings.Default, TimeSpan.FromSeconds(60), cancellationToken);
			await tor.WaitForCommandAsync("ADD_ONION", cancellationToken);
			await tor.PublishAsync();

			Assert.Equal(NostrRelayStatus.Running, (await running).Status);
		}

		// Nothing was asked of Tor for the relays that could not run.
		Assert.Single(tor.Commands, command => command.StartsWith("ADD_ONION", StringComparison.Ordinal));
	}

	/// <summary>A relay takes a Tor that the application started itself: only that Tor can be asked for an onion address.</summary>
	[Fact]
	public void RelayTakesATorOfTheApplication()
	{
		Assert.Null(NostrRelayRunner.DescribeObstacle(TorMode.Enabled, isRegTest: false));

		Assert.Equal(
			"A relay is reached at an onion address, and Tor is turned off.",
			NostrRelayRunner.DescribeObstacle(TorMode.Disabled, isRegTest: false));
		Assert.Equal(
			"A relay is reached at an onion address, and Tor is not used on regtest.",
			NostrRelayRunner.DescribeObstacle(TorMode.Disabled, isRegTest: true));
		Assert.Equal(
			"A relay is reached at an onion address, which Black Hole asks its own Tor for. With Tor set to \"Connect Only\" it uses a Tor that it did not start, and that Tor cannot be asked.",
			NostrRelayRunner.DescribeObstacle(TorMode.EnabledOnlyRunning, isRegTest: false));
	}

	[Fact]
	public void ChoiceOfEventsIsReadFromTheSettings()
	{
		Assert.Equal(NostrRelaySettings.Default, NostrRelaySettings.Parse("BlackHole", []));
		Assert.Equal(NostrRelaySettings.Default, NostrRelaySettings.Parse("nonsense", []));
		Assert.Equal(NostrRelaySettings.Default, NostrRelaySettings.Parse("7", []));
		Assert.Equal(Custom, NostrRelaySettings.Parse("custom", ["30023", "1"]));

		// The kinds are kept for the next time while the relay takes the events of Black Hole.
		var kept = NostrRelaySettings.Parse("BlackHole", ["1", "30023"]);
		Assert.Equal(RelayEventChoice.BlackHole, kept.Events);
		Assert.Equal("1, 30023", kept.Kinds.ToString());
		Assert.Null(kept.Problem);
		Assert.IsType<BlackHoleEventSet>(kept.CreateEventSet(Network.Main));

		var custom = Assert.IsType<CustomEventSet>(Custom.CreateEventSet(Network.Main));
		Assert.Equal(Custom.Kinds, custom.Kinds);
		Assert.Null(Custom.Problem);
		Assert.NotNull(new NostrRelaySettings(RelayEventChoice.Custom, KindList.Empty).Problem);
	}
}
