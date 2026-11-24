using System.Linq;
using WalletWasabi.JoinMarket;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// A maker is called by two words that its key gives it, so that everybody who reads the book calls it the same.
/// </summary>
public class MakerAliasTests
{
	[Theory]
	[InlineData("0000", "amber-heron")]
	[InlineData("0001", "amber-lynx")]
	[InlineData("0017", "amber-auk")]
	[InlineData("0018", "brisk-heron")]
	[InlineData("023f", "frosty-auk")] // 575, the last of the 576 names
	[InlineData("0240", "amber-heron")] // 576: the names start over
	[InlineData("ffff", "tawny-raven")] // 65,535 leaves 447 over 576, which is 18 × 24 + 15
	public void NameIsTakenFromTheFirstTwoBytesOfTheKey(string start, string expected)
	{
		Assert.Equal(expected, MakerAlias.Get(start + new string('a', 60)));
	}

	[Fact]
	public void KeyHasTheSameNameEveryTime()
	{
		var key = "5d" + new string('3', 62);

		Assert.Equal(MakerAlias.Get(key), MakerAlias.Get(key));
		Assert.Equal(MakerAlias.Get(key), MakerAlias.Get(key.ToUpperInvariant()));
		Assert.Equal(576, MakerAlias.Count);
	}

	[Theory]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("00")]
	[InlineData("not a key at all")]
	public void WhatIsNoKeyIsJustAMaker(string text)
	{
		Assert.Equal("maker", MakerAlias.Get(text));
	}

	/// <summary>There are more keys than names. Two makers of one name are told apart by the start of their keys.</summary>
	[Fact]
	public void MakersOfOneNameAreToldApartByTheirKeys()
	{
		var alone = "0001" + new string('a', 60);
		var first = "0000" + "11" + new string('a', 58);
		var second = "0000" + "12" + new string('a', 58);
		var third = "0240" + new string('a', 60);

		var names = MakerAlias.Disambiguate([alone, first, second, third, first]);

		Assert.Equal(4, names.Count);
		Assert.Equal("amber-lynx", names[alone]);
		Assert.Equal("amber-heron-000011", names[first]);
		Assert.Equal("amber-heron-000012", names[second]);
		Assert.Equal("amber-heron-0240aa", names[third]);
		Assert.Equal(4, names.Values.Distinct().Count());
	}

	[Fact]
	public void FourCharactersOfAKeyAreEnoughWhereTheyDiffer()
	{
		var first = "0000" + new string('a', 60);
		var second = "0240" + new string('a', 60);

		var names = MakerAlias.Disambiguate([first, second]);

		Assert.Equal("amber-heron-0000", names[first]);
		Assert.Equal("amber-heron-0240", names[second]);
	}
}
