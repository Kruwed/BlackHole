using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NNostr.Client;
using NNostr.Client.Protocols;
using WalletWasabi.Bases;
using WalletWasabi.Logging;

namespace WalletWasabi.Discoverability;

public class CoordinatorAnnouncer(AnnouncerConfig config, Network network, TimeSpan? time = null)
	: PeriodicRunner(time ?? AnnouncementPeriod)
{
	/// <summary>What the <c>type</c> tag of an announcement says.</summary>
	public const string Type = "wabisabi";

	/// <summary>How often a coordinator announces itself, to tell that it is still there.</summary>
	public static readonly TimeSpan AnnouncementPeriod = TimeSpan.FromMinutes(15);

	protected override async Task ActionAsync(CancellationToken cancellationToken)
	{
		using var client = NostrClientFactory.Create(config.RelayUris.Select(x => new Uri(x)).ToArray());
		var unsignedAnnouncementEvent = new NostrEvent
		{
			Kind = JoinMarket.MakerOfferEvent.Kind,
			Content = config.CoordinatorDescription,
			Tags =
			[
				CreateTag("name", config.CoordinatorName),
				CreateTag("type", Type),
				CreateTag("network", network.ChainName.ToString().ToLower()),
				CreateTag("endpoint", config.CoordinatorUri),
				CreateTag("absolutemininputcount", config.AbsoluteMinInputCount.ToString(CultureInfo.InvariantCulture)),
				CreateTag("readmore", config.ReadMoreUri)
			]
		};

		using var key = config.Key.FromNIP19Nsec();
		var announcementEvent = await unsignedAnnouncementEvent.ComputeIdAndSignAsync(key).ConfigureAwait(false);

		using var timeoutCancellationTokenSource = new CancellationTokenSource (TimeSpan.FromSeconds(10));
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCancellationTokenSource.Token, cancellationToken);
		await client.PublishAsync([announcementEvent], linkedCts.Token).ConfigureAwait(false);

		Logger.LogInfo($"Coordinator has been successfully announced on Nostr ({announcementEvent.Id}).");
	}

	private static NostrEventTag CreateTag(string tagIdentifier, string data) =>
			new() { TagIdentifier = tagIdentifier, Data = [data] };
}
