using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Fluent.ViewModels.AddWallet.Create;
using WalletWasabi.Wallets.Slip39;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The confirmation of the recovery words when a wallet is created: the user puts every word of the backup into its
/// place before the wallet exists. No word is confirmed for them, and nothing lets them go on without a word.
/// </summary>
public class RecoveryWordsConfirmationTests
{
	// Ten words of the SLIP-39 word list, as a page of a share has them: in no order of the alphabet.
	private static readonly string[] PageWords = ["leader", "acid", "window", "decision", "paper", "burden", "skin", "music", "harvest", "taxi"];

	[Fact]
	public void NoWordIsInItsPlaceBeforeTheUserPutItThere()
	{
		var confirmation = new RecoveryWordsConfirmation(CreateWords(PageWords));

		// Every place of the page is empty, and the first one has its turn.
		Assert.Equal(Enumerable.Range(1, 10), confirmation.Words.Select(x => x.Index));
		Assert.All(confirmation.Words, word => Assert.Null(word.SelectedWord));
		Assert.All(confirmation.Words, word => Assert.False(word.IsConfirmed));
		Assert.False(confirmation.AllWordsConfirmed);
		Assert.Same(confirmation.Words[0], confirmation.CurrentWord);

		// The user picks among all the words of the page, which are in the order of the alphabet.
		Assert.Equal(PageWords.Order(), confirmation.Choices.Select(x => x.Word));
		Assert.All(confirmation.Choices, choice => Assert.True(choice.IsEnabled));
		Assert.All(confirmation.Choices, choice => Assert.False(choice.IsConfirmed));
		Assert.All(confirmation.Choices, choice => Assert.False(choice.IsSelected));
	}

	[Fact]
	public void PageIsConfirmedWhenEveryWordIsInItsPlace()
	{
		var confirmation = new RecoveryWordsConfirmation(CreateWords(PageWords));

		for (var i = 0; i < PageWords.Length; i++)
		{
			// Nine words in their places are not enough: the page is not confirmed before the last one.
			Assert.False(confirmation.AllWordsConfirmed);
			Assert.Equal(i + 1, confirmation.CurrentWord.Index);

			var choice = Pick(confirmation, PageWords[i]);

			Assert.True(confirmation.Words[i].IsConfirmed);
			Assert.Equal(PageWords[i], confirmation.Words[i].SelectedWord);

			// A word that is in its place cannot be picked again.
			Assert.True(choice.IsConfirmed);
		}

		Assert.True(confirmation.AllWordsConfirmed);
		Assert.All(confirmation.Choices, choice => Assert.True(choice.IsConfirmed));
	}

	[Fact]
	public void WrongWordDoesNotConfirmAPlace()
	{
		var confirmation = new RecoveryWordsConfirmation(CreateWords(PageWords));
		Pick(confirmation, "leader");

		// The second place takes "acid". "window" is the word of the third.
		var wrong = Pick(confirmation, "window");

		Assert.Equal(2, confirmation.CurrentWord.Index);
		Assert.Equal("window", confirmation.CurrentWord.SelectedWord);
		Assert.False(confirmation.CurrentWord.IsConfirmed);
		Assert.False(wrong.IsConfirmed);

		// Nothing else can be picked until the wrong word is taken back.
		Assert.True(wrong.IsEnabled);
		Assert.All(confirmation.Choices.Where(x => x != wrong), choice => Assert.False(choice.IsEnabled));

		var other = confirmation.Choices.Single(x => x.Word == "acid");
		other.IsSelected = true;
		confirmation.OnChoiceChanged(other);
		Assert.Equal("window", confirmation.CurrentWord.SelectedWord);
		other.IsSelected = false;
		confirmation.OnChoiceChanged(other);
		Assert.Equal("window", confirmation.CurrentWord.SelectedWord);

		wrong.IsSelected = false;
		confirmation.OnChoiceChanged(wrong);

		Assert.Equal(2, confirmation.CurrentWord.Index);
		Assert.Null(confirmation.CurrentWord.SelectedWord);
		Assert.All(confirmation.Choices.Where(x => !x.IsConfirmed), choice => Assert.True(choice.IsEnabled));

		// The page can still be confirmed, with every word.
		foreach (var word in PageWords.Skip(1))
		{
			Assert.False(confirmation.AllWordsConfirmed);
			Pick(confirmation, word);
		}

		Assert.True(confirmation.AllWordsConfirmed);
	}

	[Fact]
	public void WordThatIsOnThePageTwiceIsPickedTwice()
	{
		string[] words = ["acid", "music", "acid", "taxi"];
		var confirmation = new RecoveryWordsConfirmation(CreateWords(words));

		Assert.Equal(2, confirmation.Choices.Count(x => x.Word == "acid"));

		Pick(confirmation, "acid");
		Pick(confirmation, "music");

		// One of the two is used up, the other one is left for the third place.
		Assert.Single(confirmation.Choices, x => x.Word == "acid" && !x.IsConfirmed);
		Assert.False(confirmation.Words[2].IsConfirmed);

		Pick(confirmation, "acid");
		Assert.False(confirmation.AllWordsConfirmed);

		Pick(confirmation, "taxi");
		Assert.True(confirmation.AllWordsConfirmed);
	}

	[Fact]
	public void ConfirmationStartsOverWhenThePageIsComeToAgain()
	{
		var words = CreateWords(PageWords);
		var confirmation = new RecoveryWordsConfirmation(words);

		foreach (var word in PageWords)
		{
			Pick(confirmation, word);
		}

		Assert.True(confirmation.AllWordsConfirmed);

		// The page is come to again, for example on the way back from the next one.
		var again = new RecoveryWordsConfirmation(words);

		Assert.False(again.AllWordsConfirmed);
		Assert.All(again.Words, word => Assert.False(word.IsConfirmed));
		Assert.All(again.Words, word => Assert.Null(word.SelectedWord));
		Assert.Equal(1, again.CurrentWord.Index);
	}

	/// <summary>
	/// The pages of the confirmation go through every word of a share: 20 words on two pages, and the 33 words of a
	/// share of a longer secret on three.
	/// </summary>
	[Theory]
	[InlineData(20, 2)]
	[InlineData(33, 3)]
	public void EveryWordOfAShareIsOnOnePage(int wordCount, int pageCount)
	{
		var words = CreateWords(Enumerable.Range(1, wordCount).Select(i => $"word{i}").ToArray());

		Assert.Equal(pageCount, ConfirmMultiShareViewModel.GetPageCount(wordCount));

		var wordsOfPages = Enumerable.Range(1, pageCount)
			.Select(page => ConfirmMultiShareViewModel.GetWordsOfPage(words, page))
			.ToArray();

		Assert.Equal(Enumerable.Range(1, wordCount), wordsOfPages.SelectMany(x => x).Select(x => x.Index));

		// Every word of a page has to be confirmed.
		foreach (var wordsOfPage in wordsOfPages)
		{
			var confirmation = new RecoveryWordsConfirmation(wordsOfPage);

			Assert.Equal(wordsOfPage.Count, confirmation.Words.Count);
			Assert.All(confirmation.Words, word => Assert.False(word.IsConfirmed));
		}
	}

	/// <summary>
	/// The pages of the confirmation of a new backup, a share of 20 words unless the user splits it, have no way around
	/// them: there is nothing to skip a page with.
	/// </summary>
	[Fact]
	public void ConfirmationOfANewBackupCannotBeSkipped()
	{
		var shares = WalletGenerator.GenerateShares();
		var backup = new WalletCreationOptions.AddNewWallet().WithNewBackup().Backup! with { Shares = shares, CurrentShare = 1 };

		var wordsOfShare = CreateWords(shares[0].ToMnemonic(WordList.Wordlist).Split(' '));
		Assert.Equal(20, wordsOfShare.Count);

		for (byte page = 1; page <= 2; page++)
		{
			var options = new WalletCreationOptions.AddNewWallet(backup with { CurrentSharePage = page });
			var viewModel = new ConfirmMultiShareViewModel(null!, options, new Dictionary<int, List<RecoveryWordViewModel>> { [0] = wordsOfShare });

			Assert.Null(viewModel.SkipCommand);
			Assert.Equal(2, viewModel.TotalCurrenSharePages);
			Assert.False(viewModel.AllWordsConfirmed);
		}
	}

	/// <summary>Picks the word for the place that has its turn, the way a click on the word does.</summary>
	private static RecoveryWordViewModel Pick(RecoveryWordsConfirmation confirmation, string word)
	{
		var choice = confirmation.Choices.First(x => x.Word == word && !x.IsConfirmed);

		choice.IsSelected = true;
		confirmation.OnChoiceChanged(choice);

		return choice;
	}

	/// <summary>The words of a page or of a share, numbered from 1. They need nothing of the application to be confirmed.</summary>
	private static List<RecoveryWordViewModel> CreateWords(string[] words) =>
		words.Select((word, i) => new RecoveryWordViewModel(null!, i + 1, word)).ToList();
}
