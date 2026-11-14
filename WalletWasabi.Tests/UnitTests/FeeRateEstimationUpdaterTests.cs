using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Services;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="FeeRateEstimationUpdater"/>. The fee rate estimations come from the user's node and from nowhere else,
/// so they have to be there soon after a node that could not be asked is back.
/// </summary>
public class FeeRateEstimationUpdaterTests
{
	private static readonly FeeRateEstimations Estimations = new(new Dictionary<int, FeeRate> { [2] = new FeeRate(10m), [6] = new FeeRate(5m) });
	private static readonly FeeRateEstimations OtherEstimations = new(new Dictionary<int, FeeRate> { [2] = new FeeRate(20m), [6] = new FeeRate(8m) });

	[Fact]
	public async Task AsksAgainSoonAfterAFailureAsync()
	{
		var requests = 0;
		Exception? failure = new HttpRequestException("Connection refused");

		FeeRateProvider provider = _ =>
		{
			requests++;
			return failure is null ? Task.FromResult(Estimations) : Task.FromException<FeeRateEstimations>(failure);
		};

		var eventBus = new EventBus();
		var announced = new List<FeeRateEstimations>();
		using var subscription = eventBus.Subscribe<MiningFeeRatesChanged>(e => announced.Add(e.AllFeeEstimate));
		var updater = FeeRateEstimationUpdater.CreateUpdater(provider, eventBus, refreshPeriod: TimeSpan.FromMinutes(15));

		// The node cannot be asked. That is no reason to stop, and every message is another attempt.
		var state = FeeRateEstimations.Empty;
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);

		Assert.Equal(2, requests);
		Assert.Equal(FeeRateEstimations.Empty, state);
		Assert.Empty(announced);

		// The node is back.
		failure = null;
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);

		Assert.Equal(3, requests);
		Assert.Equal(Estimations, state);
		Assert.Equal([Estimations], announced);

		// Estimations are good for a while: the node is not asked at every message.
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);

		Assert.Equal(3, requests);
		Assert.Equal(Estimations, state);
		Assert.Single(announced);
	}

	[Fact]
	public async Task AnnouncesEstimationsWhenTheyChangeAsync()
	{
		var current = Estimations;
		FeeRateProvider provider = _ => Task.FromResult(current);

		var eventBus = new EventBus();
		var announced = new List<FeeRateEstimations>();
		using var subscription = eventBus.Subscribe<MiningFeeRatesChanged>(e => announced.Add(e.AllFeeEstimate));

		// Every message asks the provider.
		var updater = FeeRateEstimationUpdater.CreateUpdater(provider, eventBus, refreshPeriod: TimeSpan.Zero);

		var state = FeeRateEstimations.Empty;
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);
		Assert.Equal([Estimations], announced);

		current = OtherEstimations;
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);
		Assert.Equal(OtherEstimations, state);
		Assert.Equal([Estimations, OtherEstimations], announced);

		// What was known is kept when the provider has nothing to tell.
		current = FeeRateEstimations.Empty;
		state = await updater(new FeeRateEstimationUpdater.UpdateMessage(), state, CancellationToken.None);
		Assert.Equal(OtherEstimations, state);
		Assert.Equal(2, announced.Count);
	}

	[Fact]
	public async Task StopsWhenTheApplicationStopsAsync()
	{
		using var cts = new CancellationTokenSource();
		FeeRateProvider provider = cancellationToken =>
		{
			cts.Cancel();
			return Task.FromCanceled<FeeRateEstimations>(cancellationToken);
		};

		var updater = FeeRateEstimationUpdater.CreateUpdater(provider, new EventBus(), refreshPeriod: TimeSpan.FromMinutes(15));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => updater(new FeeRateEstimationUpdater.UpdateMessage(), FeeRateEstimations.Empty, cts.Token));
	}
}
