using System.Linq;
using NBitcoin;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Models;
using WalletWasabi.NostrRelay;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The settings are five columns: Tor, Node, Nostr, Coordinator and Wallet. As long as there is no wallet they are
/// the welcome, and the user sets the columns up from left to right. These are the rules of that order, of the marks
/// in the boxes and of the Wallet column, and what the pages of the connections tell.
/// </summary>
public class SettingsScreenTests
{
	private static readonly Connection[] All = [Connection.Tor, Connection.Node, Connection.Nostr, Connection.Coordinator];

	/// <summary>Says that the user is through with these connections, and with no other.</summary>
	private static Func<Connection, bool> Done(params Connection[] connections) => connections.Contains;

	[Fact]
	public void ConnectionsAreTorNodeNostrAndTheCoordinator()
	{
		Assert.Equal(All, SettingsOrder.Connections);
		Assert.Equal(All, Enum.GetValues<Connection>().Order());

		Assert.Equal("Welcome to Black Hole", SettingsPageViewModel.WelcomeTitle);
		Assert.Equal("Settings", SettingsPageViewModel.SettingsTitle);
	}

	[Fact]
	public void OnTheWelcomeItIsTheTurnOfTheFirstConnectionThatIsNotDone()
	{
		Assert.Equal(Connection.Tor, SettingsOrder.GetCurrent(Done()));
		Assert.Equal(Connection.Node, SettingsOrder.GetCurrent(Done(Connection.Tor)));
		Assert.Equal(Connection.Nostr, SettingsOrder.GetCurrent(Done(Connection.Tor, Connection.Node)));
		Assert.Equal(Connection.Coordinator, SettingsOrder.GetCurrent(Done(Connection.Tor, Connection.Node, Connection.Nostr)));

		// With all four done it is the turn of the wallet.
		Assert.Null(SettingsOrder.GetCurrent(Done(All)));

		// What is done further to the right changes nothing about whose turn it is.
		Assert.Equal(Connection.Tor, SettingsOrder.GetCurrent(Done(Connection.Node, Connection.Coordinator)));
	}

	[Fact]
	public void PageOpensOnceTheConnectionsBeforeItAreDone()
	{
		// At the start only Tor can be set up.
		Assert.True(SettingsOrder.IsOpen(Connection.Tor, hasWallet: false, Done()));
		Assert.All(
			new[] { Connection.Node, Connection.Nostr, Connection.Coordinator },
			connection => Assert.False(SettingsOrder.IsOpen(connection, hasWallet: false, Done())));

		// With Tor and the node done, Nostr is next. What is done stays open, to be done again.
		var done = Done(Connection.Tor, Connection.Node);

		Assert.True(SettingsOrder.IsOpen(Connection.Tor, hasWallet: false, done));
		Assert.True(SettingsOrder.IsOpen(Connection.Node, hasWallet: false, done));
		Assert.True(SettingsOrder.IsOpen(Connection.Nostr, hasWallet: false, done));
		Assert.False(SettingsOrder.IsOpen(Connection.Coordinator, hasWallet: false, done));

		// A connection that is done can be opened whatever stands before it.
		Assert.True(SettingsOrder.IsOpen(Connection.Nostr, hasWallet: false, Done(Connection.Nostr)));
		Assert.False(SettingsOrder.IsOpen(Connection.Node, hasWallet: false, Done(Connection.Nostr)));
	}

	[Fact]
	public void WithAWalletEveryPageOpens()
	{
		Assert.All(All, connection => Assert.True(SettingsOrder.IsOpen(connection, hasWallet: true, Done())));
	}

	[Fact]
	public void WalletIsAddedOnceTheFourConnectionsAreDone()
	{
		Assert.True(SettingsOrder.CanAddWallet(hasWallet: false, Done(All)));

		Assert.False(SettingsOrder.CanAddWallet(hasWallet: false, Done()));
		Assert.False(SettingsOrder.CanAddWallet(hasWallet: false, Done(Connection.Tor, Connection.Node, Connection.Nostr)));
		Assert.False(SettingsOrder.CanAddWallet(hasWallet: false, Done(Connection.Node, Connection.Nostr, Connection.Coordinator)));

		// There is one wallet at most.
		Assert.False(SettingsOrder.CanAddWallet(hasWallet: true, Done(All)));
	}

	[Fact]
	public void TorThatIsNotUsedIsDone()
	{
		Assert.True(ConnectionsModel.IsDone(isConfigured: true, isUsed: true));
		Assert.False(ConnectionsModel.IsDone(isConfigured: false, isUsed: true));

		// Tor is turned off in the settings, or the application is on regtest: there is nothing to connect to.
		Assert.True(ConnectionsModel.IsDone(isConfigured: false, isUsed: false));
	}

	/// <summary>Creating a wallet is the upper box of the Wallet column, and recovering one the lower.</summary>
	[Fact]
	public void WalletColumnCreatesAndRecoversUntilThereIsAWallet()
	{
		Assert.Equal((WalletTile.Create, WalletTile.Recover), SettingsOrder.GetWalletTiles(hasWallet: false, isRecovered: false));
		Assert.Equal((WalletTile.Create, WalletTile.Recover), SettingsOrder.GetWalletTiles(hasWallet: false, isRecovered: true));
	}

	[Fact]
	public void BoxThatMadeTheWalletConfiguresIt()
	{
		// A wallet that was created: the upper box, where "Create a Wallet" was.
		Assert.Equal((WalletTile.Configure, WalletTile.Recover), SettingsOrder.GetWalletTiles(hasWallet: true, isRecovered: false));

		// A wallet that was recovered: the lower box, where "Recover a Wallet" was.
		Assert.Equal((WalletTile.Create, WalletTile.Configure), SettingsOrder.GetWalletTiles(hasWallet: true, isRecovered: true));
	}

	[Fact]
	public void ConnectionIsConfiguredOnceTheUserConnectedToIt()
	{
		Assert.True(ConnectionsModel.IsConfigured(hasTarget: true, wasConnectedByUser: true, hasWallet: false));

		// What the settings name by themselves, the relays of a new installation for example, is not configured yet.
		Assert.False(ConnectionsModel.IsConfigured(hasTarget: true, wasConnectedByUser: false, hasWallet: false));

		// With a wallet, what the settings name counts: the data folder may be older than the note of what was connected.
		Assert.True(ConnectionsModel.IsConfigured(hasTarget: true, wasConnectedByUser: false, hasWallet: true));

		// Where there is nothing to connect to, nothing is configured: no coordinator, or a Tor that is not used.
		Assert.False(ConnectionsModel.IsConfigured(hasTarget: false, wasConnectedByUser: true, hasWallet: true));
		Assert.False(ConnectionsModel.IsConfigured(hasTarget: false, wasConnectedByUser: false, hasWallet: false));
	}

	[Fact]
	public void ConfiguredConnectionWorksOrDoesNot()
	{
		Assert.Equal(ConnectionState.Working, ConnectionsModel.GetState(isConfigured: true, works: true));
		Assert.Equal(ConnectionState.NotWorking, ConnectionsModel.GetState(isConfigured: true, works: false));

		// Nobody has looked yet.
		Assert.Equal(ConnectionState.Unknown, ConnectionsModel.GetState(isConfigured: true, works: null));

		// Whether something works that the user has not configured is not told.
		Assert.Equal(ConnectionState.NotConfigured, ConnectionsModel.GetState(isConfigured: false, works: true));
		Assert.Equal(ConnectionState.NotConfigured, ConnectionsModel.GetState(isConfigured: false, works: false));
		Assert.Equal(ConnectionState.NotConfigured, ConnectionsModel.GetState(isConfigured: false, works: null));
	}

	[Fact]
	public void BoxHasACheckMarkWhenItsConnectionWorksAndAnXWhenItDoesNot()
	{
		var column = new ConnectionColumnViewModel(Connection.Nostr);
		Assert.Equal("Nostr", column.Title);

		column.Update(ConnectionState.Working, isOpen: true, isCurrent: false);
		Assert.True(column.IsWorking);
		Assert.False(column.IsNotWorking);

		column.Update(ConnectionState.NotWorking, isOpen: true, isCurrent: false);
		Assert.False(column.IsWorking);
		Assert.True(column.IsNotWorking);

		// No mark: the connection is not configured, or it is not known yet whether it works.
		foreach (var state in new[] { ConnectionState.NotConfigured, ConnectionState.Unknown })
		{
			column.Update(state, isOpen: true, isCurrent: true);
			Assert.False(column.IsWorking);
			Assert.False(column.IsNotWorking);
			Assert.True(column.IsCurrent);
		}

		Assert.Equal(["Tor", "Node", "Nostr", "Coordinator"], All.Select(connection => new ConnectionColumnViewModel(connection).Title));
	}

	[Fact]
	public void TorThatIsStillStartingNeitherWorksNorFails()
	{
		var justStarted = TimeSpan.FromSeconds(10);
		var longAgo = ConnectionsModel.TorStartGrace + TimeSpan.FromSeconds(1);

		Assert.True(ConnectionsModel.JudgeTor(isConnected: true, wasConnectedBefore: false, justStarted));
		Assert.True(ConnectionsModel.JudgeTor(isConnected: true, wasConnectedBefore: true, longAgo));

		// It takes Tor a while to connect to the Tor network after the application started.
		Assert.Null(ConnectionsModel.JudgeTor(isConnected: false, wasConnectedBefore: false, justStarted));

		// After that while, or once it was connected, a Tor that is not connected does not work.
		Assert.False(ConnectionsModel.JudgeTor(isConnected: false, wasConnectedBefore: false, longAgo));
		Assert.False(ConnectionsModel.JudgeTor(isConnected: false, wasConnectedBefore: true, justStarted));
	}

	[Fact]
	public void RelaysAndCoordinatorAreAskedAgainOnceTheAnswerIsOld()
	{
		var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
		var maxAge = TimeSpan.FromSeconds(60);

		Assert.True(ConnectionsModel.IsCheckDue(checkedAt: null, now, maxAge));
		Assert.False(ConnectionsModel.IsCheckDue(now - TimeSpan.FromSeconds(59), now, maxAge));
		Assert.True(ConnectionsModel.IsCheckDue(now - TimeSpan.FromSeconds(60), now, maxAge));
		Assert.True(ConnectionsModel.IsCheckDue(now - TimeSpan.FromMinutes(5), now, maxAge));
	}

	/// <summary>The button that connects is called "Done" from the start: it connects, and the page closes when that worked.</summary>
	[Fact]
	public void PagesHaveAButtonThatOnlyAsksAndOneThatConnectsAndCloses()
	{
		Assert.Equal("Done", ConnectionPageViewModel.DoneLabel);

		// One press connects first, unless the application is connected already.
		Assert.True(ConnectionPageViewModel.ConnectsFirst(isConnected: false, hasSomethingToConnectTo: true));
		Assert.False(ConnectionPageViewModel.ConnectsFirst(isConnected: true, hasSomethingToConnectTo: true));

		// Then the page closes, if that worked.
		Assert.True(ConnectionPageViewModel.ClosesWhenDone(isConnected: true, hasSomethingToConnectTo: true, isShown: true));
	}

	/// <summary>What does not answer keeps its page open: the page tells why, and the user can change what it names.</summary>
	[Fact]
	public void PageStaysOpenWhenConnectingDidNotWork()
	{
		Assert.False(ConnectionPageViewModel.ClosesWhenDone(isConnected: false, hasSomethingToConnectTo: true, isShown: true));
	}

	/// <summary>The user went back while the answer was awaited: the page is closed, and no other page is closed for it.</summary>
	[Fact]
	public void PageThatWasLeftClosesNothing()
	{
		Assert.False(ConnectionPageViewModel.ClosesWhenDone(isConnected: true, hasSomethingToConnectTo: true, isShown: false));
		Assert.False(ConnectionPageViewModel.ClosesWhenDone(isConnected: false, hasSomethingToConnectTo: false, isShown: false));
	}

	/// <summary>Tor that is turned off, or regtest: there is nothing to connect to, and "Done" closes the page.</summary>
	[Fact]
	public void DoneOnlyClosesAPageWithNothingToConnectTo()
	{
		Assert.True(ConnectionPageViewModel.CanBeDone(canAsk: false, isAsking: false, isConnected: false, hasSomethingToConnectTo: false));
		Assert.False(ConnectionPageViewModel.ConnectsFirst(isConnected: false, hasSomethingToConnectTo: false));
		Assert.True(ConnectionPageViewModel.ClosesWhenDone(isConnected: false, hasSomethingToConnectTo: false, isShown: true));
	}

	[Fact]
	public void DoneCannotBePressedWhileThereIsNothingToAskOrAnAnswerIsAwaited()
	{
		// An address that is none, or no address at all.
		Assert.False(ConnectionPageViewModel.CanBeDone(canAsk: false, isAsking: false, isConnected: false, hasSomethingToConnectTo: true));

		// Something to ask.
		Assert.True(ConnectionPageViewModel.CanBeDone(canAsk: true, isAsking: false, isConnected: false, hasSomethingToConnectTo: true));

		// An answer is being waited for.
		Assert.False(ConnectionPageViewModel.CanBeDone(canAsk: true, isAsking: true, isConnected: false, hasSomethingToConnectTo: true));
		Assert.False(ConnectionPageViewModel.CanBeDone(canAsk: false, isAsking: true, isConnected: false, hasSomethingToConnectTo: false));

		// Connected already: the press closes the page.
		Assert.True(ConnectionPageViewModel.CanBeDone(canAsk: false, isAsking: false, isConnected: true, hasSomethingToConnectTo: true));
	}

	/// <summary>While Tor is used, the Tor page has the choice of how and its two buttons, and no text about it.</summary>
	[Fact]
	public void TorPageSaysNothingAboutATorThatIsUsed()
	{
		Assert.Null(ConnectToTorViewModel.DescribeUse(TorMode.Enabled, isRegTest: false));
		Assert.Null(ConnectToTorViewModel.DescribeUse(TorMode.EnabledOnlyRunning, isRegTest: false));
	}

	/// <summary>
	/// Verify Connection on the Tor page answers with the number of unique Tor IP addresses that Tor is connected with:
	/// the addresses of the relays of its circuits.
	/// </summary>
	[Fact]
	public void TorPageAnswersWithTheNumberOfUniqueTorAddresses()
	{
		Assert.Equal("Tor is connected with 14 unique Tor IP addresses.", ConnectToTorViewModel.DescribeConnection(14));
		Assert.Equal("Tor is connected with 2 unique Tor IP addresses.", ConnectToTorViewModel.DescribeConnection(2));
		Assert.Equal("Tor is connected with 1 unique Tor IP address.", ConnectToTorViewModel.DescribeConnection(1));

		// Connected, and no circuit open at this moment.
		Assert.Equal("Tor is connected with 0 unique Tor IP addresses: it has no circuit open at the moment.", ConnectToTorViewModel.DescribeConnection(0));

		// A Tor that the application did not start tells nothing about its circuits.
		Assert.Equal("Tor is connected.", ConnectToTorViewModel.DescribeConnection(null));
	}

	[Fact]
	public void TorPageTellsWhenTorIsNotUsed()
	{
		var turnedOff = ConnectToTorViewModel.DescribeUse(TorMode.Disabled, isRegTest: false);
		var regTest = ConnectToTorViewModel.DescribeUse(TorMode.Disabled, isRegTest: true);

		Assert.StartsWith("Tor is turned off", turnedOff);
		Assert.Contains("see the address of this computer", turnedOff);
		Assert.StartsWith("Tor is not used on regtest", regTest);
	}

	[Fact]
	public void NostrPageWantsOneRelayAtLeast()
	{
		Assert.True(ConnectToNostrViewModel.TryReadRelays("wss://one.example\nws://127.0.0.1:7777/", out var relays, out var problem));
		Assert.Equal(["wss://one.example", "ws://127.0.0.1:7777/"], relays);
		Assert.Null(problem);

		// With no relay there is nothing to ask and nothing to connect to.
		Assert.False(ConnectToNostrViewModel.TryReadRelays("  \n", out relays, out problem));
		Assert.Empty(relays);
		Assert.Contains("one relay at least", problem);

		Assert.False(ConnectToNostrViewModel.TryReadRelays("wss://one.example\nhttps://two.example", out relays, out problem));
		Assert.Empty(relays);
		Assert.Contains("https://two.example", problem);
	}

	[Fact]
	public void NostrAnswersWhenOneRelayDoes()
	{
		var one = new Uri("wss://one.example");
		var two = new Uri("wss://two.example");

		var answer = ConnectToNostrViewModel.DescribeAnswers([new RelayReachability(one, null)]);
		Assert.True(answer.IsGood);
		Assert.Equal("The relay answers.", answer.Message);

		answer = ConnectToNostrViewModel.DescribeAnswers([new RelayReachability(one, null), new RelayReachability(two, null)]);
		Assert.True(answer.IsGood);
		Assert.Equal("All 2 relays answer.", answer.Message);

		// A relay that does not answer is told with its reason.
		answer = ConnectToNostrViewModel.DescribeAnswers([new RelayReachability(one, null), new RelayReachability(two, "The relay did not answer in time.")]);
		Assert.True(answer.IsGood);
		Assert.Equal(["1 of 2 relays answer.", "wss://two.example/: The relay did not answer in time."], answer.Message.Split(Environment.NewLine));

		answer = ConnectToNostrViewModel.DescribeAnswers([new RelayReachability(one, "Connection refused")]);
		Assert.False(answer.IsGood);
		Assert.Equal(["The relay cannot be reached.", "wss://one.example/: Connection refused"], answer.Message.Split(Environment.NewLine));

		// Asking is not connecting: the answer promises nothing about where offers go.
		Assert.DoesNotContain("from now on", ConnectToNostrViewModel.DescribeAnswers([new RelayReachability(one, null)]).Message);
	}

	[Fact]
	public void NostrIsConnectedOnceOneRelayAnswers()
	{
		var one = new Uri("wss://one.example");
		var two = new Uri("wss://two.example");

		var answer = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, null)]);
		Assert.True(answer.IsGood);
		Assert.Equal("Connected. Black Hole posts to this relay from now on.", answer.Message);

		answer = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, null), new RelayReachability(two, null)]);
		Assert.True(answer.IsGood);
		Assert.Equal("Connected to all 2 relays. Black Hole posts to them from now on.", answer.Message);

		// A relay that did not answer this time is told with its reason. It is posted to all the same.
		answer = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, null), new RelayReachability(two, "The relay did not answer in time.")]);
		Assert.True(answer.IsGood);
		Assert.Equal(
			["Connected to 1 of 2 relays. Black Hole posts to all of them from now on.", "wss://two.example/: The relay did not answer in time."],
			answer.Message.Split(Environment.NewLine));
	}

	[Fact]
	public void NostrIsNotConnectedWhenNoRelayAnswers()
	{
		var one = new Uri("wss://one.example");
		var two = new Uri("wss://two.example");

		var answer = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, "Connection refused")]);
		Assert.False(answer.IsGood);
		Assert.Equal(["The relay cannot be reached.", "wss://one.example/: Connection refused"], answer.Message.Split(Environment.NewLine));

		answer = ConnectToNostrViewModel.DescribeConnection([new RelayReachability(one, "Connection refused"), new RelayReachability(two, "The relay did not answer in time.")]);
		Assert.False(answer.IsGood);
		Assert.StartsWith("None of the 2 relays can be reached.", answer.Message);
		Assert.Equal(3, answer.Message.Split(Environment.NewLine).Length);
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

	/// <summary>
	/// The box "Run a Nostr Relay" has the marks of the boxes that connect: nothing while the relay is off or on its
	/// way, a check mark while it runs, and an X while it is on and does not run.
	/// </summary>
	[Fact]
	public void BoxOfTheRelayHasTheMarkOfHowItStandsWithTheRelay()
	{
		var address = new Uri("ws://relayaddress.onion");

		Assert.Equal(ConnectionState.NotConfigured, ConnectionsModel.GetState(NostrRelayState.Off));
		Assert.Equal(ConnectionState.Unknown, ConnectionsModel.GetState(new NostrRelayState(NostrRelayStatus.Starting)));
		Assert.Equal(ConnectionState.Unknown, ConnectionsModel.GetState(new NostrRelayState(NostrRelayStatus.Publishing, address)));
		Assert.Equal(ConnectionState.Working, ConnectionsModel.GetState(new NostrRelayState(NostrRelayStatus.Running, address)));
		Assert.Equal(ConnectionState.NotWorking, ConnectionsModel.GetState(new NostrRelayState(NostrRelayStatus.NotWorking, Problem: "Tor is turned off.")));
	}

	/// <summary>The user of a relay switches between two sets of events: the events of Black Hole, and a list of their own.</summary>
	[Fact]
	public void RelayPageOffersTheEventsOfBlackHoleAndACustomList()
	{
		Assert.Equal(
			[(RelayEventChoice.BlackHole, "Black Hole offers and coordinators"), (RelayEventChoice.Custom, "Custom list")],
			RunNostrRelayViewModel.EventOptions.Select(option => (option.Choice, option.Name)));
		Assert.Equal("Custom list", RunNostrRelayViewModel.EventOptions[1].ToString());

		Assert.Equal(
			"The relay keeps offers for 15 minutes and coordinator announcements for 30, in memory only. Nothing else is taken.",
			RunNostrRelayViewModel.DescribeEvents(RelayEventChoice.BlackHole));
		Assert.Equal(
			"The relay keeps every event of these kinds that anybody sends it, in a file on this computer, up to 100 MB. Then the oldest events make way.",
			RunNostrRelayViewModel.DescribeEvents(RelayEventChoice.Custom));
	}

	/// <summary>
	/// A custom list names one kind of event at least. While the relay takes the events of Black Hole the list is kept
	/// as far as it can be read, and nothing that is wrong with it is in the way.
	/// </summary>
	[Fact]
	public void RelayPageWantsAKindOfEventForACustomList()
	{
		Assert.True(RunNostrRelayViewModel.TryReadSettings(RelayEventChoice.Custom, "30023, 1 7", out var settings, out var problem));
		Assert.Null(problem);
		Assert.Equal(RelayEventChoice.Custom, settings.Events);
		Assert.Equal("1, 7, 30023", settings.Kinds.ToString());

		Assert.False(RunNostrRelayViewModel.TryReadSettings(RelayEventChoice.Custom, "", out _, out problem));
		Assert.Equal("Name one kind of event at least for the relay to keep, like 1.", problem);

		Assert.False(RunNostrRelayViewModel.TryReadSettings(RelayEventChoice.Custom, "notes", out _, out problem));
		Assert.Contains("'notes' is not a kind of event", problem, StringComparison.Ordinal);

		Assert.True(RunNostrRelayViewModel.TryReadSettings(RelayEventChoice.BlackHole, "", out settings, out problem));
		Assert.Null(problem);
		Assert.Equal(NostrRelaySettings.Default, settings);

		Assert.True(RunNostrRelayViewModel.TryReadSettings(RelayEventChoice.BlackHole, "1, 30023", out settings, out _));
		Assert.Equal("1, 30023", settings.Kinds.ToString());

		Assert.True(RunNostrRelayViewModel.TryReadSettings(RelayEventChoice.BlackHole, "notes", out settings, out problem));
		Assert.Null(problem);
		Assert.True(settings.Kinds.IsEmpty);
	}

	/// <summary>
	/// "Done" makes the relay run and closes the page once it does. A relay that does not run yet stays on and goes
	/// on trying, and the page stays open and tells how far it is, or why it cannot run.
	/// </summary>
	[Fact]
	public void RelayPageTellsHowItWentWhenTheRelayWasMadeToRun()
	{
		var address = new Uri("ws://relayaddress.onion");

		Assert.Equal(new ConnectionAnswer(true, "The relay runs."), RunNostrRelayViewModel.DescribeRun(new NostrRelayState(NostrRelayStatus.Running, address)));

		var publishing = RunNostrRelayViewModel.DescribeRun(new NostrRelayState(NostrRelayStatus.Publishing, address));
		Assert.False(publishing.IsGood);
		Assert.Equal("The relay is on, and Tor has not published its onion address yet. That can take a few minutes: the relay goes on trying.", publishing.Message);

		var starting = RunNostrRelayViewModel.DescribeRun(new NostrRelayState(NostrRelayStatus.Starting));
		Assert.False(starting.IsGood);
		Assert.Equal("The relay is on, and Tor has not given it its onion address yet. The relay goes on trying.", starting.Message);

		Assert.Equal(
			new ConnectionAnswer(false, "Tor did not make the onion service (550 Onion address collision)."),
			RunNostrRelayViewModel.DescribeRun(new NostrRelayState(NostrRelayStatus.NotWorking, Problem: "Tor did not make the onion service (550 Onion address collision).")));
		Assert.Equal(new ConnectionAnswer(false, "The relay is off."), RunNostrRelayViewModel.DescribeRun(NostrRelayState.Off));
	}

	/// <summary>
	/// The page tells how it stands with the relay also when nobody asked: in the colour of good news while it runs,
	/// of bad news while it cannot, and of the other words while it is on its way. About a relay that is off it tells
	/// nothing. The address shows while there is one.
	/// </summary>
	[Fact]
	public void RelayPageTellsHowItStandsWithTheRelay()
	{
		var address = new Uri("ws://relayaddress.onion");

		Assert.Equal(("The relay runs.", true), RunNostrRelayViewModel.DescribeState(new NostrRelayState(NostrRelayStatus.Running, address)));
		Assert.Equal(("Tor is publishing the onion address of the relay…", null), RunNostrRelayViewModel.DescribeState(new NostrRelayState(NostrRelayStatus.Publishing, address)));
		Assert.Equal(("The relay waits for Tor to give it its onion address…", null), RunNostrRelayViewModel.DescribeState(new NostrRelayState(NostrRelayStatus.Starting)));
		Assert.Equal(("Tor is turned off.", false), RunNostrRelayViewModel.DescribeState(new NostrRelayState(NostrRelayStatus.NotWorking, Problem: "Tor is turned off.")));
		Assert.Equal((null, null), RunNostrRelayViewModel.DescribeState(NostrRelayState.Off));

		Assert.Equal("ws://relayaddress.onion", RunNostrRelayViewModel.FormatAddress(new NostrRelayState(NostrRelayStatus.Running, address)));
		Assert.Equal("ws://relayaddress.onion", RunNostrRelayViewModel.FormatAddress(new NostrRelayState(NostrRelayStatus.Publishing, address)));
		Assert.Equal("", RunNostrRelayViewModel.FormatAddress(new NostrRelayState(NostrRelayStatus.Starting)));
		Assert.Equal("", RunNostrRelayViewModel.FormatAddress(NostrRelayState.Off));
	}
}
