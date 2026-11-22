using System.Diagnostics.CodeAnalysis;
using System.Linq;
using WalletWasabi.Helpers;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Finds out whether the application can coinjoin with a coordinator: the coordinator must answer when it is asked for
/// its rounds.
/// </summary>
public static class CoordinatorConnectionCheck
{
	/// <summary>How much is told of what went wrong on the way to a coordinator. What answers there can say anything.</summary>
	private const int MaxReasonLength = 200;

	/// <summary>
	/// Reads the address of a coordinator: an absolute HTTP or HTTPS address, for example <c>http://localhost:37126/</c>.
	/// </summary>
	public static bool TryParseUri(string? text, [NotNullWhen(true)] out Uri? uri)
	{
		// "localhost:37126" is an absolute address too, with the scheme "localhost". Nothing can be asked there.
		if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
		{
			uri = parsed;
			return true;
		}

		uri = null;
		return false;
	}

	/// <summary>Tells whether the coordinator is an onion service, which can only be reached through Tor.</summary>
	public static bool IsOnion(Uri uri) =>
		uri.DnsSafeHost.EndsWith(".onion", StringComparison.OrdinalIgnoreCase);

	/// <param name="timeout">How long the coordinator is given to answer.</param>
	/// <returns>Nothing if the coordinator answered, otherwise the reason why it cannot be used, in words for the user.</returns>
	public static async Task<Result<string>> CheckAsync(IWabiSabiApiRequestHandler coordinator, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout);

		try
		{
			// Every coordinator tells its rounds to whoever asks. Nothing about the wallet is in the question.
			var response = await coordinator.GetStatusAsync(RoundStateRequest.Empty, timeoutCts.Token).WaitAsync(timeoutCts.Token).ConfigureAwait(false);

			// An answer that is not the one of a coordinator cannot be read.
			return response?.RoundStates is not null
				? Result<string>.Ok()
				: Result<string>.Fail("What answers at this address is not a coordinator.");
		}
		catch (Exception ex)
		{
			// Whoever asked for the check has given up on it: there is nothing to tell.
			cancellationToken.ThrowIfCancellationRequested();

			return Result<string>.Fail(DescribeFailure(ex));
		}
	}

	/// <summary>Tells in words for the user why the coordinator did not answer.</summary>
	public static string DescribeFailure(Exception ex) =>
		ex switch
		{
			OperationCanceledException or TimeoutException => "The coordinator did not answer in time.",
			AggregateException { InnerExceptions: [var single, ..] } => DescribeFailure(single),

			// What went wrong the last time, when every attempt failed.
			{ InnerException: { } inner } => $"The coordinator cannot be reached ({Tidy(inner.Message)}).",
			_ => $"The coordinator cannot be reached ({Tidy(ex.Message)})."
		};

	/// <summary>Makes one line of a sensible length of a reason. A web server that is not a coordinator may answer with a whole page.</summary>
	private static string Tidy(string text)
	{
		var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
		var line = new string([.. firstLine.Select(character => char.IsControl(character) ? ' ' : character)]).Trim().TrimEnd('.');

		return line.Length <= MaxReasonLength ? line : $"{line[..MaxReasonLength]}…";
	}
}
