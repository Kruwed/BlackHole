using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using NBitcoin;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;
using WalletWasabi.WabiSabi.Models;
using static WalletWasabi.Services.Workers;

namespace WalletWasabi.Tests.UnitTests.Services;

public class RoundStateUpdaterForTesting
{
	public static MailboxProcessor<RoundUpdateMessage> Create(IWabiSabiApiRequestHandler api, CancellationToken? cancellationToken = null) =>
		Create(api, cancellationToken, autoUpdate: true);

	public static MailboxProcessor<RoundUpdateMessage> CreateManual(IWabiSabiApiRequestHandler api, CancellationToken? cancellationToken = null) =>
		Create(api, cancellationToken, autoUpdate: false);

	private static MailboxProcessor<RoundUpdateMessage> Create(IWabiSabiApiRequestHandler api, CancellationToken? cancellationToken, bool autoUpdate) =>
		Spawn<RoundUpdateMessage>($"RoundStateUpdater-{Random.Shared.Next()}", async (mailbox, token) =>
		{
			// Stop the ticker when cancellation or disposal ends the worker.
			await using var ticker = autoUpdate
				? new Timer(_ => mailbox.Post(new RoundUpdateMessage.UpdateMessage(DateTime.UtcNow)), null, TimeSpan.Zero, TimeSpan.FromSeconds(1))
				: null;
			var process = EventDriven(
				new RoundsState(DateTime.UtcNow, TimeSpan.Zero, new Dictionary<uint256, RoundState>(), ImmutableList<RoundStateAwaiter>.Empty),
				RoundStateUpdater.Create(api));
			await process(mailbox, token).ConfigureAwait(false);
		}, cancellationToken);
}
