using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Helpers;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent;

public interface IServices
{
	string DataDir { get; }
	string PersistentConfigFilePath { get; }
	PersistentConfig PersistentConfig { get; }
	WalletManager WalletManager { get; }
	UiConfig UiConfig { get; }
	Config Config { get; }
	EventBus EventBus { get; }

	/// <summary>Height of the chain, as the user's node tells it. Zero as long as the node did not answer.</summary>
	uint GetNodeTipHeight();

	/// <summary>What the user's node told about itself the last time, if it answered.</summary>
	ConnectedRpcStatus? GetNodeStatus();

	/// <summary>
	/// Makes the application work with the node at the given RPC interface from now on, without a restart, provided that
	/// the node answers and is on the network of the application. Otherwise the application keeps the node that it has.
	/// </summary>
	/// <returns>Nothing if the application works with the node, otherwise the reason why it does not, in words for the user.</returns>
	Task<Result<string>> ConnectToBitcoinNodeAsync(string rpcUri, string rpcCredentialString, CancellationToken cancellationToken);

	/// <summary>Height of the last block that the wallet processed. The confirmations of its transactions are counted to there. Zero without a wallet.</summary>
	uint GetTipHeight();

	bool TryGetTransaction(uint256 hash, [NotNullWhen(true)] out SmartTransaction? tx);

	Network GetNetwork();
	bool HasWallet();
	string GetWalletsDir();
	Task StartWalletAsync(Wallet wallet);
	void AddWallet(KeyManager keyManager);

	string GetTorLogFilePath();
	TorMode GetUseTor();

	/// <summary>Gets the current exchange rate of BTC to the configured currency.</summary>

	bool GetHideOnClose();
	double? GetWindowWidth();
	double? GetWindowHeight();
	void SetWindowWidth(double? width);
	void SetWindowHeight(double? height);
	bool GetPrivacyMode();
	bool GetAutocopy();
	bool GetAutoPaste();
	int GetFeeTarget();
	void SetFeeTarget(int value);

	T? GetHostedService<T>() where T : class, Microsoft.Extensions.Hosting.IHostedService;

	Task SendTransactionAsync(SmartTransaction transaction);

	HttpClient CreateHttpClient(string name);

	bool IsForcefulTerminationRequested();
}
