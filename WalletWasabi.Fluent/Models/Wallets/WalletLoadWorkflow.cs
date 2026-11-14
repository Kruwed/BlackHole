using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Logging;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;


/// <param name="Target"><c>null</c> means that no value is available yet.</param>
public record SyncProgressCardModel(uint Initial, uint Current, uint? Target)
{
	public bool IsTargetKnown => Target is not null;

	public double Percent => Target switch
	{
		null => 0,
		{ } target when target <= Initial => Current >= target ? 100 : 0,
		{ } target => Math.Clamp(100.0 * ((double)Current - Initial) / (target - Initial), 0, 100)
	};

	public bool IsComplete => IsTargetKnown && Percent >= 100;
}

/// <param name="Status">What the wallet is doing to catch up with the user's node.</param>
/// <param name="NodeBlocks">The blocks that the node has, of those that it knows about.</param>
/// <param name="Blocks">The blocks that the wallet processed, of those that the chain has.</param>
public record WalletLoadProgress(
	WalletSyncStatus Status,
	SyncProgressCardModel NodeBlocks,
	SyncProgressCardModel Blocks);

public partial class WalletLoadWorkflow
{
	private readonly IServices _services;
	private readonly CompositeDisposable _disposables = new();
	private readonly Wallet _wallet;
	private readonly Subject<WalletLoadProgress> _progress;
	[AutoNotify] private bool _isLoading;

	/// <summary>The progress of processing the blocks is measured from here. A new wallet learns its height from the node.</summary>
	private uint? _walletStartHeight;

	public WalletLoadWorkflow(IServices services, Wallet wallet)
	{
		_services = services;
		_wallet = wallet;
		_progress = new();
		_walletStartHeight = wallet.KeyManager.GetBestHeight()?.Height;

		UpdateProgress();

		LoadCompleted = services.EventBus.AsObservable<WalletLoaded>()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Where(x => x.Wallet == _wallet)
			.Select(x => x.Wallet.Loaded)
			.ToSignal();
	}

	public IObservable<WalletLoadProgress> Progress => _progress;

	public IObservable<Unit> LoadCompleted { get; }

	public void Start()
	{
		Observable.FromAsync(() => Task.CompletedTask)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Take(1)
			.SubscribeAsync(_ => LoadWalletAsync())
			.DisposeWith(_disposables);

		Observable.Interval(TimeSpan.FromSeconds(1))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ => UpdateProgress())
			.DisposeWith(_disposables);
	}

	public void Stop()
	{
		_disposables.Dispose();
	}

	private async Task LoadWalletAsync()
	{
		IsLoading = true;

		try
		{
			await Task.Run(async () => await _services.StartWalletAsync(_wallet));
		}
		catch (OperationCanceledException ex)
		{
			Logger.LogTrace(ex);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private void UpdateProgress()
	{
		var node = _services.GetNodeStatus();
		var walletHeight = _wallet.KeyManager.GetBestHeight()?.Height;
		_walletStartHeight ??= walletHeight;

		// The node knows the height of the chain from the headers, also while it is still downloading the blocks.
		uint? chainTip = node is not null ? (uint)Math.Max(node.Headers, node.Blocks) : null;

		_progress.OnNext(new WalletLoadProgress(
			Status: _wallet.Synchronizer.Status,
			NodeBlocks: new SyncProgressCardModel(0, (uint)(node?.Blocks ?? 0), chainTip),
			Blocks: new SyncProgressCardModel(_walletStartHeight ?? 0, walletHeight ?? 0, walletHeight is null ? null : chainTip)));
	}
}
