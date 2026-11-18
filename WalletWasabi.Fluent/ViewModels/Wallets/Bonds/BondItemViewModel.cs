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
	/// <param name="chainTime">The time of the chain, if the node told it. The timelock of a bond is over by this time, not by the clock.</param>
	/// <param name="redeem">Starts the redemption of the bond.</param>
	public BondItemViewModel(FidelityBondModel model, DateTimeOffset? chainTime, Action<FidelityBondModel> redeem)
	{
		Model = model;
		Amount = new Amount(model.Bond.Amount);
		Address = model.Address.ToString();
		DateText = GetDateText(model);
		State = model.State;
		StateText = GetStateText(model, chainTime);
		ValueText = model.Value is { } value ? value.ToString("N0", CultureInfo.InvariantCulture) : null;
		IsRedeemable = model.State == FidelityBondState.Unlocked;
		IsOver = model.State is FidelityBondState.Redeeming or FidelityBondState.Redeemed;
		RedeemCommand = ReactiveCommand.Create(() => redeem(model));
	}

	public FidelityBondModel Model { get; }

	public Amount Amount { get; }

	public string Address { get; }

	/// <summary>Tells the day that the bond unlocks or unlocked. It is the start of that day in UTC.</summary>
	public string DateText { get; }

	public FidelityBondState State { get; }

	public string StateText { get; }

	/// <summary>What the bond counts for, or null as long as that cannot be told.</summary>
	public string? ValueText { get; }

	public bool IsRedeemable { get; }

	/// <summary>Whether the bond is redeemed or about to be: it is history.</summary>
	public bool IsOver { get; }

	public ICommand RedeemCommand { get; }

	public static string FormatDate(DateTimeOffset date) => date.ToUniversalTime().ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

	private static string GetDateText(FidelityBondModel model)
	{
		var date = FormatDate(model.Bond.Locktime.Date);

		return model.State switch
		{
			FidelityBondState.Unlocked => $"Unlocked since {date}, 00:00 UTC",
			FidelityBondState.Redeeming or FidelityBondState.Redeemed => $"Was locked until {date}",
			_ => $"Unlocks on {date}, 00:00 UTC"
		};
	}

	private static string GetStateText(FidelityBondModel model, DateTimeOffset? chainTime) =>
		model.State switch
		{
			FidelityBondState.Unconfirmed => "Waiting for confirmation",
			FidelityBondState.Locked when chainTime is { } time => $"Locked for {DurationText.Between(time, model.Bond.Locktime.Date)}",
			FidelityBondState.Locked => "Locked",
			FidelityBondState.Unlocked => "Unlocked",
			FidelityBondState.Redeeming => "Redemption waits for confirmation",
			FidelityBondState.Redeemed => "Redeemed",
			_ => ""
		};
}
