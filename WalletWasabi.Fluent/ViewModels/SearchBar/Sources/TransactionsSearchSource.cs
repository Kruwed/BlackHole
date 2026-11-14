using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.ViewModels.NavBar;
using WalletWasabi.Fluent.ViewModels.SearchBar.Patterns;
using WalletWasabi.Fluent.ViewModels.SearchBar.SearchItems;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History.HistoryItems;

namespace WalletWasabi.Fluent.ViewModels.SearchBar.Sources;

public class TransactionsSearchSource : ReactiveObject, ISearchSource, IDisposable
{
	private const int MaxResultCount = 5;
	private const int MinQueryLength = 3;

	private readonly CompositeDisposable _disposables = new();
	private readonly NavBarViewModel _navBarViewModel;

	public TransactionsSearchSource(NavBarViewModel navBarViewModel, IObservable<string> queries)
	{
		_navBarViewModel = navBarViewModel;

#pragma warning disable CA2000 // Dispose objects before losing scope - disposed with the disposables
		var sourceCache = new SourceCache<ISearchItem, ComposedKey>(x => x.Key)
			.DisposeWith(_disposables);
#pragma warning restore CA2000 // Dispose objects before losing scope

		var results = queries
			.Select(query => query.Length >= MinQueryLength ? Search(query) : [])
			.ObserveOn(RxApp.MainThreadScheduler);

		sourceCache
			.RefillFrom(results)
			.DisposeWith(_disposables);

		Changes = sourceCache.Connect();
	}

	public void Dispose()
	{
		_disposables.Dispose();
	}

	public IObservable<IChangeSet<ISearchItem, ComposedKey>> Changes { get; }

	private static bool ContainsId(HistoryItemViewModelBase historyItemViewModelBase, string queryStr)
	{
		return historyItemViewModelBase.Transaction.Id.ToString().Contains(queryStr, StringComparison.CurrentCultureIgnoreCase);
	}

	private static Task NavigateTo(WalletViewModel wallet, HistoryItemViewModelBase item)
	{
		wallet.SelectTransaction(item.Transaction.Id);

		return Task.CompletedTask;
	}

	private static string GetIcon(HistoryItemViewModelBase historyItemViewModelBase)
	{
		return historyItemViewModelBase switch
		{
			CoinJoinHistoryItemViewModel => "shield_regular",
			CoinJoinsHistoryItemViewModel => "shield_regular",
			TransactionHistoryItemViewModel => "normal_transaction",
			_ => ""
		};
	}

	private ISearchItem ToSearchItem(WalletViewModel wallet, HistoryItemViewModelBase item)
	{
		return new ActionableItem(
			item.Transaction.Id.ToString(),
			"Found in the transaction history",
			() => NavigateTo(wallet, item),
			"Transactions",
			new List<string>())
		{
			Icon = GetIcon(item)
		};
	}

	/// <summary>The transactions of the wallet, including the coinjoins that the history shows in groups. Empty until the wallet is loaded.</summary>
	private IEnumerable<(WalletViewModel Wallet, HistoryItemViewModelBase Transaction)> GetTransactions()
	{
		// TODO: This is a workaround to get all the transactions of the loaded wallet. REMOVE after UIDecoupling #26

		if (_navBarViewModel.Wallet is not { IsLoggedIn: true, Wallet.Loaded: true, WalletViewModel: { } wallet })
		{
			return [];
		}

		return wallet.History.Transactions
			.Concat(wallet.History.Transactions.OfType<CoinJoinsHistoryItemViewModel>().SelectMany(y => y.Children))
			.Select(transaction => (wallet, transaction));
	}

	private IEnumerable<ISearchItem> Search(string query)
	{
		return Filter(query)
			.Take(MaxResultCount)
			.Select(tuple => ToSearchItem(tuple.Item1, tuple.Item2));
	}

	private IEnumerable<(WalletViewModel, HistoryItemViewModelBase)> Filter(string queryStr)
	{
		return GetTransactions()
		.Where(tuple => TryParseBitcoinAddress(tuple.Item1.WalletModel.Network, queryStr, out var address) ?
			ContainsDestinationAddress(tuple.Item1, tuple.Item2, address) :
			ContainsId(tuple.Item2, queryStr));
	}

	private static bool ContainsDestinationAddress(WalletViewModel walletViewModel, HistoryItemViewModelBase historyItem, BitcoinAddress address)
	{
		var txid = historyItem.Transaction.Id;
		return walletViewModel.WalletModel.Transactions.GetDestinationAddresses(txid).Contains(address);
	}

	private bool TryParseBitcoinAddress(Network network, string queryStr, [NotNullWhen(true)] out BitcoinAddress? address)
	{
		address = null;
		try
		{
			address = BitcoinAddress.Create(queryStr, network);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}
}
