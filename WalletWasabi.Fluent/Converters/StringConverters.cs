using Avalonia.Data.Converters;

namespace WalletWasabi.Fluent.Converters;

public static class StringConverters
{
	public static readonly IValueConverter AsString =
		new FuncValueConverter<object?, string?>(x => x?.ToString());
}
