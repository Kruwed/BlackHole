using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

[NavigationMetaData(Title = "")]
public partial class LoadingViewModel : RoutableViewModel
{
	private readonly IWalletModel _wallet;

	[AutoNotify] private bool _isLoading;
	[AutoNotify] private string _status = "";

	// Sync progress card models (initial, current, target - percent/isComplete are calculated)
	[AutoNotify] private SyncProgressCardModel _nodeBlocks = new(0, 0, null);
	[AutoNotify] private SyncProgressCardModel _blocks = new(0, 0, null);

	public LoadingViewModel(UiContext uiContext, IWalletModel wallet) : base(uiContext)
	{
		_wallet = wallet;
	}

	public string WalletTitle => UiConstants.WalletTitle;

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		_wallet.Loader.Progress
			.Do(p => UpdateStatus(p))
			.Subscribe()
			.DisposeWith(disposables);
	}

	private void UpdateStatus(WalletLoadProgress progress)
	{
		NodeBlocks = progress.NodeBlocks;
		Blocks = progress.Blocks;
		Status = progress.Status.ToUserFriendlyString();
	}
}
