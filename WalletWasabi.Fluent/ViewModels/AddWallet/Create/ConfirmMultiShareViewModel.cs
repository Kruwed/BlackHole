using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using WalletWasabi.Fluent.ViewModels.Dialogs;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.AddWallet.Create;

/// <summary>
/// One page of the confirmation of a backup that the user has just written down. The pages go through every share and
/// through every word of it, and the wallet is created after the last one: the user has put every word of the backup
/// into its place by then. No word is confirmed for the user, and nothing leads past a page.
/// </summary>
[NavigationMetaData(Title = "Confirm Recovery Words")]
public partial class ConfirmMultiShareViewModel : RoutableViewModel
{
	private static readonly Dictionary<int, byte> WordsPerPageMap = new()
	{
		[12] = 12,
		[18] = 9,
		[20] = 10,
		[24] = 12,
		[33] = 11,
	};

	private static readonly Dictionary<int, byte> TotalPagesMap = new()
	{
		[12] = 1,
		[18] = 2,
		[20] = 2,
		[24] = 2,
		[33] = 3,
	};

	private readonly WalletCreationOptions.AddNewWallet _options;
	private readonly Dictionary<int, List<RecoveryWordViewModel>> _wordsDictionary;
	private readonly List<RecoveryWordViewModel> _words;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private byte _currentShare;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private byte _totalShares;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private byte _currentSharePage;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private byte _totalCurrenSharePages;

	[AutoNotify] private RecoveryWordViewModel _currentWord;
	[AutoNotify] private List<RecoveryWordViewModel> _availableWords;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _allWordsConfirmed;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _caption = "";

	public ConfirmMultiShareViewModel(UiContext uiContext, WalletCreationOptions.AddNewWallet options, Dictionary<int, List<RecoveryWordViewModel>> wordsDictionary) : base(uiContext)
	{
		var multiShareBackup = options.Backup;

		ArgumentNullException.ThrowIfNull(multiShareBackup);

		_currentShare = multiShareBackup.CurrentShare;
		_totalShares = multiShareBackup.Settings.Shares;

		var words = wordsDictionary[_currentShare - 1];

		_wordsDictionary = wordsDictionary;

		_options = options;
		_availableWords = new List<RecoveryWordViewModel>();

		// The words of the share are spread over pages. Every word of this page has to be confirmed.
		_currentSharePage = multiShareBackup.CurrentSharePage;
		_totalCurrenSharePages = (byte)GetPageCount(words.Count);

		_words = GetWordsOfPage(words, _currentSharePage).ToList();
		_currentWord = _words[0];
	}

	public ObservableCollectionExtended<RecoveryWordViewModel> ConfirmationWords { get; } = new();

	/// <summary>The number of pages that the confirmation of a share of so many words takes.</summary>
	public static int GetPageCount(int wordCount) => TotalPagesMap[wordCount];

	/// <summary>
	/// The words of a share that one page of its confirmation is about, in their order. The pages are counted from 1,
	/// and every word of the share is on one of them.
	/// </summary>
	public static IReadOnlyList<RecoveryWordViewModel> GetWordsOfPage(IReadOnlyList<RecoveryWordViewModel> wordsOfShare, int page)
	{
		var wordsPerPage = WordsPerPageMap[wordsOfShare.Count];

		return wordsOfShare
			.OrderBy(x => x.Index)
			.Skip(wordsPerPage * (page - 1))
			.Take(wordsPerPage)
			.ToList();
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// The page starts over every time that it is come to, also on the way back from a later page: no word stays
		// confirmed, and none is confirmed for the user.
		var confirmation = new RecoveryWordsConfirmation(_words);

		ConfirmationWords.Clear();
		ConfirmationWords.AddRange(confirmation.Words);
		AvailableWords = confirmation.Choices.ToList();
		CurrentWord = confirmation.CurrentWord;
		AllWordsConfirmed = confirmation.AllWordsConfirmed;

		EnableBack = true;

		CancelCommand = ReactiveCommand.Create(OnCancel);

		// The next page, and after the last page the wallet, is only to be had with every word of this page in its place.
		NextCommand = ReactiveCommand.CreateFromTask(
			async () => await OnNextAsync(),
			this.WhenAnyValue(x => x.AllWordsConfirmed).ObserveOn(RxApp.MainThreadScheduler));

		confirmation.Choices
			.Select(choice => choice.WhenAnyValue(x => x.IsSelected).Skip(1).Select(_ => choice))
			.Merge()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(choice =>
			{
				confirmation.OnChoiceChanged(choice);
				CurrentWord = confirmation.CurrentWord;
				AllWordsConfirmed = confirmation.AllWordsConfirmed;
			})
			.DisposeWith(disposables);

		this.WhenAnyValue(
				x => x.CurrentShare,
				x => x.TotalShares,
				x => x.CurrentSharePage,
				x => x.TotalCurrenSharePages,
				x => x.CurrentWord,
				x => x.CurrentWord.SelectedWord,
				x => x.AllWordsConfirmed)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ =>
			{
				var currentWordMessage = AllWordsConfirmed
					? "Recovery words confirmed."
					: CurrentWord.SelectedWord is not null && !CurrentWord.IsConfirmed
						? $"Incorrect word #{CurrentWord.Index} selected"
						: $"Click the recovery word #{CurrentWord.Index}";

				// A backup that is not split has nothing to say about shares.
				var share = TotalShares == 1 ? "" : $"Share #{CurrentShare} of {TotalShares} - ";

				Caption = $"{share}Page #{CurrentSharePage} of {TotalCurrenSharePages}. {currentWordMessage}";
			})
			.DisposeWith(disposables);

		var enableCancel = UiContext.WalletRepository.HasWallet;
		SetupCancel(enableCancel: false, enableCancelOnEscape: enableCancel, enableCancelOnPressed: false);
	}

	private async Task OnNextAsync()
	{
		var options = _options;

		if (options.Backup is not { } multiShareBackup)
		{
			throw new ArgumentOutOfRangeException(nameof(options));
		}

		// After the last page of the last share the whole backup is confirmed, and the wallet can be created.
		if (_currentShare >= multiShareBackup.Settings.Shares && _currentSharePage >= _totalCurrenSharePages)
		{
			var password = await Navigate().To()
				.CreatePasswordDialog("Add Password", CreatePasswordDialogViewModel.NewWalletCaption, enableEmpty: true).GetResultAsync();

			if (password is null)
			{
				return;
			}

			options = options with
			{
				Backup = multiShareBackup with
				{
					Password = password
				}
			};

			var walletSettings = await UiContext.WalletRepository.NewWalletAsync(options);
			Navigate().To().AddedWalletPage(walletSettings, options);
		}
		else
		{
			var isLastSharePage = _currentSharePage == _totalCurrenSharePages;
			var nextShare = (byte)(isLastSharePage ? _currentShare + 1 : _currentShare);
			var nextSharePage = (byte)(isLastSharePage ? 1 : _currentSharePage + 1);

			options = options with
			{
				Backup = multiShareBackup with
				{
					CurrentShare = nextShare,
					CurrentSharePage = nextSharePage
				}
			};

			Navigate().To().ConfirmMultiShare(options, _wordsDictionary);
		}
	}

	private void OnCancel()
	{
		Navigate().Clear();
	}
}
