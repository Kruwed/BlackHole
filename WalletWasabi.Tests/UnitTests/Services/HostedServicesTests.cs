using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using WalletWasabi.Services;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Services;

/// <summary>
/// The services of the application are registered and then started together. A service can also be registered after
/// that: the coinjoins need a coordinator, and the user may name one while the application runs.
/// </summary>
public class HostedServicesTests
{
	[Fact]
	public async Task ServiceThatIsRegisteredLateIsStartedRightAwayAsync()
	{
		using var services = new HostedServices();
		var early = new FirstService();
		services.Register<FirstService>(() => early, "First");

		await services.StartAllAsync();
		Assert.Equal(1, early.StartCount);

		var late = new SecondService();
		await services.RegisterAndStartAsync<SecondService>(() => late, "Second");

		Assert.Equal(1, late.StartCount);
		Assert.Same(late, services.Get<SecondService>());

		// It is stopped with the others.
		await services.StopAllAsync();
		Assert.Equal(1, early.StopCount);
		Assert.Equal(1, late.StopCount);
	}

	[Fact]
	public async Task ServiceThatIsRegisteredBeforeTheStartIsStartedWithTheOthersAsync()
	{
		using var services = new HostedServices();
		var first = new FirstService();
		var second = new SecondService();

		await services.RegisterAndStartAsync<FirstService>(() => first, "First");
		services.Register<SecondService>(() => second, "Second");

		// Nothing runs yet.
		Assert.Equal(0, first.StartCount);
		Assert.Same(first, services.GetOrDefault<FirstService>());

		await services.StartAllAsync();

		// Once, and not a second time by whoever registered it.
		Assert.Equal(1, first.StartCount);
		Assert.Equal(1, second.StartCount);
	}

	[Fact]
	public async Task ServiceCannotBeRegisteredTwiceAsync()
	{
		using var services = new HostedServices();
		services.Register<FirstService>(() => new FirstService(), "First");

		await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.RegisterAndStartAsync<FirstService>(() => new FirstService(), "First again"));

		await services.StartAllAsync();
		await services.RegisterAndStartAsync<SecondService>(() => new SecondService(), "Second");

		var again = new SecondService();
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await services.RegisterAndStartAsync<SecondService>(() => again, "Second again"));
		Assert.Equal(0, again.StartCount);
	}

	[Fact]
	public async Task ServiceIsRegisteredAsWhatItIsAsync()
	{
		using var services = new HostedServices();

		await Assert.ThrowsAsync<ArgumentException>(async () => await services.RegisterAndStartAsync<FirstService>(() => new SecondService(), "Mixed up"));
		Assert.Null(services.GetOrDefault<FirstService>());
		Assert.Null(services.GetOrDefault<SecondService>());
	}

	private class CountingService : IHostedService
	{
		private int _startCount;
		private int _stopCount;

		public int StartCount => Volatile.Read(ref _startCount);

		public int StopCount => Volatile.Read(ref _stopCount);

		public Task StartAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _startCount);
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _stopCount);
			return Task.CompletedTask;
		}
	}

	private sealed class FirstService : CountingService;

	private sealed class SecondService : CountingService;
}
