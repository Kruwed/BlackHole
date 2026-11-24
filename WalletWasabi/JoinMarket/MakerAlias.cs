using NBitcoin.DataEncoders;

namespace WalletWasabi.JoinMarket;

/// <summary>
/// The name that the order book shows for an offer: two words that are told by the key that signed the offer.
/// </summary>
/// <remarks>
/// An offer is signed with a key of its own, which is random, so its name is random too. Everybody works the same name
/// out of the same key, and nobody can pick the name of another offer. Two offers can come to the same name, as there
/// are fewer names than keys: where that happens in a book, the book tells them apart, see <see cref="Disambiguate"/>.
/// </remarks>
public static class MakerAlias
{
	private static readonly string[] Adjectives =
	[
		"amber", "brisk", "cobalt", "dusky", "eager", "fervent", "gilded", "hazel", "idle", "jade", "lithe", "mossy",
		"nimble", "ochre", "pale", "quiet", "rustic", "silent", "tawny", "umber", "velvet", "wary", "zesty", "frosty"
	];

	private static readonly string[] Nouns =
	[
		"heron", "lynx", "otter", "falcon", "marten", "ibis", "wren", "bison", "gecko", "plover", "stoat", "tern",
		"osprey", "sable", "quail", "raven", "newt", "finch", "elk", "puffin", "hare", "kite", "civet", "auk"
	];

	/// <summary>How many names there are.</summary>
	public static int Count => Adjectives.Length * Nouns.Length;

	/// <summary>The name of the offer that the given key signed.</summary>
	/// <param name="publicKey">The public key in hex, as an event of Nostr names it.</param>
	public static string Get(string publicKey)
	{
		byte[] key;

		try
		{
			key = Encoders.Hex.DecodeData(publicKey);
		}
		catch (FormatException)
		{
			key = [];
		}

		if (key.Length < 2)
		{
			return "maker";
		}

		var number = ((key[0] << 8) | key[1]) % Count;

		return $"{Adjectives[number / Nouns.Length]}-{Nouns[number % Nouns.Length]}";
	}

	/// <summary>
	/// The names that a book shows for the given keys. A name that only one of the keys has is shown as it is. Keys of
	/// the same name get the first characters of the key added, as many as it takes to tell them apart.
	/// </summary>
	public static IReadOnlyDictionary<string, string> Disambiguate(IEnumerable<string> publicKeys)
	{
		var names = new Dictionary<string, string>();

		foreach (var sameName in publicKeys.Distinct().GroupBy(Get))
		{
			var keys = sameName.ToArray();

			if (keys.Length == 1)
			{
				names[keys[0]] = sameName.Key;
				continue;
			}

			var length = 4;

			while (length < keys.Max(key => key.Length) && keys.Select(key => Shorten(key, length)).Distinct().Count() < keys.Length)
			{
				length += 2;
			}

			foreach (var key in keys)
			{
				names[key] = $"{sameName.Key}-{Shorten(key, length)}";
			}
		}

		return names;
	}

	private static string Shorten(string key, int length) => key.Length <= length ? key : key[..length];
}
