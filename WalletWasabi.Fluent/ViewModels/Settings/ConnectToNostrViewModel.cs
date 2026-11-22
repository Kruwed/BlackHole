using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// Where the user names the Nostr relays that the application connects to, one on a line. The offer of the wallet as
/// a market maker is posted to them. It comes third on the welcome.
/// </summary>
/// <remarks>A relay is asked by opening a connection to it and closing it again: nothing is sent to it.</remarks>
[NavigationMetaData(Title = "Connect to Nostr")]
public partial class ConnectToNostrViewModel : ConnectionPageViewModel
{
	/// <summary>What stands between two relays: a new line, and also what a list that was copied from elsewhere may have.</summary>
	private static readonly char[] Separators = ['\n', '\r', ' ', '\t', ',', ';'];

	[AutoNotify] private string _relays;

	public ConnectToNostrViewModel(UiContext uiContext) : base(uiContext, Connection.Nostr)
	{
		IsReadOnly = Settings.IsNostrRelaysOverridden;

		// Relays that were given at the start are not in the saved settings. They are shown, because they are the ones in use.
		_relays = FormatRelays(IsReadOnly ? uiContext.Services.Config.NostrRelays : Settings.NostrRelays);

		this.ValidateProperty(x => x.Relays, ValidateRelays);

		// What the relays answered before says nothing about another list.
		this.WhenAnyValue(x => x.Relays)
			.Skip(1)
			.Subscribe(_ => ForgetAnswer());

		SetUpCommands(canAsk: this.WhenAnyValue(x => x.Relays, relays => TryReadRelays(relays, out _, out _)));
	}

	/// <summary>
	/// Relays that were given on the command line or in the environment win over the saved ones, so saving others would have no effect.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>Writes the relays the way the box shows them: one on a line.</summary>
	public static string FormatRelays(IEnumerable<string> relays) => string.Join(Environment.NewLine, relays);

	/// <summary>
	/// Reads the relays that the user named: WebSocket addresses, like <c>wss://relay.example</c>, one on a line. A relay
	/// that is named twice counts once.
	/// </summary>
	/// <exception cref="FormatException">One of them is not the address of a relay.</exception>
	public static string[] ParseRelays(string text)
	{
		var addresses = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var relays = NostrRelayPublisher.ParseRelays(addresses);

		return [.. addresses.Zip(relays).DistinctBy(x => x.Second).Select(x => x.First)];
	}

	/// <summary>
	/// Reads the relays that the user named, one on a line. There has to be one at least: with none there is nothing to
	/// connect to.
	/// </summary>
	/// <param name="problem">What is wrong with the list, if it cannot be used.</param>
	public static bool TryReadRelays(string text, out string[] relays, out string? problem)
	{
		try
		{
			relays = ParseRelays(text);
		}
		catch (FormatException ex)
		{
			relays = [];
			problem = ex.Message;
			return false;
		}

		problem = relays.Length == 0 ? "Name one relay at least, like wss://relay.example." : null;
		return problem is null;
	}

	/// <summary>
	/// Tells which of the relays answer. Nostr counts as answering if one of them does: that is all it takes to
	/// connect to them.
	/// </summary>
	public static ConnectionAnswer DescribeAnswers(IReadOnlyList<RelayReachability> relays)
	{
		var reached = relays.Count(relay => relay.IsReachable);

		var summary = (reached, relays.Count) switch
		{
			(0, 1) => "The relay cannot be reached.",
			(0, _) => $"None of the {relays.Count} relays can be reached.",
			(1, 1) => "The relay answers.",
			_ when reached == relays.Count => $"All {relays.Count} relays answer.",
			_ => $"{reached} of {relays.Count} relays answer."
		};

		return new ConnectionAnswer(reached > 0, WithProblems(summary, relays));
	}

	/// <summary>
	/// Tells how connecting to the relays went. The application is connected if one of them answered: it then posts to
	/// all of them, and a relay that did not answer this time gets every offer all the same.
	/// </summary>
	public static ConnectionAnswer DescribeConnection(IReadOnlyList<RelayReachability> relays)
	{
		var reached = relays.Count(relay => relay.IsReachable);

		var summary = (reached, relays.Count) switch
		{
			(0, 1) => "The relay cannot be reached.",
			(0, _) => $"None of the {relays.Count} relays can be reached.",
			(1, 1) => $"Connected. {Constants.AppName} posts to this relay from now on.",
			_ when reached == relays.Count => $"Connected to all {relays.Count} relays. {Constants.AppName} posts to them from now on.",
			_ => $"Connected to {reached} of {relays.Count} relays. {Constants.AppName} posts to all of them from now on."
		};

		return new ConnectionAnswer(reached > 0, WithProblems(summary, relays));
	}

	/// <summary>Adds a line for every relay that did not answer, with the reason.</summary>
	private static string WithProblems(string summary, IReadOnlyList<RelayReachability> relays) =>
		string.Join(
			Environment.NewLine,
			relays.Where(relay => !relay.IsReachable).Select(relay => $"{relay.Relay}: {relay.Problem}").Prepend(summary));

	private void ValidateRelays(IValidationErrors errors)
	{
		if (!TryReadRelays(Relays, out _, out var problem) && problem is not null)
		{
			errors.Add(ErrorSeverity.Error, problem);
		}
	}

	protected override string GetWaitingMessage(bool isConnecting) =>
		isConnecting ? "Connecting to the relays…" : "Asking the relays…";

	protected override async Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken)
	{
		if (!TryReadRelays(Relays, out var relays, out var problem))
		{
			return new ConnectionAnswer(false, problem ?? "");
		}

		var answers = await UiContext.Services.VerifyNostrRelaysAsync(NostrRelayPublisher.ParseRelays(relays), cancellationToken);

		return DescribeAnswers(answers);
	}

	protected override async Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken)
	{
		if (!TryReadRelays(Relays, out var relays, out var problem))
		{
			return new ConnectionAnswer(false, problem ?? "");
		}

		var answers = await UiContext.Services.ConnectToNostrRelaysAsync(NostrRelayPublisher.ParseRelays(relays), cancellationToken);
		var answer = DescribeConnection(answers);

		if (answer.IsGood && !IsReadOnly)
		{
			Settings.SetConnectedNostrRelays(relays);
		}

		return answer;
	}
}
