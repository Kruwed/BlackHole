using Avalonia;
using Avalonia.Data.Converters;
using System.Globalization;
using Avalonia.Media;

namespace WalletWasabi.Fluent.Converters;

/// <summary>Finds the geometry of an icon by the name of its resource.</summary>
public class IconNameConverter : IValueConverter
{
	public static readonly IconNameConverter Instance = new();

	private IconNameConverter()
	{
	}

	object IValueConverter.Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (Application.Current is { } && value is string iconName)
		{
			if (Application.Current.Styles.TryGetResource(iconName, null, out object? resource))
			{
				return resource is not StreamGeometry ? AvaloniaProperty.UnsetValue : resource;
			}
		}

		return AvaloniaProperty.UnsetValue;
	}

	object IValueConverter.ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
