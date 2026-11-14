using System.Collections.Generic;
using System.Windows.Input;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;

namespace WalletWasabi.Fluent.ViewModels.HelpAndSupport;

[NavigationMetaData(
	Title = "About Black Hole",
	Caption = "Display Black Hole's current info",
	IconName = "info_regular",
	Order = 4,
	Category = "Help & Support",
	Keywords = new[]
	{
			"About", "Software", "Version", "Source", "Code", "Github", "Bug", "Report", "License", "Information",
			"Fork", "Wasabi", "Wallet"
	},
	NavBarPosition = NavBarPosition.None,
	NavigationTarget = NavigationTarget.DialogScreen)]
public partial class AboutViewModel : RoutableViewModel
{
	public AboutViewModel(UiContext uiContext, bool navigateBack = false) : base(uiContext)
	{
		EnableBack = navigateBack;

		Links = new List<ViewModelBase>()
			{
				new LinkViewModel(UiContext)
				{
					Link = SourceCodeLink,
					Description = "Source Code (GitHub)",
					IsClickable = true
				},
				new SeparatorViewModel(UiContext),
				new LinkViewModel(UiContext)
				{
					Link = BugReportLink,
					Description = "Bug Report",
					IsClickable = true
				},
				new SeparatorViewModel(UiContext),
				new LinkViewModel(UiContext)
				{
					Link = WasabiWalletLink,
					Description = "Wasabi Wallet (GitHub)",
					IsClickable = true
				},
			};

		License = new LinkViewModel(UiContext)
		{
			Link = LicenseLink,
			Description = "MIT License",
			IsClickable = true
		};

		OpenBrowserCommand = ReactiveCommand.CreateFromTask<string>(x => UiContext.OpenBrowserAsync(x));

		ReleaseHighlightsDialogCommand = ReactiveCommand.CreateFromTask(async () => await Navigate().To().ReleaseHighlightsDialog().GetResultAsync());

		CopyLinkCommand = ReactiveCommand.CreateFromTask<string>(async (link) => await UiContext.Clipboard.SetTextAsync(link));

		NextCommand = CancelCommand;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);
	}

	public List<ViewModelBase> Links { get; }

	public LinkViewModel License { get; }

	public ICommand ReleaseHighlightsDialogCommand { get; }

	public ICommand OpenBrowserCommand { get; }

	public ICommand CopyLinkCommand { get; }

	public Version ClientVersion => Constants.ClientVersion;

	public static string SourceCodeLink => "https://github.com/Kruwed/BlackHole/";

	public static string BugReportLink => "https://github.com/Kruwed/BlackHole/issues/new?template=bug-report.md";

	/// <summary>The project Black Hole is a fork of.</summary>
	public static string WasabiWalletLink => "https://github.com/WalletWasabi/WalletWasabi/";

	public static string LicenseLink => "https://github.com/Kruwed/BlackHole/blob/master/LICENSE.md";
}
