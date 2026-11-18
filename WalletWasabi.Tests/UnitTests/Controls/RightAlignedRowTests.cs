using System.Linq;
using Avalonia;
using Avalonia.Controls;
using WalletWasabi.Fluent.Controls;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Controls;

/// <summary>
/// The row of the title bar ends at the right edge of the window. In a window that is too narrow for all of it, what is
/// at its start gives way, so that the buttons at its end stay where they are.
/// </summary>
public class RightAlignedRowTests
{
	private const double Height = 42;

	[Fact]
	public void RowEndsAtTheRightEdge()
	{
		var row = CreateRow(spacing: 6, 100, 50, 30);

		Layout(row, width: 400);

		Assert.Equal(new Rect(208, 0, 100, Height), row.Children[0].Bounds);
		Assert.Equal(new Rect(314, 0, 50, Height), row.Children[1].Bounds);
		Assert.Equal(new Rect(370, 0, 30, Height), row.Children[2].Bounds);
		Assert.Equal(192, row.DesiredSize.Width);
	}

	[Fact]
	public void RowThatJustFitsIsShownWhole()
	{
		var row = CreateRow(spacing: 6, 100, 50, 30);

		Layout(row, width: 192);

		Assert.Equal(new Rect(0, 0, 100, Height), row.Children[0].Bounds);
		Assert.Equal(new Rect(162, 0, 30, Height), row.Children[2].Bounds);
	}

	[Fact]
	public void StartOfTheRowGivesWayWhenThePanelIsTooNarrow()
	{
		var row = CreateRow(spacing: 6, 100, 50, 30);

		// One pixel is missing: the first control goes, and the others do not move away from the right edge.
		Layout(row, width: 191);

		Assert.True(IsOutOfSight(row.Children[0], row));
		Assert.Equal(new Rect(105, 0, 50, Height), row.Children[1].Bounds);
		Assert.Equal(new Rect(161, 0, 30, Height), row.Children[2].Bounds);

		// The panel asks for the room of what it shows only.
		Assert.Equal(86, row.DesiredSize.Width);

		// Narrower still, the second one goes too.
		Layout(row, width: 85);

		Assert.True(IsOutOfSight(row.Children[0], row));
		Assert.True(IsOutOfSight(row.Children[1], row));
		Assert.Equal(new Rect(55, 0, 30, Height), row.Children[2].Bounds);

		// With room again, all of them are back.
		Layout(row, width: 400);

		Assert.Equal(new Rect(208, 0, 100, Height), row.Children[0].Bounds);
		Assert.Equal(new Rect(314, 0, 50, Height), row.Children[1].Bounds);
		Assert.Equal(new Rect(370, 0, 30, Height), row.Children[2].Bounds);
	}

	[Fact]
	public void LastControlAlwaysStays()
	{
		var row = CreateRow(spacing: 6, 100, 50, 30);

		Layout(row, width: 10);

		Assert.True(IsOutOfSight(row.Children[0], row));
		Assert.True(IsOutOfSight(row.Children[1], row));
		Assert.Equal(30, row.Children[2].Bounds.Width);
		Assert.Equal(10, row.Children[2].Bounds.Right);
	}

	[Fact]
	public void ControlThatIsNotVisibleTakesNoRoomAndNoGap()
	{
		var row = CreateRow(spacing: 6, 100, 50, 30);
		row.Children[1].IsVisible = false;

		Layout(row, width: 400);

		Assert.Equal(new Rect(264, 0, 100, Height), row.Children[0].Bounds);
		Assert.Equal(new Rect(370, 0, 30, Height), row.Children[2].Bounds);
		Assert.Equal(136, row.DesiredSize.Width);

		// What it would need does not push the first one out either.
		Layout(row, width: 136);

		Assert.Equal(new Rect(0, 0, 100, Height), row.Children[0].Bounds);
	}

	[Fact]
	public void MarginsOfTheControlsCount()
	{
		var row = CreateRow(spacing: 6, 100, 50, 30);
		row.Children[0].Margin = new Thickness(0, 0, 12, 0);

		Layout(row, width: 400);

		Assert.Equal(new Rect(196, 0, 100, Height), row.Children[0].Bounds);
		Assert.Equal(204, row.DesiredSize.Width);

		Layout(row, width: 203);

		Assert.True(IsOutOfSight(row.Children[0], row));
	}

	[Theory]
	[InlineData(192, 0)]
	[InlineData(1000, 0)]
	[InlineData(191.9, 1)]
	[InlineData(86, 1)]
	[InlineData(85.9, 2)]
	[InlineData(30, 2)]
	[InlineData(0, 2)]
	public void CountsWhatHasToGiveWay(double availableWidth, int expected)
	{
		Assert.Equal(expected, RightAlignedRow.CountHidden([100, 50, 30], spacing: 6, availableWidth));
	}

	[Fact]
	public void EmptyRowAndRowOfOneHideNothing()
	{
		Assert.Equal(0, RightAlignedRow.CountHidden([], spacing: 6, availableWidth: 0));
		Assert.Equal(0, RightAlignedRow.CountHidden([100], spacing: 6, availableWidth: 0));

		var row = new RightAlignedRow { Spacing = 6 };
		Layout(row, width: 100);
		Assert.Equal(0, row.DesiredSize.Width);
	}

	private static RightAlignedRow CreateRow(double spacing, params double[] widths)
	{
		var row = new RightAlignedRow { Spacing = spacing };
		row.Children.AddRange(widths.Select(width => new Border { Width = width }));
		return row;
	}

	private static void Layout(RightAlignedRow row, double width)
	{
		row.Measure(new Size(width, Height));
		row.Arrange(new Rect(0, 0, width, Height));
	}

	/// <summary>Whether nothing of the control is inside the panel, which cuts off what is outside of it.</summary>
	private static bool IsOutOfSight(Control control, RightAlignedRow row) =>
		row.ClipToBounds && control.Bounds.Right <= 0;
}
