using WalletWasabi.Services;

namespace WalletWasabi.FeeRateEstimation;

public static class FeeRateEstimationUpdater
{
	public record UpdateMessage;

	/// <summary>
	/// Creates the handler that asks the provider for fee rate estimations and announces them when they change.
	/// </summary>
	/// <param name="refreshPeriod">
	/// How long estimations are good for. The messages that arrive in the meantime are ignored. A provider that fails is asked again
	/// at the next message, so the estimations are there soon after the provider is back.
	/// </param>
	public static MessageHandler<UpdateMessage, FeeRateEstimations> CreateUpdater(FeeRateProvider feeRateProvider, EventBus eventBus, TimeSpan refreshPeriod)
	{
		var lastUpdate = DateTimeOffset.MinValue;
		string? lastFailure = null;

		return async (_, feeRateEstimations, cancellationToken) =>
		{
			if (DateTimeOffset.UtcNow - lastUpdate < refreshPeriod)
			{
				return feeRateEstimations;
			}

			try
			{
				feeRateEstimations = await UpdateAsync(feeRateProvider, feeRateEstimations, eventBus, cancellationToken).ConfigureAwait(false);
				lastUpdate = DateTimeOffset.UtcNow;
				lastFailure = null;
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				// The same failure is told once, not at every attempt.
				if (lastFailure != ex.Message)
				{
					Logger.LogWarning($"Failed to get fee rate estimations: {ex.Message}");
					lastFailure = ex.Message;
				}
			}

			return feeRateEstimations;
		};
	}

	private static async Task<FeeRateEstimations> UpdateAsync(FeeRateProvider feeRateProvider, FeeRateEstimations feeRateEstimations, EventBus eventBus, CancellationToken cancellationToken)
	{
		var newFeeRateEstimations = await feeRateProvider(cancellationToken).ConfigureAwait(false);

		if (newFeeRateEstimations != feeRateEstimations)
		{
			if (newFeeRateEstimations != FeeRateEstimations.Empty)
			{
				feeRateEstimations = newFeeRateEstimations;
				eventBus.Publish(new MiningFeeRatesChanged(newFeeRateEstimations));
			}
			else
			{
				Logger.LogWarning("The fee rate provider returned empty fee rate estimations.");
			}
		}

		return feeRateEstimations;
	}
}
