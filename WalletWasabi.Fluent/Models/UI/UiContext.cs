using WalletWasabi.Announcements;
using WalletWasabi.Fluent.Models.ClientConfig;
using WalletWasabi.Fluent.Models.FileSystem;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.Models.UI;

public class UiContext
{
	private INavigate? _navigate;

	public UiContext(
		IServices services,
		QrCodeGenerator qrCodeGenerator,
		QrCodeReader qrCodeReader,
		UiClipboard clipboard,
		WalletRepository walletRepository,
		CoinjoinModel coinJoinModel,
		FileSystemModel fileSystem,
		ClientConfigModel config,
		ApplicationSettings applicationSettings,
		ConnectionsModel connections,
		ReleaseHighlights releaseHighlights)
	{
		Services = services ?? throw new ArgumentNullException(nameof(services));
		QrCodeGenerator = qrCodeGenerator ?? throw new ArgumentNullException(nameof(qrCodeGenerator));
		QrCodeReader = qrCodeReader ?? throw new ArgumentNullException(nameof(qrCodeReader));
		Clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
		WalletRepository = walletRepository ?? throw new ArgumentNullException(nameof(walletRepository));
		CoinjoinModel = coinJoinModel ?? throw new ArgumentNullException(nameof(coinJoinModel));
		FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
		Config = config ?? throw new ArgumentNullException(nameof(config));
		ApplicationSettings = applicationSettings ?? throw new ArgumentNullException(nameof(applicationSettings));
		Connections = connections ?? throw new ArgumentNullException(nameof(connections));
		ReleaseHighlights = releaseHighlights ?? throw new ArgumentNullException(nameof(releaseHighlights));
	}

	public IServices Services { get; }
	public UiClipboard Clipboard { get; }
	public QrCodeGenerator QrCodeGenerator { get; }
	public WalletRepository WalletRepository { get; }
	public CoinjoinModel CoinjoinModel { get; }
	public QrCodeReader QrCodeReader { get; }
	public FileSystemModel FileSystem { get; }
	public ClientConfigModel Config { get; }
	public ApplicationSettings ApplicationSettings { get; }

	/// <summary>Whether Tor, the node, Nostr and the coordinator are configured, and whether they work at the moment.</summary>
	public ConnectionsModel Connections { get; }
	public ReleaseHighlights ReleaseHighlights { get; }
	public MainViewModel? MainViewModel { get; private set; }

	public void RegisterNavigation(INavigate navigate)
	{
		_navigate ??= navigate;
	}

	public INavigate Navigate()
	{
		return _navigate ?? throw new InvalidOperationException($"{GetType().Name} {nameof(Navigate)} hasn't been initialized.");
	}

	public INavigationStack<RoutableViewModel> Navigate(NavigationTarget target)
	{
		return _navigate?.Navigate(target)
			?? throw new InvalidOperationException($"{GetType().Name} {nameof(Navigate)} hasn't been initialized.");
	}

	public void SetMainViewModel(MainViewModel viewModel)
	{
		MainViewModel ??= viewModel;
	}
}
