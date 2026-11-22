using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;
using WalletWasabi.WabiSabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

/// <summary>
/// Before the application takes a coordinator that the user names, it asks the coordinator for its rounds. These are
/// the rules of that check, and what the user is told when it fails.
/// </summary>
public class CoordinatorConnectionCheckTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

	[Theory]
	[InlineData("http://localhost:37126/")]
	[InlineData("https://coordinator.example")]
	[InlineData("  http://abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcd.onion/  ")]
	public void AddressOfACoordinatorIsAnHttpAddress(string text)
	{
		Assert.True(CoordinatorConnectionCheck.TryParseUri(text, out var uri));
		Assert.Equal(new Uri(text.Trim()), uri);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("coordinator.example")]
	[InlineData("localhost:37126")]
	[InlineData("wss://coordinator.example")]
	[InlineData("/wabisabi/status")]
	public void WhatIsNoHttpAddressIsNotTheAddressOfACoordinator(string? text)
	{
		Assert.False(CoordinatorConnectionCheck.TryParseUri(text, out var uri));
		Assert.Null(uri);
	}

	[Fact]
	public void OnionServiceIsToldFromOtherAddresses()
	{
		Assert.True(CoordinatorConnectionCheck.IsOnion(new Uri("http://abcdefghijklmnop.onion/")));
		Assert.True(CoordinatorConnectionCheck.IsOnion(new Uri("http://ABCDEFGHIJKLMNOP.ONION:8080/")));
		Assert.False(CoordinatorConnectionCheck.IsOnion(new Uri("https://coordinator.example/")));
		Assert.False(CoordinatorConnectionCheck.IsOnion(new Uri("http://onion.example/")));
	}

	[Fact]
	public async Task CoordinatorThatTellsItsRoundsCanBeUsedAsync()
	{
		var coordinator = new FakeCoordinator(_ => Task.FromResult(new RoundStateResponse([])));

		var result = await CoordinatorConnectionCheck.CheckAsync(coordinator, Timeout, CancellationToken.None);

		Assert.True(result.IsOk);

		// Nothing about the wallet is in the question: it names no round.
		Assert.Empty(Assert.Single(coordinator.Requests).RoundCheckpoints);
	}

	[Fact]
	public async Task WhatAnswersWithSomethingElseIsNotACoordinatorAsync()
	{
		// An answer that is not the one of a coordinator cannot be read, and nothing comes of it.
		var coordinator = new FakeCoordinator(_ => Task.FromResult<RoundStateResponse>(null!));

		var result = await CoordinatorConnectionCheck.CheckAsync(coordinator, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("What answers at this address is not a coordinator.", result.Error);
	}

	[Fact]
	public async Task CoordinatorThatDoesNotAnswerIsNotWaitedForForeverAsync()
	{
		var coordinator = new FakeCoordinator(async cancellationToken =>
		{
			await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
			return new RoundStateResponse([]);
		});

		var result = await CoordinatorConnectionCheck.CheckAsync(coordinator, TimeSpan.FromMilliseconds(300), CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The coordinator did not answer in time.", result.Error);
	}

	[Fact]
	public async Task CallerCanGiveUpAsync()
	{
		var coordinator = new FakeCoordinator(async cancellationToken =>
		{
			await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
			return new RoundStateResponse([]);
		});
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await CoordinatorConnectionCheck.CheckAsync(coordinator, Timeout, cts.Token));
	}

	[Fact]
	public async Task CoordinatorThatCannotBeReachedIsToldWithTheReasonAsync()
	{
		var coordinator = new FakeCoordinator(_ => throw new HttpRequestException(
			"Failed to make http request 'http://127.0.0.1:1/wabisabi/status' after 2 attempts.",
			new HttpRequestException("Connection refused (127.0.0.1:1)", new SocketException((int)SocketError.ConnectionRefused))));

		var result = await CoordinatorConnectionCheck.CheckAsync(coordinator, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);

		// What went wrong the last time, and not that every attempt failed.
		Assert.Equal("The coordinator cannot be reached (Connection refused (127.0.0.1:1)).", result.Error);
	}

	/// <summary>A web server that is not a coordinator may answer with a whole page. One line of it is told.</summary>
	[Fact]
	public void ReasonIsKeptToOneShortLine()
	{
		var page = "Not Found\n<html><body>" + new string('x', 5_000) + "</body></html>";

		Assert.Equal("The coordinator cannot be reached (Not Found).", CoordinatorConnectionCheck.DescribeFailure(new HttpRequestException(page)));

		var longLine = CoordinatorConnectionCheck.DescribeFailure(new HttpRequestException(new string('x', 5_000)));
		Assert.Equal("The coordinator cannot be reached (".Length + 200 + "…).".Length, longLine.Length);

		Assert.Equal("The coordinator did not answer in time.", CoordinatorConnectionCheck.DescribeFailure(new TimeoutException()));
		Assert.Equal("The coordinator did not answer in time.", CoordinatorConnectionCheck.DescribeFailure(new AggregateException(new OperationCanceledException())));
	}

	/// <summary>
	/// Connecting to a coordinator that answers gives the application its first coordinator, or another one. The
	/// coordinator is not changed while a coinjoin is under way: its round is with the one that the application has.
	/// </summary>
	[Fact]
	public void CoordinatorIsNotChangedWhileACoinjoinIsUnderWay()
	{
		var one = new Uri("https://one.example/");
		var two = new Uri("https://two.example/");

		// The first coordinator of the application: nothing coinjoins yet.
		Assert.Equal(CoordinatorChange.Start, CoordinatorConnectionCheck.DecideChange(current: null, one, CoinJoinClientState.Idle));

		// Another one takes its place ...
		Assert.Equal(CoordinatorChange.Switch, CoordinatorConnectionCheck.DecideChange(one, two, CoinJoinClientState.Idle));

		// ... unless a coinjoin is under way, whether it waits for a round or is in the middle of one.
		Assert.Equal(CoordinatorChange.Refuse, CoordinatorConnectionCheck.DecideChange(one, two, CoinJoinClientState.InProgress));
		Assert.Equal(CoordinatorChange.Refuse, CoordinatorConnectionCheck.DecideChange(one, two, CoinJoinClientState.InCriticalPhase));

		// Connecting to the coordinator that the application has changes nothing, whatever its coinjoins do.
		foreach (var state in Enum.GetValues<CoinJoinClientState>())
		{
			Assert.Equal(CoordinatorChange.Keep, CoordinatorConnectionCheck.DecideChange(one, one, state));
			Assert.Equal(CoordinatorChange.Keep, CoordinatorConnectionCheck.DecideChange(one, new Uri("https://ONE.example"), state));
		}
	}

	private sealed class FakeCoordinator(Func<CancellationToken, Task<RoundStateResponse>> answer) : IWabiSabiApiRequestHandler
	{
		public System.Collections.Generic.List<RoundStateRequest> Requests { get; } = [];

		public Task<RoundStateResponse> GetStatusAsync(RoundStateRequest request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return answer(cancellationToken);
		}

		public Task<InputRegistrationResponse> RegisterInputAsync(InputRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task RegisterOutputAsync(OutputRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task RemoveInputAsync(InputsRemovalRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task SignTransactionAsync(TransactionSignaturesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<ReissueCredentialResponse> ReissuanceAsync(ReissueCredentialRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
