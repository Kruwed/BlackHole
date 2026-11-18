using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// A fidelity bond is redeemed by a transaction that its script accepts, and only after its locktime.
/// </summary>
public class BondRedemptionTests
{
	private static readonly Network Network = Network.RegTest;
	private static readonly BondLocktime Locktime = BondLocktime.FromMonth(2027, 1);
	private static readonly FeeRate FeeRate = new(12m);

	[Theory]
	[InlineData(2027, 1)]
	[InlineData(2038, 2)] // The locktime takes five bytes in the script from here on.
	[InlineData(2099, 12)]
	public void RedemptionSatisfiesTheScriptOfTheBond(int year, int month)
	{
		using var key = new Key();
		var locktime = BondLocktime.FromMonth(year, month);
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), locktime, confirmed: true);
		var destination = CreateDestination();

		var transaction = BondRedemption.Create(bond, key, destination, FeeRate, locktime.Date.AddSeconds(1), Network);

		var input = Assert.Single(transaction.Inputs);
		var output = Assert.Single(transaction.Outputs);

		Assert.Equal(bond.OutPoint, input.PrevOut);
		Assert.Equal(destination, output.ScriptPubKey);

		// The locktime of the transaction is the one of the bond, and the input does not switch it off.
		Assert.Equal(locktime.UnixTime, transaction.LockTime.Value);
		Assert.True(transaction.LockTime.IsTimeLock);
		Assert.NotEqual(Sequence.Final, input.Sequence);

		// The witness is a signature and the script of the bond.
		Assert.Equal(2, input.WitScript.PushCount);
		Assert.Equal(BondScript.Create(key.PubKey, locktime).ToBytes(), input.WitScript.GetUnsafePush(1));
		Assert.True(transaction.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out var error), error.ToString());

		// The bond pays the mining fee: at least the fee rate that was asked for, and hardly more.
		var fee = bond.Amount - output.Value;
		Assert.True(fee >= FeeRate.GetFee(transaction.GetVirtualSize()));
		Assert.True(fee <= FeeRate.GetFee(transaction.GetVirtualSize() + 1));
	}

	[Fact]
	public void ScriptRefusesAnEarlierLocktimeAndAFinalInput()
	{
		using var key = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), Locktime, confirmed: true);
		var valid = BondRedemption.Create(bond, key, CreateDestination(), FeeRate, Locktime.Date.AddDays(1), Network);

		// Signed again after every change, so that it is the timelock that refuses and not the signature.
		var earlier = Resign(valid, bond, key, tx => tx.LockTime = new LockTime((uint)(Locktime.UnixTime - 1)));
		Assert.False(earlier.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out var error));
		Assert.Equal(ScriptError.UnsatisfiedLockTime, error);

		var byHeight = Resign(valid, bond, key, tx => tx.LockTime = new LockTime(800_000));
		Assert.False(byHeight.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out error));
		Assert.Equal(ScriptError.UnsatisfiedLockTime, error);

		var final = Resign(valid, bond, key, tx => tx.Inputs[0].Sequence = Sequence.Final);
		Assert.False(final.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out error));
		Assert.Equal(ScriptError.UnsatisfiedLockTime, error);

		// A later locktime is fine for the script.
		var later = Resign(valid, bond, key, tx => tx.LockTime = new LockTime((uint)(Locktime.UnixTime + 1)));
		Assert.True(later.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out _));

		// And nobody else's signature will do.
		using var otherKey = new Key();
		var stolen = Resign(valid, bond, otherKey, _ => { });
		Assert.False(stolen.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out _));
	}

	[Fact]
	public void BondIsNotRedeemedBeforeTheChainIsPastItsLocktime()
	{
		using var key = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), Locktime, confirmed: true);
		var destination = CreateDestination();

		// The chain accepts the transaction once its median time is past the locktime, not at the locktime itself.
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(bond, key, destination, FeeRate, Locktime.Date.AddYears(-1), Network));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(bond, key, destination, FeeRate, Locktime.Date.AddSeconds(-1), Network));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(bond, key, destination, FeeRate, Locktime.Date, Network));

		Assert.False(bond.IsRedeemable(Locktime.Date));
		Assert.True(bond.IsRedeemable(Locktime.Date.AddSeconds(1)));
		BondRedemption.Create(bond, key, destination, FeeRate, Locktime.Date.AddSeconds(1), Network);
	}

	[Fact]
	public void UnconfirmedOrSpentBondIsNotRedeemed()
	{
		using var key = new Key();
		var destination = CreateDestination();
		var now = Locktime.Date.AddDays(1);

		var unconfirmed = CreateBond(key.PubKey, Money.Coins(0.5m), Locktime, confirmed: false);
		Assert.False(unconfirmed.IsRedeemable(now));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(unconfirmed, key, destination, FeeRate, now, Network));

		var confirmed = CreateBond(key.PubKey, Money.Coins(0.5m), Locktime, confirmed: true);
		var spender = new SmartTransaction(BondRedemption.Create(confirmed, key, destination, FeeRate, now, Network), Height.Mempool);
		var spent = confirmed with { SpenderTransaction = spender };
		Assert.False(spent.IsRedeemable(now));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(spent, key, destination, FeeRate, now, Network));
	}

	[Fact]
	public void OnlyTheKeyOfTheBondRedeemsIt()
	{
		using var key = new Key();
		using var otherKey = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), Locktime, confirmed: true);

		Assert.Throws<ArgumentException>(() => BondRedemption.Create(bond, otherKey, CreateDestination(), FeeRate, Locktime.Date.AddDays(1), Network));
	}

	[Fact]
	public void BondThatCannotPayItsFeeIsNotRedeemed()
	{
		using var key = new Key();
		var destination = CreateDestination();
		var now = Locktime.Date.AddDays(1);

		// The transaction has about 120 virtual bytes.
		var tiny = CreateBond(key.PubKey, Money.Satoshis(1_000), Locktime, confirmed: true);
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(tiny, key, destination, FeeRate, now, Network));

		// What is left after the fee has to be worth an output.
		var small = CreateBond(key.PubKey, Money.Satoshis(1_600), Locktime, confirmed: true);
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(small, key, destination, FeeRate, now, Network));

		// And the fee does not take more than it leaves, like the fee of a payment.
		var lopsided = CreateBond(key.PubKey, Money.Satoshis(2_600), Locktime, confirmed: true);
		var refusal = Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(lopsided, key, destination, FeeRate, now, Network));
		Assert.Contains("more than", refusal.Message);
		BondRedemption.Create(lopsided, key, destination, new FeeRate(5m), now, Network);

		var enough = CreateBond(key.PubKey, Money.Satoshis(3_000), Locktime, confirmed: true);
		var transaction = BondRedemption.Create(enough, key, destination, FeeRate, now, Network);
		Assert.True(transaction.Outputs[0].Value > Money.Satoshis(330));
	}

	private static Script CreateDestination()
	{
		using var key = new Key();
		return key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
	}

	/// <summary>A bond that some transaction pays to the script of the key and the locktime.</summary>
	private static FidelityBond CreateBond(PubKey pubKey, Money amount, BondLocktime locktime, bool confirmed)
	{
		var funding = Network.CreateTransaction();
		funding.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		funding.Outputs.Add(Money.Coins(0.1m), CreateDestination());
		funding.Outputs.Add(amount, BondScript.GetScriptPubKey(BondScript.Create(pubKey, locktime)));

		var transaction = new SmartTransaction(funding, confirmed ? new Height.ChainHeight(100) : Height.Mempool);
		return new FidelityBond(new OutPoint(funding, 1), amount, locktime, transaction, SpenderTransaction: null);
	}

	/// <summary>A copy of the redemption with a change, signed by the key.</summary>
	private static Transaction Resign(Transaction redemption, FidelityBond bond, Key key, Action<Transaction> change)
	{
		var transaction = redemption.Clone();
		change(transaction);

		var script = new Script(redemption.Inputs[0].WitScript.GetUnsafePush(1));
		var hash = transaction.GetSignatureHash(script, 0, SigHash.All, bond.TxOut, HashVersion.WitnessV0);
		var signature = new TransactionSignature(key.Sign(hash), SigHash.All);
		transaction.Inputs[0].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()), Op.GetPushOp(script.ToBytes()));

		return transaction;
	}
}
