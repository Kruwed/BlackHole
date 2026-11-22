using System.Linq;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The settings have the tabs Tor, Node, Nostr, Coordinator and Wallet. On the Nostr tab the user names the relays that
/// the application connects to, one on a line. These are the rules of that list.
/// </summary>
public class SettingsTabsTests
{
	[Fact]
	public void RelaysAreNamedOneOnALine()
	{
		Assert.Equal(
			["wss://one.example", "ws://127.0.0.1:7777/"],
			NostrSettingsTabViewModel.ParseRelays("wss://one.example\nws://127.0.0.1:7777/"));

		// The line ends of another system, empty lines and blanks around an address do no harm.
		Assert.Equal(
			["wss://one.example", "wss://two.example"],
			NostrSettingsTabViewModel.ParseRelays("\r\n  wss://one.example  \r\n\r\nwss://two.example\r\n"));
	}

	/// <summary>A list that was copied from elsewhere may have its relays side by side, as the start option has them.</summary>
	[Theory]
	[InlineData("wss://one.example;wss://two.example")]
	[InlineData("wss://one.example, wss://two.example")]
	[InlineData("wss://one.example wss://two.example")]
	[InlineData("wss://one.example\twss://two.example")]
	public void RelaysMayAlsoStandSideBySide(string text)
	{
		Assert.Equal(["wss://one.example", "wss://two.example"], NostrSettingsTabViewModel.ParseRelays(text));
	}

	[Fact]
	public void RelayThatIsNamedTwiceCountsOnce()
	{
		Assert.Equal(
			["wss://one.example", "wss://two.example"],
			NostrSettingsTabViewModel.ParseRelays("wss://one.example\nwss://two.example\nwss://one.example"));

		// The same relay, written differently.
		Assert.Equal(["wss://One.Example"], NostrSettingsTabViewModel.ParseRelays("wss://One.Example\nwss://one.example/"));
	}

	/// <summary>No relay at all is a list too: the application then connects to none, and no offer can be posted.</summary>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("\n\n")]
	public void ListMayBeEmpty(string text)
	{
		Assert.Empty(NostrSettingsTabViewModel.ParseRelays(text));
	}

	[Theory]
	[InlineData("https://relay.example", "https://relay.example")]
	[InlineData("relay.example", "relay.example")]
	[InlineData("wss://one.example\nnot a relay", "not")]
	[InlineData("wss://", "wss://")]
	public void WhatIsNoRelayIsNotTaken(string text, string culprit)
	{
		var exception = Assert.Throws<FormatException>(() => NostrSettingsTabViewModel.ParseRelays(text));

		// The message names what is wrong and what an address looks like.
		Assert.Contains($"'{culprit}'", exception.Message);
		Assert.Contains("wss://", exception.Message);
	}

	[Fact]
	public void RelaysAreShownTheWayTheyAreTyped()
	{
		var text = NostrSettingsTabViewModel.FormatRelays(Constants.DefaultNostrRelays);

		Assert.Equal(3, text.Split(Environment.NewLine).Length);
		Assert.Equal(Constants.DefaultNostrRelays, NostrSettingsTabViewModel.ParseRelays(text));

		Assert.Equal("", NostrSettingsTabViewModel.FormatRelays([]));
	}
}
