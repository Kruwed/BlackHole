using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Coins;

/// <summary>
/// The row of a coin in a list of coins. The row is made once: apart from whether the coin is kept out of coinjoins and
/// whether it is in one, which it follows, it shows the coin as it was at that moment. <see cref="Content"/> tells the
/// list when a coin needs a new row.
/// </summary>
public partial class CoinViewModel : ViewModelBase, IDisposable
{
	private readonly CompositeDisposable _disposables = new();

	[AutoNotify] private bool _isSelected;
	[AutoNotify] private bool _isCoinjoining;
	[AutoNotify] private bool _isExcludedFromCoinJoin;
	[AutoNotify] private bool _canBeSelected;

	public CoinViewModel(UiContext uiContext, CoinModel coin, bool canSelectWhenCoinjoining) : base(uiContext)
	{
		// Temporarily enable the selection no matter what.
		// Should be again restricted once https://github.com/WalletWasabi/WalletWasabi/issues/9972 is implemented.
		// CanBeSelected = !IsCoinjoining;
		CanBeSelected = true;

		ClipboardCopyCommand = ReactiveCommand.CreateFromTask<string>(text => UiContext.Clipboard.SetTextAsync(text));

		Coin = coin;
		Content = DescribeContent(coin.GetSmartCoin());
		BtcAddress = coin.BtcAddress;
		Amount = new Amount(coin.Amount);
		IsConfirmed = coin.IsConfirmed;
		IsBanned = coin.IsBanned;
		var confirmationCount = coin.Confirmations;
		ConfirmationStatus = $"{confirmationCount} confirmation{TextHelpers.AddSIfPlural((int)confirmationCount)}";
		BannedUntilUtcToolTip = coin.BannedUntilUtcToolTip;
		AnonymityPoints = coin.AnonymityPoints;
		BannedUntilUtc = coin.BannedUntilUtc;
		this.WhenAnyValue(x => x.Coin.IsExcludedFromCoinJoin).BindTo(this, x => x.IsExcludedFromCoinJoin).DisposeWith(_disposables);
		this.WhenAnyValue(x => x.Coin.IsCoinJoinInProgress).BindTo(this, x => x.IsCoinjoining).DisposeWith(_disposables);
		this.WhenAnyValue(x => x.CanBeSelected)
			.Where(b => !b)
			.Do(_ => IsSelected = false)
			.Subscribe();

		if (!canSelectWhenCoinjoining)
		{
			this.WhenAnyValue(x => x.Coin.IsCoinJoinInProgress, b => !b).BindTo(this, x => x.CanBeSelected).DisposeWith(_disposables);
		}
	}

	public CoinModel Coin { get; }

	public ICommand ClipboardCopyCommand { get; }

	public string? BtcAddress { get; }

	public bool IsConfirmed { get; }

	public bool IsBanned { get; }

	public string ConfirmationStatus { get; }

	public Amount Amount { get; }

	public string? BannedUntilUtcToolTip { get; }

	/// <summary>The anonymity points of the coin, or null if the wallet has not found them out yet.</summary>
	public int? AnonymityPoints { get; }

	public string AnonymityPointsText => DescribeAnonymityPoints(AnonymityPoints);

	public DateTimeOffset? BannedUntilUtc { get; }

	/// <summary>
	/// What the row was made of. The list keeps the row of a coin for as long as the coin gives the same content, and
	/// puts a new row in its place otherwise.
	/// </summary>
	public string Content { get; }

	/// <summary>
	/// Tells what a row shows of its coin and does not follow: the amount, whether the coin is confirmed or banned and
	/// its anonymity points.
	/// </summary>
	public static string DescribeContent(SmartCoin coin) =>
		FormattableString.Invariant($"{coin.Outpoint}:{coin.Amount.Satoshi}:{coin.Confirmed}:{coin.AnonymityPoints}:{coin.IsBanned}");

	/// <summary>What a row shows as the anonymity points of its coin: the number, or a question mark while the wallet does not know it.</summary>
	public static string DescribeAnonymityPoints(int? points) =>
		points?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";

	public void Dispose() => _disposables.Dispose();
}
