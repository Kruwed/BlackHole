using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace WalletWasabi.Fluent.Controls;

/// <summary>
/// A row of controls that ends at the right edge of the panel. When the panel is too narrow for the whole row, the controls at
/// the start of the row give way, one after the other: they are taken out of sight, so that the ones at the end stay whole
/// and in their place. The last control always stays.
/// </summary>
public class RightAlignedRow : Panel
{
	/// <summary>Sizes are sums of fractions of a pixel: a row is not too long by what the arithmetic is off.</summary>
	private const double Tolerance = 0.01;

	public static readonly StyledProperty<double> SpacingProperty =
		AvaloniaProperty.Register<RightAlignedRow, double>(nameof(Spacing));

	static RightAlignedRow()
	{
		AffectsMeasure<RightAlignedRow>(SpacingProperty);

		// A control that gives way is placed to the left of the panel, where nothing of it is drawn.
		ClipToBoundsProperty.OverrideDefaultValue<RightAlignedRow>(true);
	}

	/// <summary>The gap between two controls of the row.</summary>
	public double Spacing
	{
		get => GetValue(SpacingProperty);
		set => SetValue(SpacingProperty, value);
	}

	/// <summary>Tells how many controls at the start of a row have to give way for the rest of the row to fit.</summary>
	/// <param name="widths">The widths of the controls of the row, from left to right.</param>
	public static int CountHidden(IReadOnlyList<double> widths, double spacing, double availableWidth)
	{
		var rowWidth = GetRowWidth(widths, spacing);
		var hidden = 0;

		while (hidden < widths.Count - 1 && rowWidth > availableWidth + Tolerance)
		{
			rowWidth -= widths[hidden] + spacing;
			hidden++;
		}

		return hidden;
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		// Every control is as wide as it wants to be: the row does not squeeze, it leaves out.
		var constraint = availableSize.WithWidth(double.PositiveInfinity);
		var height = 0.0;

		foreach (var child in Children)
		{
			child.Measure(constraint);
			height = Math.Max(height, child.DesiredSize.Height);
		}

		var widths = GetRow().Select(x => x.DesiredSize.Width).ToList();
		var shown = widths.Skip(CountHidden(widths, Spacing, availableSize.Width)).ToList();

		return new Size(GetRowWidth(shown, Spacing), height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var row = GetRow();
		var hidden = CountHidden(row.Select(x => x.DesiredSize.Width).ToList(), Spacing, finalSize.Width);
		var right = finalSize.Width;

		for (var i = row.Count - 1; i >= 0; i--)
		{
			var child = row[i];
			var width = child.DesiredSize.Width;

			if (i < hidden)
			{
				child.Arrange(new Rect(-width - 1, 0, width, finalSize.Height));
				continue;
			}

			right -= width;
			child.Arrange(new Rect(right, 0, width, finalSize.Height));
			right -= Spacing;
		}

		return finalSize;
	}

	private static double GetRowWidth(IReadOnlyList<double> widths, double spacing) =>
		widths.Sum() + (spacing * Math.Max(0, widths.Count - 1));

	/// <summary>The controls that take part in the row. One that is not visible takes no room, and no gap either.</summary>
	private List<Control> GetRow() => Children.Where(x => x.IsVisible).ToList();
}
