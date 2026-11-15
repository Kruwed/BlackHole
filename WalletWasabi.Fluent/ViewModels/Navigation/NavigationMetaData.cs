#pragma warning disable IDE0130 // Namespace does not match folder structure (see https://github.com/WalletWasabi/WalletWasabi/pull/10576#issuecomment-1552750543)

namespace WalletWasabi.Fluent;

public sealed class NavigationMetaData
{
	public required string Title { get; init; }

	public NavigationTarget NavigationTarget { get; init; }
}
