using NBitcoin;
using ReactiveUI;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Extensions;

namespace WalletWasabi.Fluent.Models.Wallets;

public class Address : ReactiveObject, IAddress
{
	private readonly Action<Address> _onHide;

	public Address(KeyManager keyManager, HdPubKey hdPubKey, Action<Address> onHide)
	{
		KeyManager = keyManager;
		HdPubKey = hdPubKey;
		Network = keyManager.GetNetwork();
		HdFingerprint = KeyManager.MasterFingerprint;
		BitcoinAddress = HdPubKey.GetAddress(Network);
		_onHide = onHide;
	}

	public KeyManager KeyManager { get; }
	public HdPubKey HdPubKey { get; }
	public Network Network { get; }
	public HDFingerprint? HdFingerprint { get; }
	public BitcoinAddress BitcoinAddress { get; }
	public LabelsArray Labels => HdPubKey.Labels;
	public PubKey PubKey => HdPubKey.PubKey;
	public KeyPath FullKeyPath => HdPubKey.FullKeyPath;
	public string Text => BitcoinAddress.ToString();
	public string ShortenedText => ShortenAddress(BitcoinAddress.ToString());

	public void Hide()
	{
		_onHide(this);
	}

	public void SetLabels(LabelsArray labels)
	{
		HdPubKey.SetLabel(labels, KeyManager);
		this.RaisePropertyChanged(nameof(Labels));
	}

	public static string ShortenAddress(string input)
	{
		if (input.Length <= 47)
		{
			return input;
		}

		return $"{input[..21]}...{input[^20..]}";
	}

	public override int GetHashCode() => Text.GetHashCode();

	public override bool Equals(object? obj)
	{
		return obj is IAddress address && Equals(address);
	}

	protected bool Equals(IAddress other) => Text.Equals(other.Text);
}
