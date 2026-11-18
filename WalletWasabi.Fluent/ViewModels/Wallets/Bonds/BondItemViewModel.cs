using System.Globalization;
using System.Windows.Input;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Bonds;

/// <summary>
/// A fidelity bond in the list of the bonds of the wallet.
/// </summary>
public class BondItemViewModel
{
	/// <param name="chainHeight">The height of the chain, if the node told it. It tells how far the unlock height is.</param>
	/// <param name="now">The time by the clock, from which the day of the unlock height is estimated.</param>
	/// <param name="redeem">Starts the redemption of the bond.</param>
	public BondItemViewModel(FidelityBondModel model, uint? chainHeight, DateTimeOffset now, Action<FidelityBondModel> redeem)
	{
		Model = model;
		Amount = new Amount(model.Bond.Amount);
		Address = model.Address.ToString();
		State = model.State;
		HeightText = GetHeightText(model, chainHeight, now);
		StateText = GetStateText(model, chainHeight);
		ValueText = model.Value is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : null;
		IsRedeemable = model.State == FidelityBondState.Unlocked;
		IsOver = model.State is FidelityBondState.Redeeming or FidelityBondState.Redeemed;
		RedeemCommand = ReactiveCommand.Create(() => redeem(model));
	}

	public FidelityBondModel Model { get; }

	public Amount Amount { get; }

	public string Address { get; }

	/// <summary>Tells the block height that the bond unlocks or unlocked at, and when that is expected as long as it is ahead.</summary>
	public string HeightText { get; }

	public FidelityBondState State { get; }

	public string StateText { get; }

	/// <summary>What the bond counts for, or null as long as that cannot be told.</summary>
	public string? ValueText { get; }

	public bool IsRedeemable { get; }

	/// <summary>Whether the bond is redeemed or about to be: it is history.</summary>
	public bool IsOver { get; }

	public ICommand RedeemCommand { get; }

	private static string GetHeightText(FidelityBondModel model, uint? chainHeight, DateTimeOffset now)
	{
		var unlockHeight = model.Bond.UnlockHeight;
		var height = BlockHeightText.Format(unlockHeight);

		return model.State switch
		{
			FidelityBondState.Unlocked => $"Unlocked since block {height}",
			FidelityBondState.Redeeming or FidelityBondState.Redeemed => $"Was locked until block {height}",
			_ when chainHeight is { } known && unlockHeight > known =>
				$"Unlocks at block {height}, expected around {BlockHeightText.DescribeExpectedTime(unlockHeight, known, now)}",
			_ => $"Unlocks at block {height}"
		};
	}

	private static string GetStateText(FidelityBondModel model, uint? chainHeight) =>
		model.State switch
		{
			FidelityBondState.Unconfirmed => "Waiting for confirmation",
			FidelityBondState.Locked when chainHeight is { } known && model.Bond.UnlockHeight > known =>
				$"Locked for {BlockHeightText.Count(model.Bond.UnlockHeight - known)}",
			FidelityBondState.Locked => "Locked",
			FidelityBondState.Unlocked => "Unlocked",
			FidelityBondState.Redeeming => "Redemption waits for confirmation",
			FidelityBondState.Redeemed => "Redeemed",
			_ => ""
		};
}
