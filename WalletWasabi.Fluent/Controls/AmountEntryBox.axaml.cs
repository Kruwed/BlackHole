using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Userfacing;

namespace WalletWasabi.Fluent.Controls;

/// <summary>Entry box for a bitcoin amount, exposed as a decimal BTC value.</summary>
public class AmountEntryBox : TemplatedControl
{
	public static readonly StyledProperty<HorizontalAlignment> HorizontalContentAlignmentProperty =
		AvaloniaProperty.Register<AmountEntryBox, HorizontalAlignment>(nameof(HorizontalContentAlignment));

	public static readonly StyledProperty<VerticalAlignment> VerticalContentAlignmentProperty =
		AvaloniaProperty.Register<AmountEntryBox, VerticalAlignment>(nameof(VerticalContentAlignment));

	public static readonly DirectProperty<AmountEntryBox, decimal?> AmountBtcProperty =
		AvaloniaProperty.RegisterDirect<AmountEntryBox, decimal?>(
			nameof(AmountBtc),
			o => o.AmountBtc,
			(o, v) => o.AmountBtc = v,
			enableDataValidation: true,
			defaultBindingMode: BindingMode.TwoWay);

	public static readonly StyledProperty<string?> TextProperty =
		AvaloniaProperty.Register<AmountEntryBox, string?>(nameof(Text));

	public static readonly StyledProperty<string> WatermarkProperty =
		AvaloniaProperty.Register<AmountEntryBox, string>(nameof(Watermark));

	public static readonly StyledProperty<string> CurrencyCodeProperty =
		AvaloniaProperty.Register<AmountEntryBox, string>(nameof(CurrencyCode));

	public static readonly StyledProperty<bool> IsReadOnlyProperty =
		AvaloniaProperty.Register<AmountEntryBox, bool>(nameof(IsReadOnly));

	public static readonly StyledProperty<CurrencyEntryBox?> EntryBoxProperty =
		AvaloniaProperty.Register<AmountEntryBox, CurrencyEntryBox?>(nameof(EntryBox));

	public static readonly StyledProperty<Money> BalanceBtcProperty =
		AvaloniaProperty.Register<AmountEntryBox, Money>(nameof(BalanceBtc));

	public static readonly StyledProperty<bool> ValidatePasteBalanceProperty =
		AvaloniaProperty.Register<AmountEntryBox, bool>(nameof(ValidatePasteBalance));

	private CompositeDisposable? _disposable;
	private bool _isTextInputFocused;
	private bool _skipProcessing;
	private bool _skipTextProcessing;

	public AmountEntryBox()
	{
		this.GetObservable(TextProperty).Where(_ => !_skipTextProcessing).Subscribe(InputText);
		this.GetObservable(IsReadOnlyProperty).Subscribe(_ => UpdateDisplay());
		this.GetObservable(AmountBtcProperty).Where(_ => !_skipProcessing).Subscribe(_ => UpdateDisplay(true));

		UpdateDisplay();

		FocusCommand = ReactiveCommand.Create(() => { EntryBox?.Focus(); });
	}

	public HorizontalAlignment HorizontalContentAlignment
	{
		get => GetValue(HorizontalContentAlignmentProperty);
		set => SetValue(HorizontalContentAlignmentProperty, value);
	}

	public VerticalAlignment VerticalContentAlignment
	{
		get => GetValue(VerticalContentAlignmentProperty);
		set => SetValue(VerticalContentAlignmentProperty, value);
	}

	public decimal? AmountBtc
	{
		get;
		set => SetAndRaise(AmountBtcProperty, ref field, value);
	}

	public string? Text
	{
		get => GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	public string Watermark
	{
		get => GetValue(WatermarkProperty);
		set => SetValue(WatermarkProperty, value);
	}

	public string CurrencyCode
	{
		get => GetValue(CurrencyCodeProperty);
		set => SetValue(CurrencyCodeProperty, value);
	}

	public bool IsReadOnly
	{
		get => GetValue(IsReadOnlyProperty);
		set => SetValue(IsReadOnlyProperty, value);
	}

	public CurrencyEntryBox? EntryBox
	{
		get => GetValue(EntryBoxProperty);
		set => SetValue(EntryBoxProperty, value);
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

	public ICommand FocusCommand { get; }

	/// <param name="text">Text is a valid decimal value with correct decimal separator. However, it can contain spaces.</param>
	private void InputText(string? text)
	{
		if (!_isTextInputFocused)
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(text))
		{
			SetBtcAmount(null);
		}
		else
		{
			if (decimal.TryParse(RemoveFormat(text), NumberStyles.Number, CurrencyInput.InvariantNumberFormat, out var decimalValue))
			{
				SetBtcAmount(decimalValue);
			}
		}

		UpdateDisplay();
	}

	private void UpdateDisplay(bool insertValue = false)
	{
		_skipTextProcessing = true;

		Watermark = $"{0m.FormattedBtc()} BTC";

		var text = EntryBox?.Text ?? "";
		if (_isTextInputFocused)
		{
			text = insertValue ? AmountBtc?.ToString(CultureInfo.InvariantCulture) : RemoveFormat(text);
		}
		else
		{
			text = AmountBtc > 0 ? AmountBtc?.FormattedBtcFixedFractional() : string.Empty;
		}

		SetCurrentValue(TextProperty, text);

		_skipTextProcessing = false;
	}

	private void SetBtcAmount(decimal? amount)
	{
		_skipProcessing = true;
		SetCurrentValue(AmountBtcProperty, amount);
		_skipProcessing = false;
	}

	protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
	{
		base.OnApplyTemplate(e);

		_disposable?.Dispose();
		_disposable = new CompositeDisposable();

		EntryBox = e.NameScope.Find<CurrencyEntryBox>("PART_EntryBox");

		EntryBox?
			.GetObservable(IsKeyboardFocusWithinProperty)
			.Subscribe(x =>
			{
				if (EntryBox.ContextFlyout is null || EntryBox.ContextFlyout.IsOpen)
				{
					return;
				}

				_isTextInputFocused = x;
				UpdateDisplay();
			})
			.DisposeWith(_disposable);
	}

	private static string RemoveFormat(string text) => text.Replace(" ", "");

	protected override void UpdateDataValidation(AvaloniaProperty property, BindingValueType state, Exception? error)
	{
		if (property == AmountBtcProperty)
		{
			DataValidationErrors.SetError(this, error);
		}
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
