using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Fluent.Configuration;
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

	uint GetTipHeight();
	uint GetServerTipHeight();
	int GetHashesLeft();
	SmartHeader? GetTip();
	uint GetBlockHeadersTipHeight();
	int GetPeerCount();

	uint? GetMinimumBlockHeight();

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
