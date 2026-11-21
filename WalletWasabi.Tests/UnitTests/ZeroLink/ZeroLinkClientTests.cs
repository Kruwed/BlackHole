using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.ZeroLink;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ZeroLink;

/// <summary>
/// Which coin a wallet takes into a ZeroLink round, and which round: one coin of a standard denomination that the
/// coordinator has a round of, and of several such denominations the largest.
/// </summary>
public class ZeroLinkClientTests
{
	private static readonly FeeRate FeeRate = new(2m);
	private static readonly DateTimeOffset InAMinute = DateTimeOffset.UtcNow.AddMinutes(1);

	[Fact]
	public void CoinOfAStandardDenominationCanGoIntoARound()
	{
		var keyManager = ServiceFactory.CreateKeyManager();

		Assert.True(CoinJoinClient.IsCoinForZeroLink(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), Money.Coins(0.1m))));
		Assert.True(CoinJoinClient.IsCoinForZeroLink(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), Money.Satoshis(1_594_323))));

		// What a ZeroLink round gives is a denomination less a fee, and that is not a standard denomination.
		Assert.False(CoinJoinClient.IsCoinForZeroLink(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), Money.Satoshis(9_999_778))));
		Assert.False(CoinJoinClient.IsCoinForZeroLink(BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(keyManager), Money.Coins(0.37m))));
	}

	[Fact]
	public void WalletGoesIntoTheRoundOfTheLargestDenominationThatItHasACoinOf()
	{
		var small = CreateRoundState(Money.Coins(0.01m));
		var medium = CreateRoundState(Money.Coins(0.1m));
		var large = CreateRoundState(Money.Coins(1m));
		var anyAmount = RoundState.FromRound(WabiSabiFactory.CreateRound(RoundParameters.Create(new WabiSabiConfig(), FeeRate)));
		RoundState[] rounds = [small, anyAmount, large, medium];

		// The wallet has coins of 0.01 and of 0.1 BTC: the round of 0.1 BTC is the one.
		Assert.Equal(medium.Id, Select(rounds, Money.Coins(0.01m), Money.Coins(0.1m))?.Id);
		Assert.Equal(small.Id, Select(rounds, Money.Coins(0.01m))?.Id);
		Assert.Equal(large.Id, Select(rounds, Money.Coins(0.01m), Money.Coins(1m), Money.Coins(0.1m))?.Id);

		// A coin of a denomination that the coordinator has no round of waits, and so does a wallet without a coin of any round.
		Assert.Null(Select(rounds, Money.Coins(0.5m)));
		Assert.Equal(small.Id, Select(rounds, Money.Coins(0.5m), Money.Coins(0.01m))?.Id);
		Assert.Null(Select(rounds));
		Assert.Null(Select([anyAmount], Money.Coins(0.1m)));
	}

	[Fact]
	public void RoundThatCannotBeRegisteredToIsLeftAlone()
	{
		var denomination = Money.Coins(0.1m);

		// A round that is past its registration.
		var started = CreateRound(denomination);
		started.SetPhase(Phase.ConnectionConfirmation);
		Assert.Null(Select([RoundState.FromRound(started)], denomination));

		// A blame round: it is for the inputs of the round that it follows.
		var blameRound = new BlameRound(started.Parameters, started, new HashSet<OutPoint>(), InsecureRandom.Instance);
		Assert.Null(Select([RoundState.FromRound(blameRound)], denomination));

		// A round whose registration is over before the wallet would get to it.
		var open = RoundState.FromRound(CreateRound(denomination));
		Assert.Equal(open.Id, Select([open], denomination)?.Id);
		Assert.Null(CoinJoinClient.SelectZeroLinkRound([open], new HashSet<Money> { denomination }, open.InputRegistrationEnd.AddSeconds(1)));
	}

	private static RoundState? Select(IEnumerable<RoundState> rounds, params Money[] denominations) =>
		CoinJoinClient.SelectZeroLinkRound(rounds, denominations.ToHashSet(), InAMinute);

	private static Round CreateRound(Money denomination) =>
		WabiSabiFactory.CreateRound(ZeroLinkRules.CreateRoundParameters(new WabiSabiConfig(), FeeRate, denomination));

	private static RoundState CreateRoundState(Money denomination) =>
		RoundState.FromRound(CreateRound(denomination));
}
