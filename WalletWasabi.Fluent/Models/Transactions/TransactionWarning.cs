using System.Collections.Generic;
using WalletWasabi.Blockchain.TransactionBuilding;

namespace WalletWasabi.Fluent.Models.Transactions;

// TODO: Case Types should be nested inside the base, and remove the "Warning" Suffix
// Avalonia XAML currently does not support {x:Type} references to nested types (https://github.com/AvaloniaUI/Avalonia/issues/2725)
// Revisit this after Avalonia V11 upgrade

/// <summary>
/// Something that the preview of a transaction tells before the transaction is confirmed, because of what may become
/// of the transaction. What a transaction gives away is not among it: which coins it spends together, how many
/// payments it makes, how much of the balance it takes and whether it has change is what the user chose.
/// </summary>
public abstract record TransactionWarning
{
	/// <summary>Tells what there is to warn about the given transaction. For most transactions that is nothing.</summary>
	public static IReadOnlyList<TransactionWarning> GetWarnings(BuildTransactionResult transaction) =>
		GetWarnings(transaction.SpendsUnconfirmed, transaction.SpendsCoinjoining);

	/// <param name="spendsUnconfirmed">Whether the transaction spends a coin that is not confirmed yet.</param>
	/// <param name="spendsCoinjoining">Whether the transaction spends a coin that is in a coinjoin round.</param>
	public static IReadOnlyList<TransactionWarning> GetWarnings(bool spendsUnconfirmed, bool spendsCoinjoining)
	{
		var warnings = new List<TransactionWarning>();

		if (spendsUnconfirmed)
		{
			warnings.Add(new UnconfirmedFundsWarning());
		}

		if (spendsCoinjoining)
		{
			warnings.Add(new CoinjoiningFundsWarning());
		}

		return warnings;
	}
}

/// <summary>The transaction spends coins that are not confirmed yet: it may be refused, or wait, or cost more.</summary>
public record UnconfirmedFundsWarning : TransactionWarning;

/// <summary>The transaction spends coins that are in a coinjoin round: the coinjoin may take its place.</summary>
public record CoinjoiningFundsWarning : TransactionWarning;
