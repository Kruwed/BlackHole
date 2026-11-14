using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Wallets;

public abstract record CpfpInfoMessage
{
	public record UpdateMessage : CpfpInfoMessage;
	public record GetCachedCpfpInfo(IReplyChannel<CachedCpfpInfo[]> ReplyChannel) : CpfpInfoMessage;
	public record PreFetchInfoForTransaction(SmartTransaction SmartTransaction) : CpfpInfoMessage;
	public record GetInfoForTransaction(SmartTransaction SmartTransaction, IReplyChannel<Result<CpfpInfo, string>> ReplyChannel) : CpfpInfoMessage;
}

public record CachedCpfpInfo(CpfpInfo CpfpInfo, SmartTransaction Transaction);

public class CpfpInfoProvider(MailboxProcessor<CpfpInfoMessage> cpfpUpdater)
{
	public Task<CachedCpfpInfo[]> GetCachedCpfpInfoAsync(CancellationToken cancellationToken) =>
		cpfpUpdater.PostAndReplyAsync<CachedCpfpInfo[]>(chan => new CpfpInfoMessage.GetCachedCpfpInfo(chan), cancellationToken);

	public void ScheduleRequest(SmartTransaction tx) =>
		cpfpUpdater.Post(new CpfpInfoMessage.PreFetchInfoForTransaction(tx));

	public Task<Result<CpfpInfo,string>> GetCpfpInfoAsync(SmartTransaction tx, CancellationToken cancellationToken) =>
		cpfpUpdater.PostAndReplyAsync<Result<CpfpInfo,string>>(chan => new CpfpInfoMessage.GetInfoForTransaction(tx, chan), cancellationToken);
}

/// <summary>
/// Keeps what the mempool of the node tells about the unconfirmed transactions of the wallet: their fees and those of their unconfirmed ancestors.
/// </summary>
public static class CpfpInfoUpdater
{
	/// <summary>The cached information is not refreshed more often than this.</summary>
	private static readonly TimeSpan MinimumRefreshPeriod = TimeSpan.FromSeconds(30);

	public static MessageHandler<CpfpInfoMessage, Unit> Create(IRPCClient rpcClient, EventBus eventBus)
	{
		var cache = new Dictionary<uint256, CachedCpfpInfo>();
		var lastRefresh = DateTimeOffset.MinValue;

		return async (msg, _, cancellationToken) =>
		{
			switch (msg)
			{
				case CpfpInfoMessage.UpdateMessage:
					if (DateTimeOffset.UtcNow - lastRefresh >= MinimumRefreshPeriod)
					{
						lastRefresh = DateTimeOffset.UtcNow;
						await RefreshAsync(rpcClient, cache, eventBus, cancellationToken).ConfigureAwait(false);
					}
					break;

				case CpfpInfoMessage.GetCachedCpfpInfo m:
					m.ReplyChannel.Reply(cache.Values.ToArray());
					break;

				case CpfpInfoMessage.GetInfoForTransaction m:
					m.ReplyChannel.Reply(await FetchAsync(m.SmartTransaction, rpcClient, cache, eventBus, cancellationToken).ConfigureAwait(false));
					break;

				case CpfpInfoMessage.PreFetchInfoForTransaction m:
					if (m.SmartTransaction.CanBeSpeedUpUsingCpfp())
					{
						await FetchAsync(m.SmartTransaction, rpcClient, cache, eventBus, cancellationToken).ConfigureAwait(false);
					}
					break;
			}

			return Unit.Instance;
		};
	}

	/// <summary>
	/// Forgets the transactions that got confirmed and asks the node again about the others.
	/// </summary>
	private static async Task RefreshAsync(IRPCClient rpcClient, Dictionary<uint256, CachedCpfpInfo> cache, EventBus eventBus, CancellationToken cancellationToken)
	{
		foreach (var cached in cache.Values.ToArray())
		{
			if (cached.Transaction.Confirmed)
			{
				cache.Remove(cached.Transaction.GetHash());
			}
			else
			{
				await FetchAsync(cached.Transaction, rpcClient, cache, eventBus, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private static async Task<Result<CpfpInfo, string>> FetchAsync(SmartTransaction tx, IRPCClient rpcClient, Dictionary<uint256, CachedCpfpInfo> cache, EventBus eventBus, CancellationToken cancellationToken)
	{
		var txid = tx.GetHash();

		try
		{
			var entry = await rpcClient.GetMempoolEntryAsync(txid, throwIfNotFound: false, cancellationToken).ConfigureAwait(false);

			if (entry is null)
			{
				cache.Remove(txid);
				return Result<CpfpInfo, string>.Fail("The transaction is not in the mempool of the node.");
			}

			var cpfpInfo = CpfpInfo.FromMempoolEntry(entry);
			var isNews = !cache.TryGetValue(txid, out var known) || known.CpfpInfo != cpfpInfo;
			cache[txid] = new CachedCpfpInfo(cpfpInfo, tx);

			if (isNews)
			{
				eventBus.Publish(new CpfpInfoArrived());
			}

			return cpfpInfo;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception e)
		{
			// This includes requests that the node did not answer in time.
			return Result<CpfpInfo, string>.Fail(e.Message);
		}
	}
}
