using WalletWasabi.JoinMarket.OrderBook;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The strategy triangle is drawn as large as its column lets it be. A click means the same strategy at every size.
/// </summary>
public class StrategyTriangleTests
{
	/// <summary>At the size of the design, 340 by 206, the corners are where the design has them.</summary>
	[Theory]
	[InlineData(170, 44, 1, 0, 0)]
	[InlineData(90, 182, 0, 1, 0)]
	[InlineData(250, 182, 0, 0, 1)]
	public void CornersOfTheTriangleAreThePureStrategies(double x, double y, double top, double left, double right)
	{
		Assert.Equal(new TriangleWeights(top, left, right), StrategyTriangle.GetWeights(x, y, 340, 206));

		// Twice as large, the corners are twice as far out.
		Assert.Equal(new TriangleWeights(top, left, right), StrategyTriangle.GetWeights(2 * x, 2 * y, 680, 412));

		// And smaller, in a column that is narrower than the design.
		Assert.Equal(new TriangleWeights(top, left, right), StrategyTriangle.GetWeights(x * 300 / 340, y * 300 / 340, 300, 206d * 300 / 340));
	}

	[Fact]
	public void MiddleOfASideIsHalfOfEachOfItsCorners()
	{
		// Between the two lower corners.
		Assert.Equal(new TriangleWeights(0, 0.5, 0.5), StrategyTriangle.GetWeights(170, 182, 340, 206));

		// Between the top and the lower left corner.
		Assert.Equal(new TriangleWeights(0.5, 0.5, 0), StrategyTriangle.GetWeights(130, 113, 340, 206));
	}

	/// <summary>A click beside the triangle is taken as a click on its edge: the weights are never negative, and they are a whole.</summary>
	[Theory]
	[InlineData(0, 0)]
	[InlineData(340, 0)]
	[InlineData(0, 206)]
	[InlineData(340, 206)]
	[InlineData(170, 0)]
	[InlineData(170, 206)]
	[InlineData(-500, 90)]
	public void ClickOutsideIsBroughtOntoTheTriangle(double x, double y)
	{
		var weights = StrategyTriangle.GetWeights(x, y, 340, 206);

		Assert.InRange(weights.Top, 0, 1);
		Assert.InRange(weights.Left, 0, 1);
		Assert.InRange(weights.Right, 0, 1);
		Assert.Equal(1, weights.Top + weights.Left + weights.Right, 2);
	}

	/// <summary>The marker of a strategy is drawn where a click would have put it.</summary>
	[Theory]
	[InlineData(1, 0, 0)]
	[InlineData(0.2, 0.5, 0.3)]
	[InlineData(0, 0.25, 0.75)]
	public void MarkerIsWhereItsStrategyIs(double top, double left, double right)
	{
		var weights = new TriangleWeights(top, left, right);
		var (x, y) = StrategyTriangle.GetPosition(weights);

		Assert.Equal(weights, StrategyTriangle.GetWeights(x - StrategyTriangle.FieldLeft, y, 340, 206));
	}
}
