using System.Globalization;
using Avalonia.Data.Converters;

namespace WalletWasabi.Fluent.Converters;

public static class DoubleConverters
{
	/// <summary>Takes the number in the parameter off the value, but not below zero. A layout uses it to leave room for something else.</summary>
	public static readonly IValueConverter Minus =
		new FuncValueConverter<double, string?, double>((value, parameter) => Subtract(value, parameter));

	public static double Subtract(double value, string? parameter) =>
		double.TryParse(parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out var subtrahend) && !double.IsNaN(value)
			? Math.Max(0, value - subtrahend)
			: value;
}
