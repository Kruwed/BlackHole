using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.JoinMarket;
using WalletWasabi.NostrRelay;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// The Nostr relay of the application with its bond filter, against a real node: the offers of a wallet prove the
/// bond of the wallet, the announcement of a coordinator shows a proof that a wallet made for it, and it is the node
/// that says whether the chain has that bond.
/// </summary>
[Collection("Integration tests")]
public class NostrRelayBondFilterTests
{
	private readonly IntegrationTestFixture _fixture;

	public NostrRelayBondFilterTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 180_000)]
	public async Task Relay_TakesOffersThatProveABondOfTheWalletToTheNode()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		var tip = (uint)await env.RpcClient.GetBlockCountAsync(cancellationToken);
		var unlockHeight = tip + 6;

		// The wallet locks 0.3 BTC in a bond, and has a coin that is no bond.
		await env.FundAddressAsync(keyManager.GetFidelityBondAddress(unlockHeight), Money.Coins(0.3m), confirmations: 1);
		var coinAddress = keyManager.GetNextReceiveKey().GetAddress(env.Network);
		var coinTransactionId = await env.FundAddressAsync(coinAddress, Money.Coins(1m), confirmations: 1);

		var keys = Enumerable.Range(0, 5).Select(_ => ECPrivKey.Create(RandomUtils.GetBytes(32))).ToArray();

		await wallet.StartAsync(cancellationToken);

		try
		{
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Count(bond => bond.IsConfirmed) == 1, TimeSpan.FromSeconds(30));
			await env.WaitForWalletToCatchUpAsync(wallet, TimeSpan.FromSeconds(60));
			Assert.True(wallet.TryLogin(RegTestEnvironment.DefaultPassword));

			// The relay of somebody else: it asks for 0.1 BTC behind every key, and looks bonds up with its node.
			long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			uint? heightThatTheNodeTold = null;

			using var checker = new NodeBondChecker(env.RpcClient);
			await using var relay = new NostrRelayServer(
				new BlackHoleEventSet(env.Network),
				now: () => DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time)),

				// One visitor shows more bonds and sends more events here than the relay lets one visitor do in a row.
				limits: RelayLimits.ForBlackHoleEvents with { VisitorBondLookupBurst = 20, EventBurst = 30 },
				senders: new RelaySenderRules(IsBondRequired: true, MinBond: 10_000_000, Whitelist: []),
				checkBond: checker.CheckAsync,
				getTipHeight: () => heightThatTheNodeTold);

			relay.OnionHost = $"127.0.0.1:{relay.EndPoint.Port}";

			using var socket = new ClientWebSocket();
			await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{relay.EndPoint.Port}/"), cancellationToken);

			async Task<string> OfferAsync(ECPrivKey key, BondProof? proof)
			{
				var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 100_000_000, Points: 3, MatchedInputs: 1, ExtraInputs: 0, Bond: proof);
				var signed = await MakerOfferEvent.Create(offer, env.Network, DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time))).ComputeIdAndSignAsync(key);
				return JsonSerializer.Serialize(signed);
			}

			BondProof ProofOfTheWallet(ECPrivKey key) =>
				wallet.GetBondBacking(key.CreateXOnlyPubKey().ToBytes())?.Proof ?? throw new InvalidOperationException("The wallet has no bond to show.");

			long? ShareOf(ECPrivKey key) => relay.Store.GetSender(Convert.ToHexStringLower(key.CreateXOnlyPubKey().ToBytes()))?.Share;

			const string NeedsABond = "restricted: this relay takes events from keys with a fidelity bond of 0.1 BTC or more behind them";
			const string NoSuchBond = "restricted: the chain has no fidelity bond that is locked and is the one that the event shows";

			// An offer without a bond is not taken.
			Assert.Equal((false, NeedsABond), await PublishAsync(socket, await OfferAsync(keys[0], proof: null), cancellationToken));

			// The offer of the wallet shows the bond of the wallet, and the node finds it: 0.3 BTC.
			var firstOffer = await OfferAsync(keys[0], ProofOfTheWallet(keys[0]));
			Assert.Equal((true, ""), await PublishAsync(socket, firstOffer, cancellationToken));
			Assert.Equal(30_000_000, ShareOf(keys[0]));
			Assert.Equal([GetId(firstOffer)], await QueryAsync(socket, cancellationToken));

			// Two more offers of the wallet show the same bond with keys of their own: each has a third of it behind it.
			Volatile.Write(ref time, time + 60);
			Assert.Equal((true, ""), await PublishAsync(socket, await OfferAsync(keys[1], ProofOfTheWallet(keys[1])), cancellationToken));
			Assert.Equal(15_000_000, ShareOf(keys[0]));

			Volatile.Write(ref time, time + 60);
			Assert.Equal((true, ""), await PublishAsync(socket, await OfferAsync(keys[2], ProofOfTheWallet(keys[2])), cancellationToken));
			Assert.All(keys[..3], key => Assert.Equal(10_000_000, ShareOf(key)));
			Assert.Equal(3, (await QueryAsync(socket, cancellationToken)).Length);

			// The bond holds three shares of 0.1 BTC. With a fourth offer it stands behind the three that showed it last.
			Volatile.Write(ref time, time + 60);
			Assert.Equal((true, ""), await PublishAsync(socket, await OfferAsync(keys[3], ProofOfTheWallet(keys[3])), cancellationToken));
			Assert.Null(ShareOf(keys[0]));
			Assert.All(keys[1..4], key => Assert.Equal(10_000_000, ShareOf(key)));

			var kept = await QueryAsync(socket, cancellationToken);
			Assert.Equal(3, kept.Length);
			Assert.DoesNotContain(GetId(firstOffer), kept);

			// Whoever has the key of a bond can sign for any coin. A coin that is no bond is none for the node.
			var coinTransaction = await env.RpcClient.GetRawTransactionAsync(coinTransactionId, cancellationToken: cancellationToken);
			var coinIndex = coinTransaction.Outputs.AsIndexedOutputs().Single(output => output.TxOut.ScriptPubKey == coinAddress.ScriptPubKey).N;

			using (var bondKey = keyManager.GetFidelityBondSecret(RegTestEnvironment.DefaultPassword, unlockHeight))
			{
				var notABond = BondProof.Create(bondKey, new OutPoint(coinTransactionId, coinIndex), unlockHeight, keys[4].CreateXOnlyPubKey().ToBytes());
				Assert.Equal((false, NoSuchBond), await PublishAsync(socket, await OfferAsync(keys[4], notABond), cancellationToken));

				// And a longer lock than the bond has is another script than the one of the coin.
				var longer = BondProof.Create(bondKey, ProofOfTheWallet(keys[4]).OutPoint, unlockHeight + 50_000, keys[4].CreateXOnlyPubKey().ToBytes());
				Assert.Equal((false, NoSuchBond), await PublishAsync(socket, await OfferAsync(keys[4], longer), cancellationToken));
			}

			// A relay that asks for more than the bond holds takes none of the offers.
			await using (var strict = new NostrRelayServer(
				new BlackHoleEventSet(env.Network),
				senders: new RelaySenderRules(IsBondRequired: true, MinBond: 30_000_001, Whitelist: []),
				checkBond: checker.CheckAsync))
			{
				strict.OnionHost = $"127.0.0.1:{strict.EndPoint.Port}";

				using var strictSocket = new ClientWebSocket();
				await strictSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{strict.EndPoint.Port}/"), cancellationToken);

				Volatile.Write(ref time, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
				Assert.Equal(
					(false, "restricted: the fidelity bond holds 0.3 BTC, and this relay takes events from keys with 0.30000001 BTC or more behind them"),
					await PublishAsync(strictSocket, await OfferAsync(keys[4], ProofOfTheWallet(keys[4])), cancellationToken));
			}

			// The chain reaches the height of the bond: it can be spent now, and stands behind nothing.
			var height = (uint)await env.RpcClient.GetBlockCountAsync(cancellationToken);
			await env.RpcClient.GenerateAsync((int)(unlockHeight - height));
			heightThatTheNodeTold = unlockHeight;

			Volatile.Write(ref time, time + 60);
			var withdrawal = JsonSerializer.Serialize(
				await MakerOfferEvent.CreateWithdrawal(env.Network, DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time))).ComputeIdAndSignAsync(keys[3]));

			Assert.Equal((false, NeedsABond), await PublishAsync(socket, withdrawal, cancellationToken));
			Assert.All(keys, key => Assert.Null(ShareOf(key)));
			Assert.Empty(await QueryAsync(socket, cancellationToken));

			// Shown again, it is no bond for the node either.
			using (var bondKey = keyManager.GetFidelityBondSecret(RegTestEnvironment.DefaultPassword, unlockHeight))
			{
				var bondOutPoint = Assert.Single(wallet.GetFidelityBonds()).OutPoint;
				var late = BondProof.Create(bondKey, bondOutPoint, unlockHeight, keys[4].CreateXOnlyPubKey().ToBytes());
				Assert.Equal((false, NoSuchBond), await PublishAsync(socket, await OfferAsync(keys[4], late), cancellationToken));
			}
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);

			foreach (var key in keys)
			{
				key.Dispose();
			}
		}
	}

	/// <summary>
	/// The announcement of a coordinator is filtered by its bond like the offer of a maker. The coordinator has no
	/// wallet: a wallet with a bond makes the proof for the key of the coordinator, the config of the announcer
	/// carries it, the announcement shows it, and the node of the relay finds the bond.
	/// </summary>
	[Fact(Timeout = 180_000)]
	public async Task Relay_TakesTheAnnouncementOfACoordinatorByTheBondThatAWalletProvesForIt()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		var tip = (uint)await env.RpcClient.GetBlockCountAsync(cancellationToken);
		var unlockHeight = tip + 6;

		await env.FundAddressAsync(keyManager.GetFidelityBondAddress(unlockHeight), Money.Coins(0.3m), confirmations: 1);

		using var coordinatorKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
		using var offerKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
		var coordinator = Convert.ToHexStringLower(coordinatorKey.CreateXOnlyPubKey().ToBytes());

		await wallet.StartAsync(cancellationToken);

		try
		{
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Count(bond => bond.IsConfirmed) == 1, TimeSpan.FromSeconds(30));
			await env.WaitForWalletToCatchUpAsync(wallet, TimeSpan.FromSeconds(60));
			Assert.True(wallet.TryLogin(RegTestEnvironment.DefaultPassword));

			long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			uint? heightThatTheNodeTold = null;
			DateTimeOffset Clock() => DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time));

			using var checker = new NodeBondChecker(env.RpcClient);

			NostrRelayServer StartRelay(long minBond)
			{
				var relay = new NostrRelayServer(
					new BlackHoleEventSet(env.Network),
					now: Clock,
					senders: new RelaySenderRules(IsBondRequired: true, MinBond: minBond, Whitelist: []),
					checkBond: checker.CheckAsync,
					getTipHeight: () => heightThatTheNodeTold);

				relay.OnionHost = $"127.0.0.1:{relay.EndPoint.Port}";
				return relay;
			}

			await using var relay = StartRelay(10_000_000);
			await using var strictRelay = StartRelay(50_000_000);
			var address = new Uri($"ws://127.0.0.1:{relay.EndPoint.Port}/");
			var strictAddress = new Uri($"ws://127.0.0.1:{strictRelay.EndPoint.Port}/");

			// The wallet makes the proof of its bond for the key of the coordinator, as one text. That text goes into the config of the announcer.
			var proof = wallet.GetBondBacking(coordinatorKey.CreateXOnlyPubKey().ToBytes())?.Proof.ToText();
			Assert.NotNull(proof);

			var config = new AnnouncerConfig
			{
				IsEnabled = true,
				CoordinatorUri = "http://coordinator.example/",
				ReadMoreUri = "http://coordinator.example/",
				Key = coordinatorKey.ToNIP19(),
				RelayUris = [address.ToString(), strictAddress.ToString()]
			};

			async Task<IReadOnlyList<RelayAnswer>> AnnounceAsync(AnnouncerConfig announcerConfig)
			{
				using var announcer = new CoordinatorAnnouncer(announcerConfig, env.Network, rpcClient: env.RpcClient, now: Clock);
				return await announcer.AnnounceAsync(cancellationToken);
			}

			const string NeedsABond = "restricted: this relay takes events from keys with a fidelity bond of 0.1 BTC or more behind them";

			// Without the proof, neither relay takes the announcement.
			var answers = await AnnounceAsync(config);
			Assert.Equal(
				[new RelayAnswer(address, false, NeedsABond), new RelayAnswer(strictAddress, false, "restricted: this relay takes events from keys with a fidelity bond of 0.5 BTC or more behind them")],
				answers);

			// With it, the relay that asks for 0.1 BTC takes it: its node finds the bond of 0.3 BTC. The relay that asks for 0.5 BTC does not.
			Volatile.Write(ref time, time + 60);
			answers = await AnnounceAsync(config with { BondProof = proof });
			Assert.Equal(
				[
					new RelayAnswer(address, true, ""),
					new RelayAnswer(strictAddress, false, "restricted: the fidelity bond holds 0.3 BTC, and this relay takes events from keys with 0.5 BTC or more behind them")
				],
				answers);
			Assert.Equal(30_000_000, relay.Store.GetSender(coordinator)?.Share);
			Assert.Equal(1, relay.EventCount);

			// An offer of the same wallet shows the same bond: the coordinator and the offer have half of it each.
			using var socket = new ClientWebSocket();
			await socket.ConnectAsync(address, cancellationToken);

			Volatile.Write(ref time, time + 60);
			var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 100_000_000, Points: 3, MatchedInputs: 1, ExtraInputs: 0,
				Bond: wallet.GetBondBacking(offerKey.CreateXOnlyPubKey().ToBytes())!.Proof);
			var signedOffer = JsonSerializer.Serialize(await MakerOfferEvent.Create(offer, env.Network, Clock()).ComputeIdAndSignAsync(offerKey));

			Assert.Equal((true, ""), await PublishAsync(socket, signedOffer, cancellationToken));
			Assert.Equal(15_000_000, relay.Store.GetSender(coordinator)?.Share);
			Assert.Equal(2, (await QueryAsync(socket, cancellationToken)).Length);

			// The proof of another key is not one for this coordinator: the announcer does not show it, and says why.
			using var otherKey = ECPrivKey.Create(RandomUtils.GetBytes(32));
			var foreign = config with { BondProof = wallet.GetBondBacking(otherKey.CreateXOnlyPubKey().ToBytes())!.Proof.ToText() };
			Assert.Null(CoordinatorAnnouncer.ReadBond(foreign, out var problem));
			Assert.Contains(coordinatorKey.CreateXOnlyPubKey().ToNIP19(), problem, StringComparison.Ordinal);

			// The chain reaches the height of the bond. It stands behind nothing any more, and the relay drops what it kept.
			var height = (uint)await env.RpcClient.GetBlockCountAsync(cancellationToken);
			await env.RpcClient.GenerateAsync((int)(unlockHeight - height));
			heightThatTheNodeTold = unlockHeight;

			Volatile.Write(ref time, time + 15 * 60);
			answers = await AnnounceAsync(config with { BondProof = proof, RelayUris = [address.ToString()] });
			Assert.Equal([new RelayAnswer(address, false, "restricted: the chain has no fidelity bond that is locked and is the one that the event shows")], answers);
			Assert.Null(relay.Store.GetSender(coordinator));
			Assert.Empty(await QueryAsync(socket, cancellationToken));
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	private static string GetId(string eventJson)
	{
		using var json = JsonDocument.Parse(eventJson);
		return json.RootElement.GetProperty("id").GetString()!;
	}

	private static Task SendAsync(ClientWebSocket socket, string message, CancellationToken cancellationToken) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

	private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		using var message = new System.IO.MemoryStream();
		var buffer = new byte[16 * 1024];

		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, cancellationToken);

			if (result.MessageType == WebSocketMessageType.Close)
			{
				throw new System.IO.IOException("The relay closed the connection.");
			}

			message.Write(buffer, 0, result.Count);

			if (result.EndOfMessage)
			{
				return JsonDocument.Parse(message.ToArray());
			}
		}
	}

	/// <summary>Sends an event and reads what the relay answers about it.</summary>
	private static async Task<(bool IsAccepted, string Message)> PublishAsync(ClientWebSocket socket, string eventJson, CancellationToken cancellationToken)
	{
		await SendAsync(socket, $"[\"EVENT\",{eventJson}]", cancellationToken);

		using var answer = await ReceiveAsync(socket, cancellationToken);
		var root = answer.RootElement;

		Assert.Equal("OK", root[0].GetString());

		return (root[2].GetBoolean(), root[3].GetString()!);
	}

	/// <summary>Asks the relay for everything that it keeps, and tells the ids of the events.</summary>
	private static async Task<string[]> QueryAsync(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		await SendAsync(socket, "[\"REQ\",\"all\",{}]", cancellationToken);

		var ids = new List<string>();

		while (true)
		{
			using var message = await ReceiveAsync(socket, cancellationToken);
			var root = message.RootElement;

			if (root[0].GetString() == "EOSE")
			{
				await SendAsync(socket, "[\"CLOSE\",\"all\"]", cancellationToken);
				return [.. ids];
			}

			Assert.Equal("EVENT", root[0].GetString());
			ids.Add(root[2].GetProperty("id").GetString()!);
		}
	}
}
