using System.Collections.Generic;
using System.Linq;

namespace WalletWasabi.Fluent.ViewModels.AddWallet.Create;

/// <summary>
/// The confirmation of the recovery words of one page. The user puts every word of the page into its place, one place
/// after the other, by picking it among the words of the page. No word is put into its place for the user and no place
/// can be left out: the page is confirmed when every word is in its place.
/// </summary>
public class RecoveryWordsConfirmation
{
	/// <param name="words">The words of the page. What was confirmed of them before is forgotten: the confirmation starts over.</param>
	public RecoveryWordsConfirmation(IEnumerable<RecoveryWordViewModel> words)
	{
		Words = words.OrderBy(x => x.Index).ToList();

		if (Words.Count == 0)
		{
			throw new ArgumentException("There is no word to confirm.", nameof(words));
		}

		foreach (var word in Words)
		{
			word.Reset();
		}

		// The words to pick from are the words of the page in the order of the alphabet, which tells nothing about their places.
		Choices = Words
			.Select(x => new RecoveryWordViewModel(x.UiContext, x.Index, x.Word))
			.OrderBy(x => x.Word)
			.ToList();

		CurrentWord = Words[0];
		EnableChoices(true);
	}

	/// <summary>
	/// The places of the page in their order. A place shows what the user picked for it, and it is confirmed when that
	/// is its word.
	/// </summary>
	public IReadOnlyList<RecoveryWordViewModel> Words { get; }

	/// <summary>The words that the user picks from, one for every place. A word that is in its place cannot be picked again.</summary>
	public IReadOnlyList<RecoveryWordViewModel> Choices { get; }

	/// <summary>The place that the next pick is for: the first one that is not confirmed, and the last one when all are.</summary>
	public RecoveryWordViewModel CurrentWord { get; private set; }

	/// <summary>Whether every word of the page is in its place.</summary>
	public bool AllWordsConfirmed => Words.All(x => x.IsConfirmed);

	/// <summary>
	/// Takes what the user did with one of the words to pick from: they picked it for the current place, or they took
	/// a wrong pick back.
	/// </summary>
	public void OnChoiceChanged(RecoveryWordViewModel choice)
	{
		// A word that is in its place, and any other word while a wrong one is picked, is not to be had.
		if (choice.IsConfirmed || !choice.IsEnabled)
		{
			return;
		}

		if (choice.IsSelected)
		{
			// The place is confirmed by this if the word is its word.
			CurrentWord.SelectedWord = choice.Word;
		}
		else
		{
			CurrentWord.SelectedWord = null;
		}

		if (CurrentWord.IsConfirmed)
		{
			// The word is in its place and cannot be picked again. The next place has its turn.
			choice.IsConfirmed = true;
			CurrentWord = Words.FirstOrDefault(x => !x.IsConfirmed) ?? Words[^1];
			EnableChoices(true);
		}
		else if (choice.IsSelected)
		{
			// A wrong word. Nothing else can be picked until it is taken back.
			EnableChoices(false);
			choice.IsEnabled = true;
		}
		else
		{
			EnableChoices(true);
		}
	}

	private void EnableChoices(bool enable)
	{
		foreach (var choice in Choices)
		{
			choice.IsEnabled = enable;
		}
	}
}
