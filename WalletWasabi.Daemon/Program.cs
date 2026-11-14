using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using WalletWasabi.Client;
using WalletWasabi.Logging;

namespace WalletWasabi.Daemon;

public class Program
{
	public static async Task<int> Main(string[] args)
	{
		var app = WasabiAppBuilder
			.Create("Black Hole Daemon", args)
			.EnsureSingleInstance()
			.OnUnhandledExceptions(LogUnhandledException)
			.OnUnobservedTaskExceptions(LogUnobservedTaskException)
			.Build();

		var exitCode = await app.RunAsConsoleAsync().ConfigureAwait(false);
		return (int)exitCode;
	}

	private static void LogUnobservedTaskException(object? sender, AggregateException e)
	{
		ReadOnlyCollection<Exception> innerExceptions = e.Flatten().InnerExceptions;

		switch (innerExceptions)
		{
			case [SocketException { SocketErrorCode: SocketError.OperationAborted }]:
			// Source of this exception is NBitcoin library.
			case [OperationCanceledException { Message: "The peer has been disconnected" }]:
				// Until https://github.com/MetacoSA/NBitcoin/pull/1089 is resolved.
				Logger.LogTrace(e);
				break;
			default:
				Logger.LogDebug(e);
				break;
		}
	}

	private static void LogUnhandledException(object? sender, Exception e) =>
		Logger.LogWarning(e);
}

public static class WasabiAppExtensions
{
	public static async Task<ExitCode> RunAsConsoleAsync(this WasabiApplication app)
	{
		void ProcessCommands()
		{
			// The wallet is opened at start only when it is asked for. Otherwise the 'loadwallet' RPC opens it.
			if (!app.AppConfig.Arguments.Contains("--loadwallet", StringComparer.InvariantCultureIgnoreCase))
			{
				return;
			}

			if (app.Global.WalletManager.Wallet is { } wallet)
			{
				app.Global.WalletManager.StartWalletAsync(wallet).ConfigureAwait(false);
			}
			else
			{
				Logger.LogWarning("There is no wallet to load. Ignoring...");
			}
		}

		return await app.RunAsync(
			async () =>
			{
				try
				{
					await app.Global.InitializeAsync(initializeSleepInhibitor: false, app.TerminateService, app.TerminateService.CancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (app.TerminateService.CancellationToken.IsCancellationRequested)
				{
					Logger.LogInfo("User requested the application to stop. Stopping.");
				}

				if (!app.TerminateService.CancellationToken.IsCancellationRequested)
				{
					ProcessCommands();
					await app.TerminateService.ForcefulTerminationRequestedTask.ConfigureAwait(false);
				}
			}).ConfigureAwait(false);
	}
}
