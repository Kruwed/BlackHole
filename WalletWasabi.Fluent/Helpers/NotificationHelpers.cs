using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.Helpers;

public static class NotificationHelpers
{
	private const int DefaultNotificationTimeout = 10;
	private static WindowNotificationManager? NotificationManager;

	public static void SetNotificationManager(Visual host)
	{
		var notificationManager = new WindowNotificationManager(TopLevel.GetTopLevel(host))
		{
			Position = NotificationPosition.BottomRight,
			MaxItems = 4,
			Margin = new Thickness(0, 0, 15, 40)
		};

		NotificationManager = notificationManager;
	}

	public static void Show(string title, string message, Action? onClick = null)
	{
		if (NotificationManager is { } nm)
		{
			RxApp.MainThreadScheduler.Schedule(() => nm.Show(new Notification(title, message, NotificationType.Information, TimeSpan.FromSeconds(DefaultNotificationTimeout), onClick)));
		}
	}

	/// <summary>Notifies about a transaction of the wallet.</summary>
	public static void Show(ProcessedResult result, Action onClick)
	{
		if (TryGetNotificationInputs(result, out var message))
		{
			Show(UiConstants.WalletTitle, message, onClick);
		}
	}

	public static void Show(object viewModel)
	{
		NotificationManager?.Show(viewModel);
	}

	private static bool TryGetNotificationInputs(ProcessedResult result, [NotNullWhen(true)] out string? message)
	{
		message = null;

		try
		{
			bool isSpent = result.NewlySpentCoins.Count != 0;
			bool isReceived = result.NewlyReceivedCoins.Count != 0;
			bool isConfirmedReceive = result.NewlyConfirmedReceivedCoins.Count != 0;
			bool isConfirmedSpent = result.NewlyConfirmedReceivedCoins.Count != 0;
			Money miningFee = result.Transaction.Transaction.GetFee(result.SpentCoins.Select(x => (ICoin)x.Coin).ToArray()) ?? Money.Zero;
			bool isAccelerator = result.Transaction.IsCPFP;

			if (DescribeBondTransaction(result.LockedInBonds, result.UnlockedFromBonds, isNew: isReceived || isSpent, isConfirmation: isConfirmedReceive || isConfirmedSpent) is { } bondMessage)
			{
				message = bondMessage;
			}
			else if (isReceived || isSpent)
			{
				Money receivedSum = result.NewlyReceivedCoins.Sum(x => x.Amount);
				Money spentSum = result.NewlySpentCoins.Sum(x => x.Amount);
				Money incoming = receivedSum - spentSum;
				Money receiveSpentDiff = incoming.Abs();
				string amountString = receiveSpentDiff.ToFormattedString();

				if (result.Transaction.Transaction.IsCoinBase)
				{
					message = $"{amountString} BTC received as Coinbase reward";
				}
				else if (isSpent && receiveSpentDiff == miningFee)
				{
					message = isAccelerator ? "Accelerator transaction" : $"Self transfer";
				}
				else if (incoming > Money.Zero)
				{
					message = $"{amountString} BTC incoming";
				}
				else if (incoming < Money.Zero)
				{
					var sentAmount = receiveSpentDiff - miningFee;
					message = $"{sentAmount.ToFormattedString()} BTC sent";
				}
			}
			else if (isConfirmedReceive || isConfirmedSpent)
			{
				Money receivedSum = result.ReceivedCoins.Sum(x => x.Amount);
				Money spentSum = result.SpentCoins.Sum(x => x.Amount);
				Money incoming = receivedSum - spentSum;
				Money receiveSpentDiff = incoming.Abs();
				string amountString = receiveSpentDiff.ToFormattedString();

				if (isConfirmedSpent && receiveSpentDiff == miningFee)
				{
					message = isAccelerator ? "Accelerator transaction confirmed" : $"Self transfer confirmed";
				}
				else if (incoming > Money.Zero)
				{
					message = $"Receiving {amountString} BTC has been confirmed";
				}
				else if (incoming < Money.Zero)
				{
					var sentAmount = receiveSpentDiff - miningFee;
					message = $"{sentAmount.ToFormattedString()} BTC sent got confirmed";
				}
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(ex);
		}

		return message is { };
	}

	/// <summary>
	/// What to tell about a transaction that puts coins into a fidelity bond of the wallet or takes them out of one. Those
	/// coins are neither sent nor received: they are the wallet's before and after. Null for any other transaction.
	/// </summary>
	/// <param name="isNew">The wallet sees the transaction for the first time.</param>
	/// <param name="isConfirmation">The wallet knew the transaction, and now it is confirmed.</param>
	public static string? DescribeBondTransaction(Money lockedInBonds, Money unlockedFromBonds, bool isNew, bool isConfirmation)
	{
		var isLock = lockedInBonds > Money.Zero;

		if (!isLock && unlockedFromBonds <= Money.Zero)
		{
			return null;
		}

		var amount = (isLock ? lockedInBonds : unlockedFromBonds).ToFormattedString();

		return (isLock, isNew, isConfirmation) switch
		{
			(true, true, _) => $"{amount} BTC locked in a fidelity bond",
			(false, true, _) => $"{amount} BTC unlocked from a fidelity bond",
			(true, false, true) => $"Locking {amount} BTC in a fidelity bond has been confirmed",
			(false, false, true) => $"Unlocking {amount} BTC from a fidelity bond has been confirmed",

			// Nothing new about the coins of the wallet: nothing to tell.
			_ => null
		};
	}
}
