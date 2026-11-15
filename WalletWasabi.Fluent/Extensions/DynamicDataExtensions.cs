using System.Reactive.Linq;

namespace WalletWasabi.Fluent.Extensions;

public static class DynamicDataExtensions
{
	public static IObservable<bool> NotEmpty<TObject, TKey>(this IObservableCache<TObject, TKey> changeSet) where TKey : notnull where TObject : notnull
	{
		return changeSet.CountChanged.Select(n => n > 0);
	}

	public static IObservable<bool> Empty<TObject, TKey>(this IObservableCache<TObject, TKey> changeSet) where TKey : notnull where TObject : notnull
	{
		return changeSet.CountChanged.Select(n => n == 0);
	}
}
