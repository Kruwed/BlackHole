using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using WalletWasabi.NostrRelay;
using WalletWasabi.Services;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>One of the two sets of events that the user of a relay switches between, with the name that the page has for it.</summary>
public sealed record RelayEventOption(RelayEventChoice Choice, string Name)
{
	public override string ToString() => Name;
}

/// <summary>
/// Where the user runs a Nostr relay of their own, inside the application. The relay is reached at an onion address
/// and nowhere else, so nobody who uses it learns where this computer is. The user switches it on and off, and
/// between the two sets of events that it takes: the events of Black Hole, and a list of kinds that the user names.
/// The user also says whose events it takes: with one filter that asks for a fidelity bond of a size that the user
/// sets, and one that names keys which need no bond.
/// </summary>
/// <remarks>
/// The page has the two buttons of the pages that connect. "Done" makes the relay what the page says, saves that,
/// and closes the page once the relay runs, or is off. A relay that does not run yet stays on and goes on trying,
/// and the page stays open and tells why. "Verify Connection" asks the relay at its onion address, through Tor, the
/// way anybody else reaches it.
/// </remarks>
[NavigationMetaData(Title = "Run a Nostr Relay")]
public partial class RunNostrRelayViewModel : ConnectionPageViewModel
{
	public const string RunsMessage = "The relay runs.";
	public const string OffMessage = "The relay is off.";
	public const string AnswersMessage = "The relay answers at its onion address.";

	/// <summary>Whether the relay is to run.</summary>
	[AutoNotify] private bool _isRelayOn;

	/// <summary>Which of the two sets of events the relay takes.</summary>
	[AutoNotify] private RelayEventOption _selectedEvents;

	/// <summary>The kinds of events that the user names, as the user writes them.</summary>
	[AutoNotify] private string _kinds;

	/// <summary>Whether the relay takes events only from keys with a fidelity bond behind them, and from the keys of the list.</summary>
	[AutoNotify] private bool _isBondFilterOn;

	/// <summary>The least that a fidelity bond has to hold, in bitcoin, as the user writes it.</summary>
	[AutoNotify] private string _minBond;

	/// <summary>The keys that need no bond, as the user writes them: npubs, one on a line.</summary>
	[AutoNotify] private string _whitelist;

	/// <summary>The address of the relay, while it has one. Nothing otherwise.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _address = "";

	/// <summary>What was saved for the relay when the page was opened.</summary>
	private readonly NostrRelaySettings _saved;

	public RunNostrRelayViewModel(UiContext uiContext) : base(uiContext, connection: null)
	{
		var services = uiContext.Services;

		Obstacle = services.GetNostrRelayObstacle();
		IsReadOnly = Settings.IsNostrRelayOverridden;

		// What was given at the start is not in the saved settings. It is shown, because it is what the relay runs with.
		_saved = IsReadOnly ? services.Config.NostrRelaySettings : Settings.NostrRelaySettings;

		_isRelayOn = IsReadOnly ? services.Config.RunNostrRelay : Settings.RunNostrRelay;
		_selectedEvents = EventOptions.First(option => option.Choice == _saved.Events);
		_kinds = _saved.Kinds.ToString();
		_isBondFilterOn = _saved.Senders.IsBondRequired;
		_minBond = NostrRelaySettings.FormatBond(_saved.Senders.MinBond);
		_whitelist = string.Join(Environment.NewLine, _saved.Senders.WhitelistToNpubs());

		this.ValidateProperty(x => x.Kinds, ValidateKinds);
		this.ValidateProperty(x => x.MinBond, ValidateMinBond);
		this.ValidateProperty(x => x.Whitelist, ValidateWhitelist);

		// What the relay answered says nothing about a relay that is to be otherwise.
		this.WhenAnyValue(x => x.IsRelayOn, x => x.SelectedEvents, x => x.Kinds, x => x.IsBondFilterOn, x => x.MinBond, x => x.Whitelist, (_, _, _, _, _, _) => System.Reactive.Unit.Default)
			.Skip(1)
			.Subscribe(_ => ForgetAnswer());

		this.WhenAnyValue(x => x.IsBondFilterOn, x => x.SelectedEvents)
			.Subscribe(x =>
			{
				CanSetMinBond = CanChooseEvents && x.Item1;
				SendersDescription = DescribeSenders(x.Item1, x.Item2.Choice);
			});

		// A minimum that is not asked for has nothing wrong with it that would be in the way.
		this.WhenAnyValue(x => x.IsBondFilterOn)
			.Skip(1)
			.Subscribe(_ => this.RaisePropertyChanged(nameof(MinBond)));

		this.WhenAnyValue(x => x.SelectedEvents)
			.Subscribe(events =>
			{
				IsCustomListChosen = CanChooseEvents && events.Choice == RelayEventChoice.Custom;
				EventsDescription = DescribeEvents(events.Choice);
			});

		// A list that is not the choice, or of a relay that is off, has nothing wrong with it that would be in the way.
		this.WhenAnyValue(x => x.SelectedEvents, x => x.IsRelayOn, (_, _) => System.Reactive.Unit.Default)
			.Skip(1)
			.Subscribe(_ => this.RaisePropertyChanged(nameof(Kinds)));

		SetUpCommands(
			canAsk: this.WhenAnyValue(x => x.Address, address => address.Length > 0),
			hasSomethingToConnectTo: !IsReadOnly,
			canConnect: this.WhenAnyValue(
				x => x.IsRelayOn,
				x => x.SelectedEvents,
				x => x.Kinds,
				x => x.IsBondFilterOn,
				x => x.MinBond,
				x => x.Whitelist,
				(isOn, events, kinds, isBondFilterOn, minBond, whitelist) =>
					TryReadSenders(isBondFilterOn, minBond, whitelist, _saved.Senders, out _, out _, out _)
					&& (!isOn || TryReadSettings(events.Choice, kinds, out _, out _))));
	}


	/// <summary>The two sets of events, in the order in which the page offers them.</summary>
	public static IReadOnlyList<RelayEventOption> EventOptions { get; } =
	[
		new(RelayEventChoice.BlackHole, $"{Constants.AppName} offers and coordinators"),
		new(RelayEventChoice.Custom, "Custom list")
	];

	/// <summary>Why the application cannot run a relay as it was started. Null if it can.</summary>
	public string? Obstacle { get; }

	/// <summary>
	/// Whether the relay was given on the command line or in the environment, so that what the page saved would have no
	/// effect the next time.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>Whether the user can switch the relay: on only where one can run, and off always.</summary>
	public bool CanSwitch => !IsReadOnly && (Obstacle is null || Settings.RunNostrRelay);

	/// <summary>Whether the user can choose the events of the relay.</summary>
	public bool CanChooseEvents => !IsReadOnly && Obstacle is null;

	/// <summary>Whether the list of the user's own is the choice, so that it can be written into.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isCustomListChosen;

	/// <summary>What the chosen set of events means for whoever runs the relay.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _eventsDescription = "";

	/// <summary>Whether the least that a bond has to hold can be written: while a bond is asked for.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _canSetMinBond;

	/// <summary>What the two filters mean for whoever runs the relay.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _sendersDescription = "";

	/// <summary>
	/// Reads what the user chose for the relay.
	/// </summary>
	/// <param name="problem">What is wrong with it, if a relay cannot run with it.</param>
	public static bool TryReadSettings(RelayEventChoice events, string kinds, out NostrRelaySettings settings, out string? problem)
	{
		// The list counts while it is the choice. While it is not, it is kept as far as it can be read.
		if (!KindList.TryParse(kinds, out var kindList, out problem))
		{
			settings = new NostrRelaySettings(events, KindList.Empty, RelaySenderRules.Anyone);
			problem = events == RelayEventChoice.Custom ? problem : null;
			return problem is null;
		}

		settings = new NostrRelaySettings(events, kindList, RelaySenderRules.Anyone);
		problem = settings.Problem;
		return problem is null;
	}

	/// <summary>
	/// Reads the two filters that the user set for whose events the relay takes.
	/// </summary>
	/// <param name="minBond">The least that a bond has to hold, in bitcoin, as the user wrote it. It counts while a bond is asked for. While none is, it is kept as far as it can be read.</param>
	/// <param name="whitelist">The keys that need no bond, as the user wrote them.</param>
	/// <param name="saved">The filters as they were saved, for what does not count now and cannot be read.</param>
	/// <param name="minBondProblem">What is wrong with the amount, if it cannot be read.</param>
	/// <param name="whitelistProblem">What is wrong with the list, if it cannot be read.</param>
	public static bool TryReadSenders(bool isBondRequired, string minBond, string whitelist, RelaySenderRules saved, out RelaySenderRules rules, out string? minBondProblem, out string? whitelistProblem)
	{
		long sats;

		if (!NostrRelaySettings.TryParseBond(minBond, out sats, out minBondProblem))
		{
			sats = saved.MinBond;
			minBondProblem = isBondRequired ? minBondProblem : null;
		}

		if (!RelaySenderRules.TryParseWhitelist(whitelist, out var keys, out whitelistProblem))
		{
			keys = saved.Whitelist;
		}

		rules = new RelaySenderRules(isBondRequired, sats, keys);
		return minBondProblem is null && whitelistProblem is null;
	}

	/// <summary>Tells what a set of events means for whoever runs the relay: what is taken, and how long it is kept.</summary>
	public static string DescribeEvents(RelayEventChoice events) =>
		events == RelayEventChoice.Custom
			? $"The relay takes every event of these kinds, and keeps each for {RelayEventSet.Retention.TotalHours:0} hours."
			: $"The relay takes offers and coordinator announcements, and keeps each for {RelayEventSet.Retention.TotalHours:0} hours. Nothing else is taken.";

	/// <summary>Tells what the two filters mean for whoever runs the relay: who can post, and what makes way when the relay is full.</summary>
	public static string DescribeSenders(bool isBondRequired, RelayEventChoice events)
	{
		var limits = events == RelayEventChoice.Custom ? RelayLimits.ForCustomEvents : RelayLimits.ForBlackHoleEvents;
		var room = $"{limits.MaxStoredEvents:N0} events or {limits.MaxStoredBytes / (1024 * 1024)} MB";

		return isBondRequired
			? $"Only keys with a proven fidelity bond of this size or more can post, and the keys of the whitelist. A bond that several keys prove is divided evenly among them. Bonds are looked up with your node. When the relay is full ({room}), the events of the smallest bonds make way first."
			: $"Anybody can post. When the relay is full ({room}), the events of the smallest bonds make way first, and the events of the whitelist last.";
	}

	/// <summary>Tells how it went when the relay was made to run.</summary>
	public static ConnectionAnswer DescribeRun(NostrRelayState state) =>
		state.Status switch
		{
			NostrRelayStatus.Running => new ConnectionAnswer(true, RunsMessage),
			NostrRelayStatus.Publishing => new ConnectionAnswer(false, "The relay is on, and Tor has not published its onion address yet. That can take a few minutes: the relay goes on trying."),
			NostrRelayStatus.Starting => new ConnectionAnswer(false, "The relay is on, and Tor has not given it its onion address yet. The relay goes on trying."),
			NostrRelayStatus.NotWorking => new ConnectionAnswer(false, state.Problem ?? "The relay does not run."),
			_ => new ConnectionAnswer(false, OffMessage)
		};

	/// <summary>Tells how it stands with the relay at a moment at which nobody asked.</summary>
	/// <returns>What to tell, and whether it is good news or bad: null if it is neither. Nothing is told about a relay that is off.</returns>
	public static (string? Message, bool? IsGood) DescribeState(NostrRelayState state) =>
		state.Status switch
		{
			NostrRelayStatus.Running => (RunsMessage, true),
			NostrRelayStatus.Publishing => ("Tor is publishing the onion address of the relay…", null),
			NostrRelayStatus.Starting => ("The relay waits for Tor to give it its onion address…", null),
			NostrRelayStatus.NotWorking => (state.Problem ?? "The relay does not run.", false),
			_ => (null, null)
		};

	/// <summary>The address of a relay the way the page shows it. Nothing for a relay without one.</summary>
	public static string FormatAddress(NostrRelayState state) =>
		state.Address is { } address ? NostrRelayState.Format(address) : "";

	private void ValidateKinds(IValidationErrors errors)
	{
		if (IsRelayOn && !TryReadSettings(SelectedEvents.Choice, Kinds, out _, out var problem) && problem is not null)
		{
			errors.Add(ErrorSeverity.Error, problem);
		}
	}

	private void ValidateMinBond(IValidationErrors errors)
	{
		if (!TryReadSenders(IsBondFilterOn, MinBond, Whitelist, _saved.Senders, out _, out var problem, out _) && problem is not null)
		{
			errors.Add(ErrorSeverity.Error, problem);
		}
	}

	private void ValidateWhitelist(IValidationErrors errors)
	{
		if (!TryReadSenders(IsBondFilterOn, MinBond, Whitelist, _saved.Senders, out _, out _, out var problem) && problem is not null)
		{
			errors.Add(ErrorSeverity.Error, problem);
		}
	}

	protected override string GetWaitingMessage(bool isConnecting) =>
		isConnecting
			? IsRelayOn ? "Starting the relay, and waiting for Tor to publish its onion address…" : "Stopping the relay…"
			: "Asking the relay at its onion address…";

	protected override async Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken)
	{
		var result = await UiContext.Services.VerifyNostrRelayAsync(cancellationToken);

		return result.IsOk
			? new ConnectionAnswer(true, AnswersMessage)
			: new ConnectionAnswer(false, result.Error);
	}

	protected override async Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken)
	{
		var services = UiContext.Services;

		// The filters count whether the relay is on or off: what cannot be read is not saved.
		if (!TryReadSenders(IsBondFilterOn, MinBond, Whitelist, _saved.Senders, out var senders, out var minBondProblem, out var whitelistProblem))
		{
			return new ConnectionAnswer(false, minBondProblem ?? whitelistProblem ?? "");
		}

		// The list is kept as the user wrote it, also while the relay is off or takes the other events.
		var isReadable = TryReadSettings(SelectedEvents.Choice, Kinds, out var settings, out var problem);
		settings = settings with { Senders = senders };

		if (!IsRelayOn)
		{
			await services.StopNostrRelayAsync();
			Settings.SetNostrRelay(isRun: false, settings);

			return new ConnectionAnswer(true, OffMessage);
		}

		if (!isReadable)
		{
			return new ConnectionAnswer(false, problem ?? "");
		}

		if (Obstacle is { } obstacle)
		{
			return new ConnectionAnswer(false, obstacle);
		}

		var state = await services.RunNostrRelayAsync(settings, cancellationToken);

		// The relay is on from here on. If it does not run yet it goes on trying, also after a restart.
		Settings.SetNostrRelay(isRun: true, settings);

		return DescribeRun(state);
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// The relay gets its address, and loses it, by itself: with Tor.
		UiContext.Services.EventBus.AsObservable<NostrRelayStateChanged>()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(e => Show(e.State))
			.DisposeWith(disposables);

		Show(UiContext.Services.GetNostrRelayState());
	}

	private void Show(NostrRelayState state)
	{
		Address = FormatAddress(state);

		// About a relay that is off there is nothing to tell. What the user was told when they turned it off stays in sight.
		if (state.Status == NostrRelayStatus.Off && StatusMessage == OffMessage)
		{
			return;
		}

		var (message, isGood) = DescribeState(state);

		// Why no relay can run here is on the page already.
		if (state.Status == NostrRelayStatus.NotWorking && state.Problem == Obstacle)
		{
			message = null;
		}

		ShowState(message, isGood);
	}
}
