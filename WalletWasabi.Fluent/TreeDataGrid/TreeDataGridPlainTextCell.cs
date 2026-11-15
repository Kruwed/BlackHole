using System.Globalization;
using Avalonia;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Selection;
using Avalonia.Media;

namespace WalletWasabi.Fluent.TreeDataGrid;

/// <summary>A cell that draws the text of a <see cref="PlainTextCell"/>, centred vertically.</summary>
public class TreeDataGridPlainTextCell : TreeDataGridCell
{
	private string? _text;
	private FormattedText? _formattedText;
	private Size? _availableSize;
	private bool _haveText;

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property == ForegroundProperty)
		{
			InvalidateVisual();
		}
	}

	public override void Realize(
		TreeDataGridElementFactory factory,
		ITreeDataGridSelectionInteraction? selection,
		ICell model,
		int columnIndex,
		int rowIndex)
	{
		var text = ((PlainTextCell)model).Value;

		if (_text != text)
		{
			_text = text;
			_haveText = !string.IsNullOrWhiteSpace(_text);
			_formattedText = null;

			if (_availableSize is not null)
			{
				_formattedText = CreateFormattedText(_availableSize.Value, _text);
			}
		}

		base.Realize(factory, selection, model, columnIndex, rowIndex);
	}

	public override void Unrealize()
	{
		_formattedText = null;
		_text = null;
		_haveText = false;
		base.Unrealize();
	}

	public override void Render(DrawingContext context)
	{
		context.FillRectangle(Brushes.Transparent, new Rect(new Point(), DesiredSize));

		if (!_haveText || _formattedText is not { } formattedText)
		{
			return;
		}

		var r = Bounds.CenterRect(new Rect(new Point(0, 0), new Size(formattedText.Width, formattedText.Height)));
		if (Foreground is { })
		{
			formattedText.SetForegroundBrush(Foreground);
		}

		context.DrawText(formattedText, new Point(0, r.Position.Y));
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		_availableSize = availableSize;

		_formattedText = _haveText
			? CreateFormattedText(availableSize, _text)
			: null;

		return _formattedText is null
			? default
			: new Size(_formattedText.Width, _formattedText.Height);
	}

	private FormattedText CreateFormattedText(Size availableSize, string? text)
	{
		return new FormattedText(
			text ?? string.Empty,
			CultureInfo.CurrentCulture,
			FlowDirection.LeftToRight,
			new Typeface(FontFamily, FontStyle, FontWeight),
			FontSize,
			null)
		{
			TextAlignment = TextAlignment.Left,
			MaxTextHeight = availableSize.Height,
			MaxTextWidth = availableSize.Width,
			Trimming = TextTrimming.None
		};
	}
}
