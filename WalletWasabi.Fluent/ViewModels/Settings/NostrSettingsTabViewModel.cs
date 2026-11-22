using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Validation;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// The Nostr tab of the settings: the relays that the application connects to. The offer of the wallet as a market
/// maker is posted to them. The user names them, one on a line.
/// </summary>
/// <remarks>
/// A list that can be read is saved as it is typed, like the other settings, and it is used from the next start on. A
/// list with something in it that is not the address of a relay is not saved: the relays stay the ones that were read
/// last.
/// </remarks>
[AppLifetime]
public partial class NostrSettingsTabViewModel : ViewModelBase
{
	/// <summary>What stands between two relays: a new line, and also what a list that was copied from elsewhere may have.</summary>
	private static readonly char[] Separators = ['\n', '\r', ' ', '\t', ',', ';'];

	[AutoNotify] private string _relays;

	public NostrSettingsTabViewModel(UiContext uiContext, ApplicationSettings settings) : base(uiContext)
	{
		Settings = settings;

		_relays = FormatRelays(settings.NostrRelays);

		this.ValidateProperty(x => x.Relays, ValidateRelays);

		// The relays can change elsewhere: the welcome connects to the relays that the user names there. The box shows
		// them then, unless it says the same already, in the user's own way of writing it.
		this.WhenAnyValue(x => x.Settings.NostrRelays)
			.Skip(1)
			.Where(relays => !IsShown(relays))
			.Subscribe(relays => Relays = FormatRelays(relays));
	}

	public bool IsReadOnly => Settings.IsOverridden;

	public ApplicationSettings Settings { get; }

	/// <summary>Writes the relays the way the box shows them: one on a line.</summary>
	public static string FormatRelays(IEnumerable<string> relays) => string.Join(Environment.NewLine, relays);

	/// <summary>
	/// Reads the relays that the user named: WebSocket addresses, like <c>wss://relay.example</c>, one on a line. A relay
	/// that is named twice counts once. No relay at all is a list too: then nothing can be posted.
	/// </summary>
	/// <exception cref="FormatException">One of them is not the address of a relay.</exception>
	public static string[] ParseRelays(string text)
	{
		var addresses = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var relays = NostrRelayPublisher.ParseRelays(addresses);

		return [.. addresses.Zip(relays).DistinctBy(x => x.Second).Select(x => x.First)];
	}

	/// <summary>Whether the box names exactly these relays.</summary>
	private bool IsShown(string[] relays)
	{
		try
		{
			return ParseRelays(Relays).SequenceEqual(relays);
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private void ValidateRelays(IValidationErrors errors)
	{
		string[] relays;

		try
		{
			relays = ParseRelays(Relays);
		}
		catch (FormatException ex)
		{
			errors.Add(ErrorSeverity.Error, ex.Message);
			return;
		}

		if (!relays.SequenceEqual(Settings.NostrRelays))
		{
			Settings.NostrRelays = relays;
		}
	}
}
