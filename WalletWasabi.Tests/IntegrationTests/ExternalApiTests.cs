using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.WebClients.Wasabi;
using Xunit;

namespace WalletWasabi.Tests.IntegrationTests;

public class ExternalApiTests
{
	[Fact(Timeout = 180_000)]
	public async Task BlockstreamFeeRateProviderTestsAsync() =>
		await AssertFeeProviderAsync(FeeRateProviders.BlockstreamAsync, TestContext.Current.CancellationToken);

	[Fact(Timeout = 180_000)]
	public async Task MempoolSpaceRateProviderTestsAsync() =>
		await AssertFeeProviderAsync(FeeRateProviders.MempoolSpaceAsync, TestContext.Current.CancellationToken);

	private async Task AssertFeeProviderAsync(Func<IHttpClientFactory, FeeRateProvider> providerFactory, CancellationToken cancellationToken)
	{
		var provider = providerFactory(new HttpClientFactory());
		var estimations = await provider(cancellationToken).ConfigureAwait(false);
		Assert.NotEmpty(estimations.Estimations);
	}
}
