using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Coordinator.WabiSabi;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Services;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Coordinator.Models;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.ZeroLink;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ZeroLink;

/// <summary>
/// The ZeroLink rounds of a coordinator: which ones it holds, what it takes into them, and what follows a round that
/// was disrupted.
/// </summary>
public class ZeroLinkCoordinatorTests
{
	private static readonly Money Denomination = Money.Coins(0.1m);

	private TimeSpan TestTimeout { get; } = TimeSpan.FromMinutes(3);

	[Fact]
	public async Task CoordinatorHoldsNoZeroLinkRoundUnlessItIsToldTheDenominationsAsync()
	{
		using Arena arena = await ArenaBuilder.From(new WabiSabiConfig()).With(BitcoinFactory.GetMockMinimalRpc()).CreateAndStartAsync();
		await arena.TriggerAndWaitRoundAsync(TestTimeout);

		var round = Assert.Single(arena.Rounds);
		Assert.False(ZeroLinkRules.IsZeroLinkRound(round.Parameters));

		await arena.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task CoordinatorHoldsOneRoundOfEveryDenominationAsync()
	{
		var smallDenomination = Money.Satoshis(1_594_323);
		WabiSabiConfig cfg = new() { ZeroLinkDenominations = [Denomination, smallDenomination] };

		using Arena arena = CreateArena(cfg, () => new FeeRate(5m));
		await arena.StartAsync(TestContext.Current.CancellationToken);
		await arena.TriggerAndWaitRoundAsync(TestTimeout);

		// The round that takes any amount is there as before, and next to it one ZeroLink round of each denomination.
		Assert.Equal(3, arena.Rounds.Count);
		Assert.Single(arena.Rounds, x => !ZeroLinkRules.IsZeroLinkRound(x.Parameters));
		Assert.Equal(
			new[] { smallDenomination, Denomination },
			arena.Rounds.Select(GetDenomination).Where(x => x is not null).Order());

		// At 5 sat/vB a participant pays for 111 vB at that: 555 sats.
		var round = arena.Rounds.Single(x => GetDenomination(x) == Denomination);
		Assert.Equal(new MoneyRange(Denomination, Denomination), round.Parameters.AllowedInputAmounts);
		Assert.Equal(new MoneyRange(Money.Satoshis(9_999_445), Money.Satoshis(9_999_445)), round.Parameters.AllowedOutputAmounts);

		// Nothing is added while these rounds can be registered to.
		await arena.TriggerAndWaitRoundAsync(TestTimeout);
		Assert.Equal(3, arena.Rounds.Count);

		// Once a round has left its registration, the next one of its denomination is opened.
		round.SetPhase(Phase.ConnectionConfirmation);
		await arena.TriggerAndWaitRoundAsync(TestTimeout);

		Assert.Equal(4, arena.Rounds.Count);
		Assert.Equal(2, arena.Rounds.Count(x => GetDenomination(x) == Denomination));
		Assert.Single(arena.Rounds, x => GetDenomination(x) == Denomination && x.Phase == Phase.InputRegistration);
		Assert.Single(arena.Rounds, x => GetDenomination(x) == smallDenomination);
		Assert.Single(arena.Rounds, x => !ZeroLinkRules.IsZeroLinkRound(x.Parameters));

		await arena.StopAsync(TestContext.Current.CancellationToken);
	}

	/// <summary>
	/// At a fee rate that eats a denomination, or leaves dust of it, the coordinator holds no round of it. The round
	/// comes with a fee rate that leaves an output.
	/// </summary>
	[Fact]
	public async Task DenominationThatTheFeeEatsHasNoRoundAsync()
	{
		var smallDenomination = Money.Satoshis(5_000);
		WabiSabiConfig cfg = new() { ZeroLinkDenominations = [smallDenomination, Denomination] };

		var feeRate = new FeeRate(100m);

		using Arena arena = CreateArena(cfg, () => feeRate);
		await arena.StartAsync(TestContext.Current.CancellationToken);
		await arena.TriggerAndWaitRoundAsync(TestTimeout);

		// At 100 sat/vB a participant pays 11,100 sats: more than 5,000 sats hold.
		Assert.DoesNotContain(arena.Rounds, x => GetDenomination(x) == smallDenomination);
		Assert.Single(arena.Rounds, x => GetDenomination(x) == Denomination);

		feeRate = new FeeRate(2m);
		await arena.TriggerAndWaitRoundAsync(TestTimeout);

		var round = Assert.Single(arena.Rounds, x => GetDenomination(x) == smallDenomination);
		Assert.Equal(Money.Satoshis(5_000 - 222), round.Parameters.AllowedOutputAmounts.Min);
		Assert.Single(arena.Rounds, x => GetDenomination(x) == Denomination);

		await arena.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact]
	public void CoordinatorDoesNotStartWithADenominationThatIsNotStandard()
	{
		WabiSabiConfig cfg = new() { ZeroLinkDenominations = [Money.Coins(0.3m)] };

		var exception = Assert.Throws<ArgumentException>(() => ArenaBuilder.From(cfg).Create());

		Assert.Contains("0.30 BTC in ZeroLinkDenominations is not a standard denomination", exception.Message);
	}

	/// <summary>A round takes Taproot coins of its denomination, to the satoshi, and nothing else.</summary>
	[Fact]
	public async Task RoundTakesOnlyCoinsOfItsDenominationAsync()
	{
		using CancellationTokenSource cancellationTokenSource = new(TestTimeout);
		var token = cancellationTokenSource.Token;

		using Key rightKey = new();
		using Key largerKey = new();
		using Key smallerKey = new();
		using Key segwitKey = new();

		var rightCoin = WabiSabiFactory.CreateCoin(rightKey, Denomination, ScriptPubKeyType.TaprootBIP86);
		var largerCoin = WabiSabiFactory.CreateCoin(largerKey, Denomination + Money.Satoshis(1), ScriptPubKeyType.TaprootBIP86);
		var smallerCoin = WabiSabiFactory.CreateCoin(smallerKey, Denomination - Money.Satoshis(1), ScriptPubKeyType.TaprootBIP86);
		var segwitCoin = WabiSabiFactory.CreateCoin(segwitKey, Denomination, ScriptPubKeyType.Segwit);

		WabiSabiConfig cfg = new() { ZeroLinkDenominations = [Denomination] };
		var rpc = WabiSabiFactory.CreatePreconfiguredRpcClient(rightCoin, largerCoin, smallerCoin, segwitCoin);

		using Arena arena = await ArenaBuilder.From(cfg).With(rpc).CreateAndStartAsync();
		await arena.TriggerAndWaitRoundAsync(token);

		var round = arena.Rounds.Single(x => GetDenomination(x) == Denomination);
		var arenaClient = WabiSabiFactory.CreateArenaClient(arena, round);

		Task RegisterAsync(Key key, Coin coin, ScriptPubKeyType scriptPubKeyType) =>
			arenaClient.RegisterInputAsync(round.Id, coin.Outpoint, WabiSabiFactory.CreateOwnershipProof(key, round.Id, scriptPubKeyType), token);

		async Task AssertRefusedAsync(WabiSabiProtocolErrorCode expected, Key key, Coin coin, ScriptPubKeyType scriptPubKeyType)
		{
			var exception = await Assert.ThrowsAsync<WabiSabiProtocolException>(() => RegisterAsync(key, coin, scriptPubKeyType));
			Assert.Equal(expected, exception.ErrorCode);
		}

		await AssertRefusedAsync(WabiSabiProtocolErrorCode.TooMuchFunds, largerKey, largerCoin, ScriptPubKeyType.TaprootBIP86);
		await AssertRefusedAsync(WabiSabiProtocolErrorCode.NotEnoughFunds, smallerKey, smallerCoin, ScriptPubKeyType.TaprootBIP86);
		await AssertRefusedAsync(WabiSabiProtocolErrorCode.ScriptNotAllowed, segwitKey, segwitCoin, ScriptPubKeyType.Segwit);
		Assert.Empty(round.Alices);

		await RegisterAsync(rightKey, rightCoin, ScriptPubKeyType.TaprootBIP86);
		Assert.Equal(rightCoin.Outpoint, Assert.Single(round.Alices).Coin.Outpoint);

		await arena.StopAsync(token);
	}

	/// <summary>
	/// The output of a participant holds the denomination less the fee, and not all that its input is worth in the
	/// credentials of the round: the rest is what the participant pays for the overhead of the transaction.
	/// </summary>
	[Fact]
	public async Task OutputOfAllThatAnInputIsWorthIsRefusedAsync()
	{
		using CancellationTokenSource cancellationTokenSource = new(TestTimeout);
		var token = cancellationTokenSource.Token;

		WabiSabiConfig cfg = new() { MaxInputCountByRound = 2, ZeroLinkDenominations = [Denomination] };
		var (keyChain, coins) = CreateCoins(2);

		using Arena arena = await ArenaBuilder.From(cfg).With(WabiSabiFactory.CreatePreconfiguredRpcClient([.. coins.Select(x => x.Coin)])).CreateAndStartAsync();
		var (round, aliceClients) = await CreateRoundWithInputsConfirmedAsync(arena, keyChain, coins, token);

		var bobClient = new BobClient(round.Id, WabiSabiFactory.CreateArenaClient(arena, round));
		using Key destinationKey = new();

		var exception = await Assert.ThrowsAsync<WabiSabiProtocolException>(() => bobClient.RegisterOutputAsync(
			destinationKey.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
			aliceClients[0].IssuedAmountCredentials.Take(ProtocolConstants.CredentialNumber),
			aliceClients[0].IssuedVsizeCredentials.Take(ProtocolConstants.CredentialNumber),
			token));

		Assert.Equal(WabiSabiProtocolErrorCode.TooMuchFunds, exception.ErrorCode);
		Assert.Empty(round.Bobs);

		await arena.StopAsync(token);
	}

	/// <summary>
	/// The coordinator adds no output of its own to the transaction of a ZeroLink round, whatever the participants
	/// leave: the round that takes any amount would get an output of the coordinator here.
	/// </summary>
	[Fact]
	public async Task CoordinatorAddsNoOutputOfItsOwnAsync()
	{
		using CancellationTokenSource cancellationTokenSource = new(TestTimeout);
		var token = cancellationTokenSource.Token;

		WabiSabiConfig cfg = new() { MaxInputCountByRound = 2, ZeroLinkDenominations = [Denomination] };
		var (keyChain, coins) = CreateCoins(2);

		using Arena arena = await ArenaBuilder.From(cfg).With(WabiSabiFactory.CreatePreconfiguredRpcClient([.. coins.Select(x => x.Coin)])).CreateAndStartAsync();
		var (round, _) = await CreateRoundWithInputsConfirmedAsync(arena, keyChain, coins, token);

		// Nobody registers an output, so all that the inputs hold is left over.
		round.OutputRegistrationTimeFrame = new TimeFrame(DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.Zero);
		await arena.TriggerAndWaitRoundAsync(token);

		Assert.Equal(Phase.TransactionSigning, round.Phase);
		Assert.Null(round.CoordinatorScript);

		var transaction = round.Assert<SigningState>().CreateUnsignedTransaction();
		Assert.Equal(2, transaction.Inputs.Count);
		Assert.Empty(transaction.Outputs);

		await arena.StopAsync(token);
	}

	/// <summary>
	/// The blame round of a ZeroLink round is a ZeroLink round of the same denomination, for the inputs that signed.
	/// </summary>
	[Fact]
	public async Task BlameRoundOfAZeroLinkRoundIsOneOfTheSameDenominationAsync()
	{
		using CancellationTokenSource cancellationTokenSource = new(TestTimeout);
		var token = cancellationTokenSource.Token;

		WabiSabiConfig cfg = new()
		{
			MaxInputCountByRound = 3,
			MinInputCountByRoundMultiplier = 1,
			MinInputCountByBlameRoundMultiplier = 0.5,
			TransactionSigningTimeout = TimeSpan.Zero,
			ZeroLinkDenominations = [Denomination]
		};
		var (keyChain, coins) = CreateCoins(3);

		using Arena arena = await ArenaBuilder.From(cfg).With(WabiSabiFactory.CreatePreconfiguredRpcClient([.. coins.Select(x => x.Coin)])).CreateAndStartAsync();
		var (round, aliceClients) = await CreateRoundWithInputsConfirmedAsync(arena, keyChain, coins, token);

		round.OutputRegistrationTimeFrame = new TimeFrame(DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.Zero);
		await arena.TriggerAndWaitRoundAsync(token);
		Assert.Equal(Phase.TransactionSigning, round.Phase);

		// Two of the three sign.
		var unsignedCoinJoin = round.Assert<SigningState>().CreateUnsignedTransactionWithPrecomputedData();

		// A signature that names its signature hash is valid, but it has a byte more than the signatures of the round have.
		var longSignature = keyChain.Sign(unsignedCoinJoin, aliceClients[0].SmartCoin.Coin, TaprootSigHash.All)
			.Inputs.AsIndexedInputs().Single(input => input.PrevOut == aliceClients[0].SmartCoin.Outpoint);
		Assert.Equal(65, longSignature.WitScript.GetUnsafePush(0).Length);
		var refused = await Assert.ThrowsAsync<WabiSabiProtocolException>(() =>
			arena.SignTransactionAsync(new TransactionSignaturesRequest(round.Id, longSignature.Index, longSignature.WitScript), token));
		Assert.Equal(WabiSabiProtocolErrorCode.SignatureTooLong, refused.ErrorCode);

		await aliceClients[0].SignTransactionAsync(unsignedCoinJoin, keyChain, token);
		await aliceClients[1].SignTransactionAsync(unsignedCoinJoin, keyChain, token);
		await arena.TriggerAndWaitRoundAsync(token);

		Assert.Equal(Phase.Ended, round.Phase);
		Assert.Equal(EndRoundState.NotAllAlicesSign, round.EndRoundState);

		var blameRound = Assert.IsType<BlameRound>(Assert.Single(arena.Rounds, x => x is BlameRound));
		Assert.Equal(round.Id, blameRound.BlameOf.Id);
		Assert.Equal(Denomination, GetDenomination(blameRound));
		Assert.Equal(round.Parameters.AllowedOutputAmounts, blameRound.Parameters.AllowedOutputAmounts);
		Assert.Equal(2, blameRound.Parameters.MinInputCountByRound);
		Assert.Equal(
			new[] { aliceClients[0].SmartCoin.Outpoint, aliceClients[1].SmartCoin.Outpoint }.OrderBy(x => x.ToString()),
			blameRound.BlameWhitelist.OrderBy(x => x.ToString()));

		await arena.StopAsync(token);
	}

	private static Money? GetDenomination(Round round) =>
		ZeroLinkRules.TryGetDenomination(round.Parameters, out var denomination) ? denomination : null;

	/// <summary>An arena whose rounds get the fee rate that the test names, whatever a node would estimate.</summary>
	private static Arena CreateArena(WabiSabiConfig cfg, Func<FeeRate> getFeeRate)
	{
		FeeRateProvider feeRateProvider = _ => Task.FromResult(
			new FeeRateEstimations(Constants.ConfirmationTargets.ToDictionary(target => target, _ => getFeeRate())));

		return new Arena(
			cfg,
			BitcoinFactory.GetMockMinimalRpc(),
			WabiSabiFactory.CreatePrison(),
			WabiSabiFactory.CreateRoundParametersFactory(cfg, WabiSabiFactory.VsizeOfOneInputAndOneOutput),
			feeRateProvider,
			period: TimeSpan.FromHours(1));
	}

	/// <summary>Coins of the denomination on Taproot keys of one wallet, with what signs for them.</summary>
	private static (IKeyChain KeyChain, SmartCoin[] Coins) CreateCoins(int count)
	{
		var keyManager = ServiceFactory.CreateKeyManager("");
		var coins = Enumerable.Range(0, count)
			.Select(_ => BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), Denomination))
			.ToArray();

		return (new KeyChain(keyManager, ""), coins);
	}

	/// <summary>
	/// Registers the coins in the ZeroLink round of the arena, as many as the round takes, and goes on to the phase in
	/// which the outputs are registered.
	/// </summary>
	private static async Task<(Round Round, AliceClient[] AliceClients)> CreateRoundWithInputsConfirmedAsync(Arena arena, IKeyChain keyChain, SmartCoin[] coins, CancellationToken token)
	{
		await arena.TriggerAndWaitRoundAsync(token);

		var round = arena.Rounds.Single(x => GetDenomination(x) == Denomination);
		var arenaClient = WabiSabiFactory.CreateArenaClient(arena, round);

		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(arena);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		var tasks = coins
			.Select(coin => AliceClient.CreateRegisterAndConfirmInputAsync(RoundState.FromRound(round), arenaClient, coin, keyChain, roundStateProvider, token, token, token))
			.ToArray();

		while (round.Phase != Phase.OutputRegistration)
		{
			await arena.TriggerAndWaitRoundAsync(token);
		}

		return (round, await Task.WhenAll(tasks));
	}
}
