using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Wallets.SilentPayment;

namespace WalletWasabi.Blockchain.TransactionBuilding;

public abstract record Destination
{
	public static implicit operator Destination(BitcoinAddress address) => new Loudly(address.ScriptPubKey);
	public static implicit operator Destination(Script scriptPubKey) => new Loudly(scriptPubKey);

	public string ToString(Network network) =>
		this switch
		{
			Loudly l when l.ScriptPubKey.GetDestinationAddress(network) is {} addr => addr.ToString(),
			Silent s => s.Address.ToWip(network),
			_ => throw new ArgumentException("Unknown destination type")
		};

	public Script GetScriptPubKey() =>
		this switch
		{
			Loudly l => l.ScriptPubKey,
			Silent s => s.FakeScriptPubKey,
			_ => throw new ArgumentException("Unknown destination type")
		};

	public record Loudly(Script ScriptPubKey) : Destination;

	public record Silent(SilentPaymentAddress Address) : Destination
	{
		public Script FakeScriptPubKey =>
			new PubKey(Address.ScanKey.AddTweak(Address.SpendKey.ToXOnlyPubKey().ToBytes()).ToBytes()).GetScriptPubKey(
				ScriptPubKeyType.TaprootBIP86);
	};
}

public class DestinationRequest(Destination destination, MoneyRequest amount)
{
	public DestinationRequest(Script scriptPubKey, Money amount, bool subtractFee = false)
		: this(scriptPubKey, MoneyRequest.Create(amount, subtractFee))
	{
	}

	public DestinationRequest(Script scriptPubKey, MoneyRequest amount)
		: this(new Destination.Loudly(scriptPubKey), amount)
	{
	}

	public DestinationRequest(IDestination destination, Money amount, bool subtractFee = false)
		: this(new Destination.Loudly(destination.ScriptPubKey), MoneyRequest.Create(amount, subtractFee))
	{
	}

	public DestinationRequest(IDestination destination, MoneyRequest amount)
		: this(new Destination.Loudly(destination.ScriptPubKey), amount)
	{
	}

	public DestinationRequest(SilentPaymentAddress silentPaymentAddress, MoneyRequest amount)
		: this(new Destination.Silent(silentPaymentAddress), amount)
	{
	}

	public Destination Destination { get; } = destination;
	public MoneyRequest Amount { get; } = amount;
}
