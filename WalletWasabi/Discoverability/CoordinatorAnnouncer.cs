using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using WalletWasabi.Bases;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.Logging;
using WalletWasabi.NostrRelay;

namespace WalletWasabi.Discoverability;

/// <summary>
/// Announces a coordinator on Nostr relays, again and again, so that whoever looks for a coordinator finds it and
/// sees that it is still there.
/// </summary>
/// <remarks>
/// <para>
/// A relay may take events only from keys with a fidelity bond behind them, see <see cref="RelaySenderRules"/>. The
/// announcement of a coordinator is filtered like the offer of a maker: it shows the proof of a bond, in a tag, see
/// <see cref="RelayBondClaim"/>. The coordinator has no wallet and locks no coins itself. The bond is in a wallet,
/// and that wallet makes the proof for the key of the coordinator, which the config of the announcer then carries.
/// </para>
/// <para>
/// A relay at an onion address is reached through Tor: through the Tor of the coordinator, if it runs one. The other
/// relays are reached directly.
/// </para>
/// </remarks>
public class CoordinatorAnnouncer : PeriodicRunner
{
	/// <summary>What the <c>type</c> tag of an announcement says.</summary>
	public const string Type = "wabisabi";

	/// <summary>What is told about a relay at an onion address when the coordinator runs no Tor.</summary>
	public const string NoTorMessage = "The relay is at an onion address, and this coordinator runs no Tor to reach it with.";

	/// <summary>How often a coordinator announces itself, to tell that it is still there.</summary>
	public static readonly TimeSpan AnnouncementPeriod = TimeSpan.FromMinutes(15);

	/// <summary>How long the first announcement waits for Tor to reach a relay at an onion address. Tor takes its time to start.</summary>
	private static readonly TimeSpan TorPatience = TimeSpan.FromMinutes(3);

	private static readonly TimeSpan TorProbePeriod = TimeSpan.FromSeconds(10);

	private readonly AnnouncerConfig _config;
	private readonly Network _network;
	private readonly IRPCClient? _rpcClient;
	private readonly EndPoint? _torSocksEndPoint;
	private readonly Func<DateTimeOffset> _now;

	/// <summary>What every relay said the last time, so that the log tells what changed and not the same thing every quarter of an hour.</summary>
	private readonly Dictionary<Uri, string> _lastAnswers = [];

	private BondProofVerifier? _bondVerifier;
	private string? _lastBondProblem;
	private long? _lastBondAmount;
	private bool _hasWaitedForTor;
	private bool _hasToldItsKey;

	/// <param name="rpcClient">The node of the coordinator. With it the announcer tells in the log whether the chain has the bond that the announcement shows.</param>
	/// <param name="torSocksEndPoint">The SOCKS port of the Tor that the coordinator runs, or null if it runs none.</param>
	/// <param name="now">The clock, for tests.</param>
	public CoordinatorAnnouncer(
		AnnouncerConfig config,
		Network network,
		TimeSpan? time = null,
		IRPCClient? rpcClient = null,
		EndPoint? torSocksEndPoint = null,
		Func<DateTimeOffset>? now = null)
		: base(time ?? AnnouncementPeriod)
	{
		_config = config;
		_network = network;
		_rpcClient = rpcClient;
		_torSocksEndPoint = torSocksEndPoint;
		_now = now ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>
	/// Reads the proof of the fidelity bond from the config of the announcer, if it has one that is for the key of the
	/// coordinator.
	/// </summary>
	/// <param name="problem">Why the config has a proof that cannot be shown. Null if it has none, or one that can.</param>
	public static BondProof? ReadBond(AnnouncerConfig config, out string? problem)
	{
		problem = null;

		if (string.IsNullOrWhiteSpace(config.BondProof))
		{
			return null;
		}

		if (!BondProof.TryParse(config.BondProof, out var proof))
		{
			problem = "The BondProof of the announcer cannot be read. It is what the wallet writes: the transaction, the number of the output, the unlock height, the key and the signature, with colons between them.";
			return null;
		}

		using var key = config.Key.FromNIP19Nsec();
		var publicKey = key.CreateXOnlyPubKey();

		if (!proof.IsSignedFor(publicKey.ToBytes()))
		{
			problem = $"The BondProof of the announcer is not for the key of this coordinator. The wallet has to make it for {publicKey.ToNIP19()}.";
			return null;
		}

		return proof;
	}

	/// <summary>Makes the announcement of a coordinator. It is not signed yet.</summary>
	/// <param name="bond">The proof of the fidelity bond that stands behind the coordinator, if there is one: the announcement shows it.</param>
	public static NostrEvent CreateAnnouncement(AnnouncerConfig config, Network network, DateTimeOffset createdAt, BondProof? bond)
	{
		List<NostrEventTag> tags =
		[
			CreateTag("name", config.CoordinatorName),
			CreateTag("type", Type),
			CreateTag("network", MakerOfferEvent.GetNetworkName(network)),
			CreateTag("endpoint", config.CoordinatorUri),
			CreateTag("absolutemininputcount", config.AbsoluteMinInputCount.ToString(CultureInfo.InvariantCulture)),
			CreateTag("readmore", config.ReadMoreUri)
		];

		if (bond is not null)
		{
			var tag = RelayBondClaim.CreateTag(bond);
			tags.Add(new NostrEventTag { TagIdentifier = tag[0], Data = [.. tag[1..]] });
		}

		return new NostrEvent
		{
			Kind = MakerOfferEvent.Kind,
			Content = config.CoordinatorDescription,
			CreatedAt = createdAt,
			Tags = tags
		};
	}

	/// <summary>Sends the announcement to the relays of the config, and tells what each of them made of it.</summary>
	public async Task<IReadOnlyList<RelayAnswer>> AnnounceAsync(CancellationToken cancellationToken)
	{
		var bond = ReadBond(_config, out var problem);

		if (problem != _lastBondProblem)
		{
			_lastBondProblem = problem;

			if (problem is not null)
			{
				Logger.LogError($"{problem} The coordinator is announced without a fidelity bond.");
			}
		}

		using var key = _config.Key.FromNIP19Nsec();
		var publicKey = key.CreateXOnlyPubKey();

		if (!_hasToldItsKey)
		{
			// Whoever is to make the proof of a fidelity bond for this coordinator needs this key.
			_hasToldItsKey = true;
			Logger.LogInfo($"The coordinator announces itself on Nostr with the key {publicKey.ToNIP19()}.");
		}

		if (bond is not null)
		{
			await CheckBondAsync(bond, Convert.ToHexStringLower(publicKey.ToBytes()), cancellationToken).ConfigureAwait(false);
		}

		var announcement = await CreateAnnouncement(_config, _network, _now(), bond).ComputeIdAndSignAsync(key).ConfigureAwait(false);

		var relays = NostrRelayPublisher.ParseRelays(_config.RelayUris);
		var onionRelays = relays.Where(IsOnion).ToArray();
		var otherRelays = relays.Except(onionRelays).ToArray();
		var answers = new List<RelayAnswer>();

		if (otherRelays.Length > 0)
		{
			answers.AddRange(await new NostrRelayPublisher(otherRelays, torSocksEndPoint: null).PublishAsync(announcement, cancellationToken).ConfigureAwait(false));
		}

		if (onionRelays.Length > 0 && _torSocksEndPoint is null)
		{
			answers.AddRange(onionRelays.Select(relay => new RelayAnswer(relay, Accepted: false, NoTorMessage)));
		}
		else if (onionRelays.Length > 0)
		{
			await WaitForTorOnceAsync(onionRelays, cancellationToken).ConfigureAwait(false);
			answers.AddRange(await new NostrRelayPublisher(onionRelays, _torSocksEndPoint).PublishAsync(announcement, cancellationToken).ConfigureAwait(false));
		}

		Log(answers, announcement.Id);

		return answers;
	}

	protected override Task ActionAsync(CancellationToken cancellationToken) =>
		AnnounceAsync(cancellationToken);

	private static bool IsOnion(Uri relay) => relay.Host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase);

	private static NostrEventTag CreateTag(string tagIdentifier, string data) =>
		new() { TagIdentifier = tagIdentifier, Data = [data] };

	/// <summary>
	/// Tells in the log what the node of the coordinator knows of the bond that the announcement shows, when that
	/// changes: a relay asks its own node the same, and takes the announcement by the answer.
	/// </summary>
	private async Task CheckBondAsync(BondProof bond, string publicKey, CancellationToken cancellationToken)
	{
		if (_rpcClient is null)
		{
			return;
		}

		long amount;

		try
		{
			_bondVerifier ??= new BondProofVerifier(_rpcClient);
			amount = (await _bondVerifier.VerifyAsync([(publicKey, bond)], cancellationToken).ConfigureAwait(false))[0];
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// The node is away. That says nothing about the bond, and the announcement goes out as it is.
			Logger.LogDebug($"The node could not be asked about the fidelity bond of the announcement: {ex.Message}");
			return;
		}

		if (amount == _lastBondAmount)
		{
			return;
		}

		_lastBondAmount = amount;

		if (amount > 0)
		{
			Logger.LogInfo($"The announcement of the coordinator shows a fidelity bond of {NostrRelaySettings.FormatBond(amount)} BTC, which is locked until block {bond.UnlockHeight}.");
		}
		else
		{
			Logger.LogWarning("The node does not find the fidelity bond of the announcement on the chain as one that is still locked. Relays that ask for a bond will not take the announcement.");
		}
	}

	/// <summary>
	/// Before the first announcement, waits until Tor reaches one of the relays at onion addresses: the announcer
	/// starts with the coordinator, and Tor needs a while longer.
	/// </summary>
	private async Task WaitForTorOnceAsync(Uri[] onionRelays, CancellationToken cancellationToken)
	{
		if (_hasWaitedForTor)
		{
			return;
		}

		_hasWaitedForTor = true;

		var probe = new NostrRelayPublisher(onionRelays, _torSocksEndPoint, TorProbePeriod);
		using var patienceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		patienceCts.CancelAfter(TorPatience);

		try
		{
			while ((await probe.CheckAsync(patienceCts.Token).ConfigureAwait(false)).All(relay => !relay.IsReachable))
			{
				await Task.Delay(TorProbePeriod, patienceCts.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// None of them was reached in that time. What each relay says is told with the announcement.
		}
	}

	private void Log(List<RelayAnswer> answers, string announcementId)
	{
		foreach (var answer in answers)
		{
			var said = answer.Accepted ? "" : answer.Message;

			if (_lastAnswers.TryGetValue(answer.Relay, out var before) && before == said)
			{
				continue;
			}

			_lastAnswers[answer.Relay] = said;

			if (answer.Accepted)
			{
				Logger.LogInfo($"The Nostr relay {answer.Relay} takes the announcement of the coordinator.");
			}
			else
			{
				Logger.LogWarning($"The Nostr relay {answer.Relay} does not take the announcement of the coordinator: {answer.Message}");
			}
		}

		Logger.LogInfo($"The coordinator was announced on {answers.Count(answer => answer.Accepted)} of {answers.Count} Nostr relays ({announcementId}).");
	}
}
