using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Helpers;
using WalletWasabi.Userfacing;
using static WalletWasabi.Userfacing.CurrencyInput;

namespace WalletWasabi.Fluent.Controls;

public partial class CurrencyEntryBox : TextBox
{
	public static readonly StyledProperty<string> CurrencyCodeProperty =
		AvaloniaProperty.Register<CurrencyEntryBox, string>(nameof(CurrencyCode));

	public static readonly StyledProperty<int> MaxDecimalsProperty =
		AvaloniaProperty.Register<CurrencyEntryBox, int>(nameof(MaxDecimals), 8);

	public static readonly StyledProperty<Money> BalanceBtcProperty =
		AvaloniaProperty.Register<CurrencyEntryBox, Money>(nameof(BalanceBtc));

	public static readonly StyledProperty<bool> ValidatePasteBalanceProperty =
		AvaloniaProperty.Register<CurrencyEntryBox, bool>(nameof(ValidatePasteBalance));

	private static readonly string[] InvalidCharacters = new string[1] { "\u007f" };

	public CurrencyEntryBox()
	{
		SetCurrentValue(TextProperty, string.Empty);
	}

	public string CurrencyCode
	{
		get => GetValue(CurrencyCodeProperty);
		set => SetValue(CurrencyCodeProperty, value);
	}

	public int MaxDecimals
	{
		get => GetValue(MaxDecimalsProperty);
		set => SetValue(MaxDecimalsProperty, value);
	}

	public Money BalanceBtc
	{
		get => GetValue(BalanceBtcProperty);
		set => SetValue(BalanceBtcProperty, value);
	}

	public bool ValidatePasteBalance
	{
		get => GetValue(ValidatePasteBalanceProperty);
		set => SetValue(ValidatePasteBalanceProperty, value);
	}

	protected override void OnGotFocus(GotFocusEventArgs e)
	{
		base.OnGotFocus(e);

		SetCurrentValue(CaretIndexProperty, Text?.Length ?? 0);

		Dispatcher.UIThread.Post(SelectAll);
	}

	protected override void OnTextInput(TextInputEventArgs e)
	{
		var input = e.Text == null ? "" : e.Text.TotalTrim();
		var currentText = Text ?? "";

		// Reject space char input when there's no text.
		if (string.IsNullOrWhiteSpace(currentText) && string.IsNullOrWhiteSpace(input))
		{
			e.Handled = true;
			base.OnTextInput(e);
			return;
		}

		if (IsReplacingWithImplicitDecimal(input))
		{
			var result = ReplaceCurrentTextWithLeadingZero(e);
			base.OnTextInput(result);
			return;
		}

		if (IsInsertingImplicitDecimal(input))
		{
			var result = InsertLeadingZeroForDecimal(currentText, e);
			base.OnTextInput(result);
			return;
		}

		var preComposedText = PreComposeText(input);

		var isValid = ValidateEntryText(preComposedText);

		preComposedText = preComposedText.TotalTrim();

		var parsed = decimal.TryParse(preComposedText, NumberStyles.Number, InvariantNumberFormat, out _);

		e.Handled = !(isValid && parsed);

		base.OnTextInput(e);
	}

	private bool IsReplacingWithImplicitDecimal(string input)
	{
		return input.StartsWith('.') && SelectedText == Text;
	}

	private bool IsInsertingImplicitDecimal(string input)
	{
		return input.StartsWith('.') && CaretIndex == 0 && Text is not null && !Text.Contains('.');
	}

	private TextInputEventArgs ReplaceCurrentTextWithLeadingZero(TextInputEventArgs e)
	{
		var finalText = "0" + e.Text;
		SetCurrentValue(TextProperty, "");
		SetCurrentValue(CaretIndexProperty, finalText.Length);
		ClearSelection();
		return new TextInputEventArgs { Text = finalText };
	}

	private TextInputEventArgs InsertLeadingZeroForDecimal(string currentText, TextInputEventArgs e)
	{
		var prependText = "0" + e.Text;
		SetCurrentValue(TextProperty, currentText.Insert(0, prependText));
		SetCurrentValue(CaretIndexProperty, CaretIndex + prependText.Length);
		return new TextInputEventArgs { Text = "" };
	}

	[GeneratedRegex($"^(?<Whole>[0-9{GroupSeparator}]*)(\\{DecimalSeparator}?(?<Frac>[0-9{GroupSeparator}]*))$")]
	private static partial Regex RegexBtcFormat();

	[GeneratedRegex($"{GroupSeparator}{{2,}}")]
	private static partial Regex RegexConsecutiveSpaces();

	[GeneratedRegex($"[{GroupSeparator}{DecimalSeparator}]+")]
	private static partial Regex RegexGroupAndDecimal();

	private bool ValidateEntryText(string preComposedText)
	{
		// Check if it has a decimal separator.
		var trailingDecimal = preComposedText.Length > 0 && preComposedText.EndsWith(DecimalSeparator);
		var match = RegexBtcFormat().Match(preComposedText);

		// Ignore group chars on count of the whole part of the decimal.
		var wholeStr = match.Groups["Whole"].ToString();
		var whole = RegexGroupAndDecimal().Replace(wholeStr, "").Length;

		var fracStr = match.Groups["Frac"].ToString().Replace(GroupSeparator, "");
		var frac = RegexGroupAndDecimal().Replace(fracStr, "").Length;

		// Check for consecutive spaces (2 or more) and leading spaces.
		var rule1 = preComposedText.Length > 1 && (preComposedText.StartsWith(GroupSeparator) ||
												   RegexConsecutiveSpaces().IsMatch(preComposedText));

		// Check for trailing spaces in the whole number part and in the last part of the precomp string.
		var rule2 = whole >= 8 && (preComposedText.EndsWith(GroupSeparator) || wholeStr.EndsWith(GroupSeparator));

		// Check for non-numeric chars.
		var rule3 = !CurrencyInput.RegexDecimalCharsOnly().IsMatch(preComposedText);
		if (rule1 || rule2 || rule3)
		{
			return false;
		}

		// Reject and don't process the input if the string doesn't match.
		if (!match.Success)
		{
			return false;
		}

		// Passthrough the decimal place char or the group separator.
		if (preComposedText == DecimalSeparator && !trailingDecimal)
		{
			return false;
		}

		// Bitcoin input restriction is to only allow 8 decimal places max
		// and also 8 whole number places.
		if ((whole > 8 && !trailingDecimal) || frac > MaxDecimals)
		{
			return false;
		}

		return true;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		DoPasteCheck(e);
	}

	private void DoPasteCheck(KeyEventArgs e)
	{
		var keymap = Application.Current?.PlatformSettings?.HotkeyConfiguration;

		bool Match(IEnumerable<KeyGesture> gestures) => gestures.Any(g => g.Matches(e));

		if (keymap is { } && Match(keymap.Paste))
		{
			ModifiedPasteAsync();
			e.Handled = true;
		}
		else
		{
			base.OnKeyDown(e);
		}
	}

	public async void ModifiedPasteAsync()
	{
		var text = await ApplicationHelper.GetTextAsync();

		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		// Ignore paste if there are invalid characters
		if (!CurrencyInput.IsValidDecimal(text, out _))
		{
			return;
		}

		text = text.Replace("\r", "").Replace("\n", "").Trim();

		if (!TryParsePastedValue(text, out text))
		{
			return;
		}

		if (ValidateEntryText(text))
		{
			OnTextInput(new TextInputEventArgs { Text = text });

			Dispatcher.UIThread.Post(() =>
			{
				ClearSelection();
				CaretIndex = Text?.Length ?? 0;
			});
		}
	}

	private bool TryParsePastedValue(string text, [NotNullWhen(true)] out string? result)
	{
		var money = ValidatePasteBalance
			? ClipboardObserver.ParseToBtc(text, BalanceBtc)
			: ClipboardObserver.ParseToBtc(text);
		if (money is not null)
		{
			result = money.ToDecimal(MoneyUnit.BTC).FormattedBtcExactFractional(text);
			return true;
		}

		result = null;
		return false;
	}

	// Pre-composes the TextInputEventArgs to see the potential Text that is to
	// be committed to the TextPresenter in this control.

	private string RemoveInvalidCharacters(string text)
	{
		for (var i = 0; i < InvalidCharacters.Length; i++)
		{
			text = text.Replace(InvalidCharacters[i], string.Empty);
		}

		return text;
	}

	// An event in Avalonia's TextBox with this function should be implemented there for brevity.
	private string PreComposeText(string input)
	{
		input = RemoveInvalidCharacters(input);
		var preComposedText = Text ?? "";
		var caretIndex = CaretIndex;
		var selectionStart = SelectionStart;
		var selectionEnd = SelectionEnd;

		if (!string.IsNullOrEmpty(input) && (MaxLength == 0 ||
											 input.Length + preComposedText.Length -
											 Math.Abs(selectionStart - selectionEnd) <= MaxLength))
		{
			if (selectionStart != selectionEnd)
			{
				var start = Math.Min(selectionStart, selectionEnd);
				var end = Math.Max(selectionStart, selectionEnd);
				preComposedText = $"{preComposedText[..start]}{preComposedText[end..]}";
				caretIndex = start;
			}

			return $"{preComposedText[..caretIndex]}{input}{preComposedText[caretIndex..]}";
		}

		return "";
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == IsReadOnlyProperty)
		{
			PseudoClasses.Set(":readonly", change.GetNewValue<bool>());
		}
	}
}
