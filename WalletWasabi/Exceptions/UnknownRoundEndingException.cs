using System.Collections.Immutable;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace WalletWasabi.Exceptions;

public class UnknownRoundEndingException : Exception
{
	public UnknownRoundEndingException(ImmutableList<SmartCoin> coins, Exception exception) :
		base(
			$"Round was not ended properly, reason '{exception.Message}'.",
			innerException: exception)
	{
		Coins = coins;
	}

	public ImmutableList<SmartCoin> Coins { get; }
}
