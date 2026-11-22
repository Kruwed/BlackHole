using System.Linq;
using NBitcoin;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// As long as there is no wallet, the application shows the welcome: five columns, Tor, Node, Nostr, Coordinator and
/// Wallet, which the user sets up from left to right. These are the rules of that order, and what the pages of the
/// steps tell.
/// </summary>
public class WelcomeTests
{
	[Fact]
	public void StepsAreTorNodeNostrCoordinatorAndThenTheWallet()
	{
		Assert.Equal(
			[WelcomeStep.Tor, WelcomeStep.Node, WelcomeStep.Nostr, WelcomeStep.Coordinator, WelcomeStep.Wallet],
			Enum.GetValues<WelcomeStep>().Order());

		Assert.Equal(WelcomeStep.Tor, new WelcomeProgress().CurrentStep);
	}

	[Fact]
	public void StepIsOpenOnceTheStepsBeforeItAreDone()
	{
		// At the start only Tor can be set up.
		Assert.True(WelcomeProgress.IsOpen(WelcomeStep.Tor, current: WelcomeStep.Tor));
		Assert.All(
			new[] { WelcomeStep.Node, WelcomeStep.Nostr, WelcomeStep.Coordinator, WelcomeStep.Wallet },
			step => Assert.False(WelcomeProgress.IsOpen(step, current: WelcomeStep.Tor)));

		// With Tor and the node done, Nostr is next. What is done stays open, to be done again.
		Assert.True(WelcomeProgress.IsOpen(WelcomeStep.Tor, current: WelcomeStep.Nostr));
		Assert.True(WelcomeProgress.IsOpen(WelcomeStep.Node, current: WelcomeStep.Nostr));
		Assert.True(WelcomeProgress.IsOpen(WelcomeStep.Nostr, current: WelcomeStep.Nostr));
		Assert.False(WelcomeProgress.IsOpen(WelcomeStep.Coordinator, current: WelcomeStep.Nostr));
		Assert.False(WelcomeProgress.IsOpen(WelcomeStep.Wallet, current: WelcomeStep.Nostr));

		// The wallet comes last: it takes all four.
		Assert.False(WelcomeProgress.IsOpen(WelcomeStep.Wallet, current: WelcomeStep.Coordinator));
		Assert.True(WelcomeProgress.IsOpen(WelcomeStep.Wallet, current: WelcomeStep.Wallet));
	}

	[Fact]
	public void StepIsDoneWhenItsTurnIsOver()
	{
		Assert.False(WelcomeProgress.IsDone(WelcomeStep.Tor, current: WelcomeStep.Tor));
		Assert.True(WelcomeProgress.IsDone(WelcomeStep.Tor, current: WelcomeStep.Node));
		Assert.True(WelcomeProgress.IsDone(WelcomeStep.Coordinator, current: WelcomeStep.Wallet));

		// The wallet ends the welcome: it is never a step that is done while the welcome shows.
		Assert.False(WelcomeProgress.IsDone(WelcomeStep.Wallet, current: WelcomeStep.Wallet));
	}

	[Fact]
	public void ProgressGoesFromStepToStep()
	{
		var progress = new WelcomeProgress();

		progress.Complete(WelcomeStep.Tor);
		Assert.Equal(WelcomeStep.Node, progress.CurrentStep);

		progress.Complete(WelcomeStep.Node);
		Assert.Equal(WelcomeStep.Nostr, progress.CurrentStep);

		progress.Complete(WelcomeStep.Nostr);
		Assert.Equal(WelcomeStep.Coordinator, progress.CurrentStep);

		progress.Complete(WelcomeStep.Coordinator);
		Assert.Equal(WelcomeStep.Wallet, progress.CurrentStep);

		// There is nothing after the wallet.
		progress.Complete(WelcomeStep.Wallet);
		Assert.Equal(WelcomeStep.Wallet, progress.CurrentStep);
	}

	[Fact]
	public void StepThatIsDoneAgainChangesNothing()
	{
		var progress = new WelcomeProgress();
		progress.Complete(WelcomeStep.Tor);
		progress.Complete(WelcomeStep.Node);

		// The user connects to another node, for example.
		progress.Complete(WelcomeStep.Node);
		progress.Complete(WelcomeStep.Tor);

		Assert.Equal(WelcomeStep.Nostr, progress.CurrentStep);
	}

	[Fact]
	public void StepWhoseTurnItIsNotCannotBeSkippedTo()
	{
		var progress = new WelcomeProgress();

		progress.Complete(WelcomeStep.Coordinator);
		progress.Complete(WelcomeStep.Wallet);
		progress.Complete(WelcomeStep.Node);

		Assert.Equal(WelcomeStep.Tor, progress.CurrentStep);
	}

	[Fact]
	public void ColumnTellsWhetherItIsDoneAndWhetherItIsItsTurn()
	{
		var columns = Enum.GetValues<WelcomeStep>().Select(step => new WelcomeColumnViewModel(step)).ToArray();

		Assert.Equal(["Tor", "Node", "Nostr", "Coordinator", "Wallet"], columns.Select(column => column.Title));

		foreach (var column in columns)
		{
			column.Update(WelcomeStep.Nostr);
		}

		Assert.Equal([true, true, false, false, false], columns.Select(column => column.IsDone));
		Assert.Equal([false, false, true, false, false], columns.Select(column => column.IsCurrent));
	}

	[Fact]
	public void TorPageTellsWhereTorComesFrom()
	{
		var own = ConnectToTorViewModel.DescribeUse(TorMode.Enabled, isRegTest: false);
		var running = ConnectToTorViewModel.DescribeUse(TorMode.EnabledOnlyRunning, isRegTest: false);

		Assert.Contains("through Tor", own);
		Assert.Contains("starts a Tor of its own", own);
		Assert.Contains("through Tor", running);
		Assert.Contains("starts none of its own", running);
	}

	[Fact]
	public void TorPageTellsWhenTorIsNotUsed()
	{
		var turnedOff = ConnectToTorViewModel.DescribeUse(TorMode.Disabled, isRegTest: false);
		var regTest = ConnectToTorViewModel.DescribeUse(TorMode.Disabled, isRegTest: true);

		Assert.StartsWith("Tor is turned off in the settings", turnedOff);
		Assert.Contains("see the address of this computer", turnedOff);
		Assert.StartsWith("Tor is not used on regtest", regTest);
	}

	[Fact]
	public void NostrPageWantsOneRelayAtLeast()
	{
		Assert.True(ConnectToNostrViewModel.TryReadRelays("wss://one.example\nws://127.0.0.1:7777/", out var relays, out var problem));
		Assert.Equal(["wss://one.example", "ws://127.0.0.1:7777/"], relays);
		Assert.Null(problem);

		// The settings take an empty list. The welcome connects, and with no relay there is nothing to connect to.
		Assert.False(ConnectToNostrViewModel.TryReadRelays("  \n", out relays, out problem));
		Assert.Empty(relays);
		Assert.Contains("one relay at least", problem);

		Assert.False(ConnectToNostrViewModel.TryReadRelays("wss://one.example\nhttps://two.example", out relays, out problem));
		Assert.Empty(relays);
		Assert.Contains("https://two.example", problem);
	}

	[Fact]
	public void NostrIsConnectedOnceOneRelayAnswers()
	{
		var one = new Uri("wss://one.example");
		var two = new Uri("wss://two.example");

		var (isConnected, message) = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, null)]);
		Assert.True(isConnected);
		Assert.Equal("Connected. Black Hole posts to this relay from now on.", message);

		(isConnected, message) = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, null), new RelayReachability(two, null)]);
		Assert.True(isConnected);
		Assert.Equal("Connected to all 2 relays. Black Hole posts to them from now on.", message);

		// A relay that did not answer this time is told with its reason. It is posted to all the same.
		(isConnected, message) = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, null), new RelayReachability(two, "The relay did not answer in time.")]);
		Assert.True(isConnected);
		Assert.Equal(
			["Connected to 1 of 2 relays. Black Hole posts to all of them from now on.", "wss://two.example/: The relay did not answer in time."],
			message.Split(Environment.NewLine));
	}

	[Fact]
	public void NostrIsNotConnectedWhenNoRelayAnswers()
	{
		var one = new Uri("wss://one.example");
		var two = new Uri("wss://two.example");

		var (isConnected, message) = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, "Connection refused")]);
		Assert.False(isConnected);
		Assert.Equal(["The relay cannot be reached.", "wss://one.example/: Connection refused"], message.Split(Environment.NewLine));

		(isConnected, message) = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, "Connection refused"), new RelayReachability(two, "The relay did not answer in time.")]);
		Assert.False(isConnected);
		Assert.StartsWith("None of the 2 relays can be reached.", message);
		Assert.Equal(3, message.Split(Environment.NewLine).Length);
	}

	[Fact]
	public void CoordinatorPageShowsWhatAnAddressLooksLike()
	{
		// On regtest the coordinator of this repository listens there when it runs on this computer.
		Assert.Equal("http://localhost:37126/", ConnectToCoordinatorViewModel.GetExampleUri(Network.RegTest));

		// Elsewhere there is no coordinator to point at: the example is a made-up one.
		Assert.Equal("https://coordinator.example/", ConnectToCoordinatorViewModel.GetExampleUri(Network.Main));
		Assert.Equal("https://coordinator.example/", ConnectToCoordinatorViewModel.GetExampleUri(Bitcoin.Instance.Signet));
	}
}
