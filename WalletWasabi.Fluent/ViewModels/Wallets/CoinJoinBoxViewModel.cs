using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

/// <summary>
/// The box of one kind of coinjoin on the Coins tab: its name, what its coinjoin is doing, and Play and Stop. Every kind
/// has a box of its own, and a round of either gets its coins from the list of the tab.
/// </summary>
[AppLifetime]
public partial class CoinJoinBoxViewModel : ViewModelBase
{
	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinStateViewModel? _state;

	/// <param name="startRound">Starts a round of the given kind with the coins that are ticked in the list of the tab.</param>
	public CoinJoinBoxViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinJoinProtocol protocol, Func<CoinJoinProtocol, Task> startRound) : base(uiContext)
	{
		Protocol = protocol;
		Name = GetName(protocol);
		IsCoinJoinEnabled = walletModel.IsCoinJoinEnabled;

		// The controls come with the loaded wallet: what the wallet knows about its coins is not complete until it has
		// processed the blocks of the node. Nothing starts a round but their Play button.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.Where(isLoaded => isLoaded && State is null && walletModel.Coinjoin is not null)
			.Subscribe(_ => State = new CoinJoinStateViewModel(uiContext, walletModel, wallet, walletModel.Coinjoin!, protocol, () => startRound(protocol)));

		// The settings only decide how the wallet coinjoins later, so they are open while the wallet still processes blocks.
		CoordinatorSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (UiContext.MainViewModel is { } mainViewModel)
			{
				await mainViewModel.SettingsPage.ActivateCoordinatorTabAsync();
			}
		});
	}

	public CoinJoinProtocol Protocol { get; }

	/// <summary>What the box calls its kind of coinjoin.</summary>
	public string Name { get; }

	/// <summary>Whether a coordinator is set. Without one there is no coinjoin of any kind, and the box says so.</summary>
	public bool IsCoinJoinEnabled { get; }

	/// <summary>
	/// Whether the box is the one of the WabiSabi coinjoins. It carries the logo and the menu: the settings of the
	/// coordinator are the same for every kind, and payments are added to WabiSabi rounds only.
	/// </summary>
	public bool IsWabiSabi => Protocol == CoinJoinProtocol.WabiSabi;

	/// <summary>Opens the settings of the coinjoins: the coordinator and the limits that the wallet keeps to.</summary>
	public ICommand CoordinatorSettingsCommand { get; }

	public static string GetName(CoinJoinProtocol protocol) =>
		protocol switch
		{
			CoinJoinProtocol.WabiSabi => "WabiSabi",
			CoinJoinProtocol.ZeroLink => "ZeroLink",
			_ => protocol.ToString()
		};
}
