using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Tests.UnitTests.ViewModels.TestDoubles;

public class TestAddress : ReactiveObject, IAddress
{
	public TestAddress(string address)
	{
		Text = address;
		ShortenedText = address;
	}

	public string Text { get; }
	public string ShortenedText { get; }

	public bool IsUsed
	{
		get;
		set => this.RaiseAndSetIfChanged(ref field, value);
	}

	public void Hide()
	{
		IsUsed = true;
	}
}
