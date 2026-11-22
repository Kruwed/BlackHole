using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.Models.UI;

/// <summary>What the application connects to, in the order in which the user sets it up.</summary>
public enum Connection
{
	Tor,
	Node,
	Nostr,
	Coordinator
}

/// <summary>How it stands with one of the connections of the application.</summary>
public enum ConnectionState
{
	/// <summary>The user has not connected the application to it.</summary>
	NotConfigured,

	/// <summary>It is configured, and it is not known yet whether it works at the moment.</summary>
	Unknown,

	/// <summary>It is configured, and it works at the moment.</summary>
	Working,

	/// <summary>It is configured, and it does not work at the moment.</summary>
	NotWorking
}

/// <summary>
/// Tells for Tor, the node, Nostr and the coordinator whether the user has configured the connection, and whether it
/// works at the moment. The settings show it in the box of each connection.
/// </summary>
/// <remarks>
/// A connection is configured once the user has connected the application to it on its page: the settings keep note
/// of that. In a data folder that has a wallet every connection that there is counts as configured, as such a folder
/// may be older than the note. Whether a connection works is found out while somebody watches, see <see cref="Watch"/>:
/// nothing is asked otherwise.
/// </remarks>
[AppLifetime]
public partial class ConnectionsModel : ReactiveObject
{
	/// <summary>How long Tor is given to start, after the application did, before it counts as not working.</summary>
	public static readonly TimeSpan TorStartGrace = TimeSpan.FromSeconds(90);

	/// <summary>How often Tor and the node are looked at while somebody watches. Looking at them costs next to nothing.</summary>
	private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(3);

	/// <summary>How often the relays and the coordinator are asked while somebody watches.</summary>
	private static readonly TimeSpan RemoteCheckInterval = TimeSpan.FromSeconds(60);

	/// <summary>What the relays or the coordinator answered that short a time ago is not asked for again when somebody starts to watch.</summary>
	private static readonly TimeSpan RemoteCheckFreshness = TimeSpan.FromSeconds(20);

	private readonly IServices _services;
	private readonly ApplicationSettings _settings;
	private readonly WalletRepository _walletRepository;
	private readonly Dictionary<Connection, Probe> _probes = Enum.GetValues<Connection>().ToDictionary(connection => connection, _ => new Probe());
	private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

	/// <summary>Whether Tor was connected at some time since the application started.</summary>
	private bool _wasTorConnected;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private ConnectionState _tor;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private ConnectionState _node;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private ConnectionState _nostr;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private ConnectionState _coordinator;

	public ConnectionsModel(IServices services, ApplicationSettings settings, WalletRepository walletRepository)
	{
		_services = services;
		_settings = settings;
		_walletRepository = walletRepository;

		// With the wallet, every connection that there is counts as configured.
		walletRepository.WhenAnyValue(x => x.Wallet)
			.Skip(1)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ => Update());

		Update();
	}

	/// <summary>
	/// Whether the application uses Tor. If it does not, there is no Tor to connect to: Tor is turned off in the
	/// settings, or the application is on regtest.
	/// </summary>
	public bool IsTorUsed => _services.GetUseTor() != TorMode.Disabled;

	public ConnectionState GetState(Connection connection) =>
		connection switch
		{
			Connection.Tor => Tor,
			Connection.Node => Node,
			Connection.Nostr => Nostr,
			Connection.Coordinator => Coordinator,
			_ => throw new ArgumentOutOfRangeException(nameof(connection))
		};

	/// <summary>Whether the user is through with a connection on the welcome, see <see cref="IsDone(bool, bool)"/>.</summary>
	public bool IsDone(Connection connection) =>
		IsDone(
			isConfigured: GetState(connection) != ConnectionState.NotConfigured,
			isUsed: connection != Connection.Tor || IsTorUsed);

	/// <summary>
	/// The user is through with a connection once it is configured, or if there is nothing to configure: that is the
	/// case for Tor when the application does not use it.
	/// </summary>
	public static bool IsDone(bool isConfigured, bool isUsed) => isConfigured || !isUsed;

	/// <summary>
	/// A connection is configured if there is something that the application connects to, and the user connected the
	/// application to it. With a wallet the second is taken for granted.
	/// </summary>
	public static bool IsConfigured(bool hasTarget, bool wasConnectedByUser, bool hasWallet) =>
		hasTarget && (wasConnectedByUser || hasWallet);

	/// <param name="works">Whether the connection worked the last time it was looked at. Null if it was not looked at yet.</param>
	public static ConnectionState GetState(bool isConfigured, bool? works) =>
		(isConfigured, works) switch
		{
			(false, _) => ConnectionState.NotConfigured,
			(true, null) => ConnectionState.Unknown,
			(true, true) => ConnectionState.Working,
			(true, false) => ConnectionState.NotWorking
		};

	/// <summary>
	/// Tells whether Tor works, from what it answered. A Tor that is not connected while the application is still
	/// starting is on its way, which is neither: it takes Tor a while to connect to the Tor network.
	/// </summary>
	/// <param name="wasConnectedBefore">Whether Tor was connected at some time since the application started.</param>
	/// <param name="sinceStart">How long the application has been running.</param>
	public static bool? JudgeTor(bool isConnected, bool wasConnectedBefore, TimeSpan sinceStart) =>
		isConnected
			? true
			: !wasConnectedBefore && sinceStart < TorStartGrace
				? null
				: false;

	/// <summary>Whether what is known about a connection is old enough to be asked for again.</summary>
	/// <param name="checkedAt">When it was asked the last time. Null if it never was.</param>
	public static bool IsCheckDue(DateTimeOffset? checkedAt, DateTimeOffset now, TimeSpan maxAge) =>
		checkedAt is not { } lastCheck || now - lastCheck >= maxAge;

	/// <summary>
	/// Takes note that the user has just connected the application to something on its page: the connection is
	/// configured from now on, and it works.
	/// </summary>
	public void SetConnected(Connection connection)
	{
		var probe = _probes[connection];

		// An answer that is still on its way is about what the application was connected to before.
		probe.Version++;
		probe.Works = true;
		probe.CheckedAt = DateTimeOffset.UtcNow;

		if (connection == Connection.Tor)
		{
			_wasTorConnected = true;
		}

		_settings.SetConnectionConfigured(connection.ToString());

		Update();
	}

	/// <summary>
	/// Keeps finding out whether the connections work, until what it returns is disposed. Tor and the node are looked
	/// at every few seconds, which stays on this computer. The relays and the coordinator are asked right away,
	/// unless they were a moment ago, and then once a minute.
	/// </summary>
	public IDisposable Watch()
	{
		var watch = new CancellationDisposable();

		_ = WatchAsync(watch.Token);

		return watch;
	}

	private async Task WatchAsync(CancellationToken cancellationToken)
	{
		var remoteMaxAge = RemoteCheckFreshness;

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var doesTorWork = await CheckLocalAsync(cancellationToken);

				CheckRemote(doesTorWork, remoteMaxAge);
				remoteMaxAge = RemoteCheckInterval;

				await Task.Delay(WatchInterval, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
			// Nobody watches any more.
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	/// <summary>Looks at Tor and at the node.</summary>
	/// <returns>Whether Tor works. Null if it is still starting, or if the application does not use it.</returns>
	private async Task<bool?> CheckLocalAsync(CancellationToken cancellationToken)
	{
		bool? doesTorWork = null;

		if (IsTorUsed)
		{
			var probe = _probes[Connection.Tor];
			var version = probe.Version;
			var answer = await _services.VerifyTorAsync(cancellationToken);

			doesTorWork = JudgeTor(answer.IsOk, _wasTorConnected, DateTimeOffset.UtcNow - _startedAt);
			_wasTorConnected |= answer.IsOk;

			Apply(Connection.Tor, version, doesTorWork);
		}

		// The application asks the node every few seconds anyway.
		Apply(Connection.Node, _probes[Connection.Node].Version, _services.IsNodeAnswering());

		return doesTorWork;
	}

	/// <summary>
	/// Asks the relays and the coordinator, as far as they are configured and it is time to. They are asked in the
	/// background, as an answer that does not come can take its time.
	/// </summary>
	/// <param name="doesTorWork">See <see cref="CheckLocalAsync"/>.</param>
	private void CheckRemote(bool? doesTorWork, TimeSpan maxAge)
	{
		foreach (var connection in new[] { Connection.Nostr, Connection.Coordinator })
		{
			if (!IsConfigured(connection))
			{
				continue;
			}

			var probe = _probes[connection];

			if (IsTorUsed && doesTorWork != true)
			{
				// Nothing gets through without Tor. While Tor is still starting there is nothing to tell yet.
				if (doesTorWork == false)
				{
					probe.Works = false;
					probe.CheckedAt = null;
					Update();
				}

				continue;
			}

			if (probe.IsBeingChecked || !IsCheckDue(probe.CheckedAt, DateTimeOffset.UtcNow, maxAge))
			{
				continue;
			}

			_ = CheckRemoteAsync(connection, probe);
		}
	}

	private async Task CheckRemoteAsync(Connection connection, Probe probe)
	{
		var version = probe.Version;

		probe.IsBeingChecked = true;

		try
		{
			var works = connection == Connection.Nostr
				? await DoRelaysAnswerAsync()
				: await DoesCoordinatorAnswerAsync();

			Apply(connection, version, works);
		}
		catch (Exception ex)
		{
			Logger.LogDebug(ex);
			Apply(connection, version, works: false);
		}
		finally
		{
			probe.IsBeingChecked = false;
		}
	}

	/// <summary>Nostr works if one of the relays answers, as it is connected once one does.</summary>
	private async Task<bool> DoRelaysAnswerAsync()
	{
		IReadOnlyList<RelayReachability> answers = await _services.VerifyNostrRelaysAsync(_services.GetNostrRelays(), CancellationToken.None);

		return answers.Any(relay => relay.IsReachable);
	}

	private async Task<bool> DoesCoordinatorAnswerAsync()
	{
		if (_services.GetCoordinatorUri() is not { } coordinatorUri)
		{
			return false;
		}

		var answer = await _services.VerifyCoordinatorAsync(coordinatorUri.ToString(), CancellationToken.None);

		return answer.IsOk;
	}

	/// <summary>Takes note of what was found out about a connection, unless the user connected it to something else since it was asked.</summary>
	private void Apply(Connection connection, int version, bool? works)
	{
		var probe = _probes[connection];

		if (probe.Version != version)
		{
			return;
		}

		probe.Works = works;
		probe.CheckedAt = DateTimeOffset.UtcNow;

		Update();
	}

	private bool IsConfigured(Connection connection) =>
		IsConfigured(
			HasTarget(connection),
			wasConnectedByUser: _settings.ConfiguredConnections.Contains(connection.ToString()),
			hasWallet: _walletRepository.HasWallet);

	/// <summary>Whether there is something that the application connects to. There always is a node: the one of the settings, or the one that Bitcoin Core runs on this computer by default.</summary>
	private bool HasTarget(Connection connection) =>
		connection switch
		{
			Connection.Tor => IsTorUsed,
			Connection.Node => true,
			Connection.Nostr => _services.GetNostrRelays().Count > 0,
			Connection.Coordinator => _services.GetCoordinatorUri() is not null,
			_ => throw new ArgumentOutOfRangeException(nameof(connection))
		};

	private void Update()
	{
		Tor = GetCurrentState(Connection.Tor);
		Node = GetCurrentState(Connection.Node);
		Nostr = GetCurrentState(Connection.Nostr);
		Coordinator = GetCurrentState(Connection.Coordinator);
	}

	private ConnectionState GetCurrentState(Connection connection) =>
		GetState(IsConfigured(connection), _probes[connection].Works);

	/// <summary>What is known about one connection.</summary>
	private sealed class Probe
	{
		/// <summary>Whether it worked the last time it was looked at. Null if it was not looked at yet.</summary>
		public bool? Works { get; set; }

		public DateTimeOffset? CheckedAt { get; set; }

		/// <summary>Goes up whenever the user connects the application to something else.</summary>
		public int Version { get; set; }

		public bool IsBeingChecked { get; set; }
	}
}
