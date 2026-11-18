using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// A fidelity bond is redeemed by a transaction that its script accepts, and only once the chain has reached its unlock height.
/// </summary>
public class BondRedemptionTests
{
	private const uint UnlockHeight = 880_000;
	private static readonly Network Network = Network.RegTest;
	private static readonly FeeRate FeeRate = new(12m);

	[Theory]
	[InlineData(1u)]
	[InlineData(16u)]
	[InlineData(17u)]
	[InlineData(150u)]
	[InlineData(880_000u)]
	[InlineData(8_388_608u)] // The height takes five bytes in the script from here on.
	[InlineData(499_999_999u)]
	public void RedemptionSatisfiesTheScriptOfTheBond(uint unlockHeight)
	{
		using var key = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), unlockHeight, confirmed: true);
		var destination = CreateDestination();

		var transaction = BondRedemption.Create(bond, key, destination, FeeRate, chainHeight: unlockHeight, Network);

		var input = Assert.Single(transaction.Inputs);
		var output = Assert.Single(transaction.Outputs);

		Assert.Equal(bond.OutPoint, input.PrevOut);
		Assert.Equal(destination, output.ScriptPubKey);

		// The locktime of the transaction is the height of the bond, and the input does not switch it off.
		Assert.Equal(unlockHeight, transaction.LockTime.Value);
		Assert.True(transaction.LockTime.IsHeightLock);
		Assert.NotEqual(Sequence.Final, input.Sequence);

		// The witness is a signature, the script of the bond, and the proof that the output commits to the script.
		var leaf = BondScript.CreateLeaf(key.PubKey, unlockHeight);
		Assert.Equal(3, input.WitScript.PushCount);
		Assert.Equal(64, input.WitScript.GetUnsafePush(0).Length);
		Assert.Equal(leaf.ToBytes(), input.WitScript.GetUnsafePush(1));
		Assert.Equal(33, input.WitScript.GetUnsafePush(2).Length);
		Assert.Equal(BondScript.UnspendableKey.ToBytes(), input.WitScript.GetUnsafePush(2)[1..]);
		Assert.True(Verify(transaction, bond, out var error), error.ToString());

		// The bond pays the mining fee: at least the fee rate that was asked for, and hardly more.
		var fee = bond.Amount - output.Value;
		Assert.True(fee >= FeeRate.GetFee(transaction.GetVirtualSize()));
		Assert.True(fee <= FeeRate.GetFee(transaction.GetVirtualSize() + 1));
	}

	[Fact]
	public void ScriptRefusesALowerLocktimeAndAFinalInput()
	{
		using var key = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), UnlockHeight, confirmed: true);
		var valid = BondRedemption.Create(bond, key, CreateDestination(), FeeRate, UnlockHeight, Network);

		// Signed again after every change, so that it is the timelock that refuses and not the signature.
		var lower = Resign(valid, bond, key, tx => tx.LockTime = new LockTime(UnlockHeight - 1));
		Assert.False(Verify(lower, bond, out var error));
		Assert.Equal(ScriptError.UnsatisfiedLockTime, error);

		var byTime = Resign(valid, bond, key, tx => tx.LockTime = new LockTime(1_800_000_000));
		Assert.False(Verify(byTime, bond, out error));
		Assert.Equal(ScriptError.UnsatisfiedLockTime, error);

		var final = Resign(valid, bond, key, tx => tx.Inputs[0].Sequence = Sequence.Final);
		Assert.False(Verify(final, bond, out error));
		Assert.Equal(ScriptError.UnsatisfiedLockTime, error);

		// A higher locktime is fine for the script.
		var higher = Resign(valid, bond, key, tx => tx.LockTime = new LockTime(UnlockHeight + 1));
		Assert.True(Verify(higher, bond, out _));

		// And nobody else's signature will do.
		using var otherKey = new Key();
		var stolen = Resign(valid, bond, otherKey, _ => { });
		Assert.False(Verify(stolen, bond, out _));
	}

	[Fact]
	public void BondHasNoWayAroundItsScript()
	{
		using var key = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), UnlockHeight, confirmed: true);
		var valid = BondRedemption.Create(bond, key, CreateDestination(), FeeRate, UnlockHeight, Network);

		// A signature of the key of the bond alone, as for an output that the key has by itself, does not spend it.
		var keyPath = valid.Clone();
		var hash = keyPath.GetSignatureHashTaproot([bond.TxOut], new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default });
		keyPath.Inputs[0].WitScript = new WitScript(Op.GetPushOp(key.SignTaprootKeySpend(hash, TaprootSigHash.Default).ToBytes()));

		Assert.False(Verify(keyPath, bond, out _));
	}

	[Fact]
	public void BondIsNotRedeemedBeforeTheChainReachesItsHeight()
	{
		using var key = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), UnlockHeight, confirmed: true);
		var destination = CreateDestination();

		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(bond, key, destination, FeeRate, chainHeight: 1, Network));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(bond, key, destination, FeeRate, chainHeight: UnlockHeight - 1, Network));

		// With the block of the unlock height the transaction fits into the next block, and the node takes it.
		Assert.False(bond.IsRedeemable(UnlockHeight - 1));
		Assert.True(bond.IsRedeemable(UnlockHeight));
		Assert.True(bond.IsRedeemable(UnlockHeight + 1_000));
		BondRedemption.Create(bond, key, destination, FeeRate, chainHeight: UnlockHeight, Network);
	}

	[Fact]
	public void UnconfirmedOrSpentBondIsNotRedeemed()
	{
		using var key = new Key();
		var destination = CreateDestination();

		var unconfirmed = CreateBond(key.PubKey, Money.Coins(0.5m), UnlockHeight, confirmed: false);
		Assert.False(unconfirmed.IsRedeemable(UnlockHeight));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(unconfirmed, key, destination, FeeRate, UnlockHeight, Network));

		var confirmed = CreateBond(key.PubKey, Money.Coins(0.5m), UnlockHeight, confirmed: true);
		var spender = new SmartTransaction(BondRedemption.Create(confirmed, key, destination, FeeRate, UnlockHeight, Network), Height.Mempool);
		var spent = confirmed with { SpenderTransaction = spender };
		Assert.False(spent.IsRedeemable(UnlockHeight));
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(spent, key, destination, FeeRate, UnlockHeight, Network));
	}

	[Fact]
	public void OnlyTheKeyOfTheBondRedeemsIt()
	{
		using var key = new Key();
		using var otherKey = new Key();
		var bond = CreateBond(key.PubKey, Money.Coins(0.5m), UnlockHeight, confirmed: true);

		Assert.Throws<ArgumentException>(() => BondRedemption.Create(bond, otherKey, CreateDestination(), FeeRate, UnlockHeight, Network));
	}

	[Fact]
	public void BondThatCannotPayItsFeeIsNotRedeemed()
	{
		using var key = new Key();
		var destination = CreateDestination();

		// The transaction has about 130 virtual bytes.
		var tiny = CreateBond(key.PubKey, Money.Satoshis(1_000), UnlockHeight, confirmed: true);
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(tiny, key, destination, FeeRate, UnlockHeight, Network));

		// What is left after the fee has to be worth an output.
		var small = CreateBond(key.PubKey, Money.Satoshis(1_700), UnlockHeight, confirmed: true);
		Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(small, key, destination, FeeRate, UnlockHeight, Network));

		// And the fee does not take more than it leaves, like the fee of a payment.
		var lopsided = CreateBond(key.PubKey, Money.Satoshis(2_600), UnlockHeight, confirmed: true);
		var refusal = Assert.Throws<InvalidOperationException>(() => BondRedemption.Create(lopsided, key, destination, FeeRate, UnlockHeight, Network));
		Assert.Contains("more than", refusal.Message);
		BondRedemption.Create(lopsided, key, destination, new FeeRate(5m), UnlockHeight, Network);

		var enough = CreateBond(key.PubKey, Money.Satoshis(3_200), UnlockHeight, confirmed: true);
		var transaction = BondRedemption.Create(enough, key, destination, FeeRate, UnlockHeight, Network);
		Assert.True(transaction.Outputs[0].Value > Money.Satoshis(330));
	}

	private static bool Verify(Transaction transaction, FidelityBond bond, out ScriptError error) =>
		transaction.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, transaction.PrecomputeTransactionData([bond.TxOut]), out error);

	private static Script CreateDestination()
	{
		using var key = new Key();
		return key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
	}

	/// <summary>A bond that some transaction pays to the script of the key and the height.</summary>
	private static FidelityBond CreateBond(PubKey pubKey, Money amount, uint unlockHeight, bool confirmed)
	{
		var funding = Network.CreateTransaction();
		funding.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		funding.Outputs.Add(Money.Coins(0.1m), CreateDestination());
		funding.Outputs.Add(amount, BondScript.GetScriptPubKey(BondScript.CreateLeaf(pubKey, unlockHeight)));

		var transaction = new SmartTransaction(funding, confirmed ? new Height.ChainHeight(100) : Height.Mempool);
		return new FidelityBond(new OutPoint(funding, 1), amount, unlockHeight, transaction, SpenderTransaction: null);
	}

	/// <summary>A copy of the redemption with a change, signed by the key for the script of the bond.</summary>
	private static Transaction Resign(Transaction redemption, FidelityBond bond, Key key, Action<Transaction> change)
	{
		var transaction = redemption.Clone();
		change(transaction);

		var witness = redemption.Inputs[0].WitScript;
		var leaf = BondScript.ToTapScript(new Script(witness.GetUnsafePush(1)));
		var hash = transaction.GetSignatureHashTaproot([bond.TxOut], new TaprootExecutionData(0, leaf.LeafHash) { SigHash = TaprootSigHash.Default });
		var signature = key.SignTaprootScriptSpend(hash, TaprootSigHash.Default);
		transaction.Inputs[0].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()), Op.GetPushOp(witness.GetUnsafePush(1)), Op.GetPushOp(witness.GetUnsafePush(2)));

		return transaction;
	}
}
