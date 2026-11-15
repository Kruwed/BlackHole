#pragma warning disable IDE0130 // Namespace does not match folder structure (see https://github.com/WalletWasabi/WalletWasabi/pull/10576#issuecomment-1552750543)

namespace WalletWasabi.Fluent;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class NavigationMetaDataAttribute : Attribute
{
	public required string Title { get; set; }

	public NavigationTarget NavigationTarget { get; set; }
}
