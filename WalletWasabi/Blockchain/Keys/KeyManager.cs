using NBitcoin.Secp256k1;
using NBitcoin.WalletPolicies;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Io;
using WalletWasabi.Models;
using WalletWasabi.Serialization;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.Wallets.SilentPayment;
using WalletWasabi.Wallets.Slip39;
using Decode = WalletWasabi.Serialization.Decode;
using Encode = WalletWasabi.Serialization.Encode;
using Network = NBitcoin.Network;
using OutPoint = NBitcoin.OutPoint;

namespace WalletWasabi.Blockchain.Keys;

public class KeyManager
{
	/// <summary>
	/// The gap limit of a wallet that was not given a wider one. On the chain of its receive addresses and on the chain of
	/// its change addresses, the wallet watches at least this many unused addresses after the last one that was used.
	/// </summary>
	/// <remarks>
	/// A recovered wallet does not find a payment that follows more unused addresses in a row than its gap limit.
	/// Every time the receive screen is opened it gives out a new address, so the unused ones can add up.
	/// </remarks>
	public const int AbsoluteMinGapLimit = 210;

	public const int MaxGapLimit = 10_000;

	/// <summary>
	/// The gap limit of the generators of the silent payment keys. Those keys are not addresses that are given out one after
	/// the other, so the gap limit of the wallet means nothing to them.
	/// </summary>
	private const int SilentPaymentKeyGapLimit = 21;

	/// <summary>What a coinjoin may leave over unless the user decides otherwise.</summary>
	public static readonly Money DefaultMaxCoinjoinLeftover = Money.Satoshis(5_000);

	/// <summary>The user cannot allow a coinjoin to leave over more than this.</summary>
	public static readonly Money HighestMaxCoinjoinLeftover = Money.Satoshis(10_000);

	internal KeyManager(
		BitcoinEncryptedSecretNoEC encryptedSecret,
		byte[] chainCode,
		HDFingerprint? masterFingerprint,
		ExtPubKey taprootExtPubKey,
		ExtPubKey? silentPaymentScanExtPubKey,
		ExtPubKey? silentPaymentSpendExtPubKey,
		int? minGapLimit,
		BlockchainState blockchainState,
		string? filePath = null,
		KeyPath? taprootAccountKeyPath = null)
	{
		EncryptedSecret = encryptedSecret;
		ChainCode = chainCode;
		MasterFingerprint = masterFingerprint;
		TaprootExtPubKey = taprootExtPubKey;
		SilentPaymentScanExtPubKey = silentPaymentScanExtPubKey;
		SilentPaymentSpendExtPubKey = silentPaymentSpendExtPubKey;

		MinGapLimit = Math.Max(AbsoluteMinGapLimit, minGapLimit ?? 0);

		_blockchainState = blockchainState;

		TaprootAccountKeyPath = taprootAccountKeyPath ?? GetAccountKeyPath(_blockchainState.Network);
		FidelityBonds = new FidelityBondKeys(TaprootExtPubKey, TaprootAccountKeyPath);

		// The wallet file remembers the unlock heights whose addresses the wallet watches.
		FidelityBonds.HeightRegistered += _ => ToFile();
		TaprootExternalKeyGenerator = new HdPubKeyGenerator(TaprootExtPubKey.Derive(0), TaprootAccountKeyPath.Derive(0), MinGapLimit);
		_taprootInternalKeyGenerator = new HdPubKeyGenerator(TaprootExtPubKey.Derive(1), TaprootAccountKeyPath.Derive(1), MinGapLimit);

		if (SilentPaymentScanExtPubKey is { })
		{
			_silentPaymentScanKeyGenerator = new HdPubKeyGenerator(SilentPaymentScanExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Scan), SilentPaymentKeyGapLimit);
		}

		if (SilentPaymentSpendExtPubKey is { })
		{
			_silentPaymentSpendKeyGenerator = new HdPubKeyGenerator(SilentPaymentSpendExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Spend), SilentPaymentKeyGapLimit);
		}

		SetFilePath(filePath);

		ToFile();
	}

	/// <remarks>Note that <see cref="GetNextReceiveKey(HdPubKeyGenerator)"/> can increase the minimum gap limit value.</remarks>
	private int GetEffectiveMinGapLimit() =>
		Math.Max(MinGapLimit, TaprootExternalKeyGenerator.MinGapLimit);

	/// <summary>The BIP86 account that the addresses of the wallet come from.</summary>
	public static KeyPath GetAccountKeyPath(Network network) =>
		GetAccountKeyPath(network, KeyPurpose.Loud);

	public static KeyPath GetAccountKeyPath(Network network, KeyPurpose purpose) =>
		new((network.Name, purpose) switch
		{
			("TestNet4", KeyPurpose.LoudPaymentKey) => "m/86h/1h/0h",
			("signet", KeyPurpose.LoudPaymentKey) => "m/86h/1h/0h",
			("RegTest", KeyPurpose.LoudPaymentKey) => "m/86h/0h/0h",
			("Main", KeyPurpose.LoudPaymentKey) => "m/86h/0h/0h",
			("TestNet4", KeyPurpose.SilentPaymentKey.ScanKey) => "m/352h/1h/0h/1h",
			("signet", KeyPurpose.SilentPaymentKey.ScanKey) => "m/352h/1h/0h/1h",
			("RegTest", KeyPurpose.SilentPaymentKey.ScanKey) => "m/352h/0h/0h/1h",
			("Main", KeyPurpose.SilentPaymentKey.ScanKey) => "m/352h/0h/0h/1h",
			("TestNet4", KeyPurpose.SilentPaymentKey.SpendKey) => "m/352h/1h/0h/0h",
			("signet", KeyPurpose.SilentPaymentKey.SpendKey) => "m/352h/1h/0h/0h",
			("RegTest", KeyPurpose.SilentPaymentKey.SpendKey) => "m/352h/0h/0h/0h",
			("Main", KeyPurpose.SilentPaymentKey.SpendKey) => "m/352h/0h/0h/0h",
			(_, KeyPurpose.LoudPaymentKey) => throw new ArgumentException($"Unknown account for network '{network}'."),
			(_, KeyPurpose.SilentPaymentKey) => throw new ArgumentException($"Unknown account for silentPayment and network '{network}'"),
			_ => throw new ArgumentException($"Unknown account for network '{network}' and key purpose.")
		});

	/// <summary>
	/// Public descriptor of the wallet's addresses with its checksum, see <seealso href="https://bips.dev/386/">BIP386</seealso>
	/// and <seealso href="https://bips.dev/389/">BIP389</seealso>, e.g. <c>tr([2fc4a4f3/86'/0'/0']xpub.../&lt;0;1&gt;/*)#udsxyp2a</c>.
	/// </summary>
	public string GetOutputDescriptor(Network network)
	{
		var origin = MasterFingerprint is { } fingerprint ? $"[{fingerprint}/{TaprootAccountKeyPath}]" : "";
		return Miniscript.AddChecksum($"tr({origin}{TaprootExtPubKey.ToString(network)}/<0;1>/*)");
	}

	#region Properties

	public BitcoinEncryptedSecretNoEC EncryptedSecret { get; }

	public byte[] ChainCode { get; }

	public HDFingerprint? MasterFingerprint { get; private set; }

	/// <summary>Extended public key of the BIP86 account. Every address of the wallet is derived from it.</summary>
	public ExtPubKey TaprootExtPubKey { get; }

	public int MinGapLimit { get; private set; }

	public KeyPath TaprootAccountKeyPath { get; }

	/// <summary>
	/// The keys of the fidelity bonds of the wallet. They are keys of the account too, but not addresses of the wallet: a
	/// bond pays to a script with a timelock.
	/// </summary>
	public FidelityBondKeys FidelityBonds { get; }

	/// <summary>See <see cref="GetFidelityBondSearchStart"/>.</summary>
	private uint? _fidelityBondSearchStart;

	public ExtPubKey? SilentPaymentScanExtPubKey { get; private set; }

	public ExtPubKey? SilentPaymentSpendExtPubKey { get; private set; }

	private readonly BlockchainState _blockchainState;

	/// <summary>
	/// The most that a coinjoin of this wallet may leave over. What a coinjoin cannot put into an output goes to the
	/// miners; above this limit it is added to an output instead. Every wallet has a limit, and it is never above
	/// <see cref="HighestMaxCoinjoinLeftover"/>.
	/// </summary>
	public Money MaxCoinjoinLeftover
	{
		get;
		set => field = Money.Min(Money.Max(value, Money.Zero), HighestMaxCoinjoinLeftover);
	} = DefaultMaxCoinjoinLeftover;

	/// <summary>A new wallet registers at most one non-private coin in a round.</summary>
	public bool NonPrivateCoinIsolation { get; set; } = true;

	public bool OnlyUsePrivateFundsForPayments { get; set; }

	public List<OutPoint> ExcludedCoinsFromCoinJoin { get; private set; } = new();

	private Dictionary<uint256, CoinjoinCosts> CoinjoinCosts { get; init; } = new();

	public string? FilePath { get; private set; }

	private readonly HdPubKeyCache _hdPubKeyCache = new();

	// `_criticalStateLock` is aimed to synchronize read/write access to the "critical" properties:
	// keys (stored in the `_hdPubKeyCache`), minGapLimit, secrets, height, network.
	private readonly Lock _criticalStateLock = new();

	#endregion Properties

	private HdPubKeyGenerator TaprootExternalKeyGenerator { get; set; }
	private readonly HdPubKeyGenerator _taprootInternalKeyGenerator;
	private HdPubKeyGenerator? _silentPaymentScanKeyGenerator;
	private HdPubKeyGenerator? _silentPaymentSpendKeyGenerator;
	private List<(SilentPaymentAddress Address, ECPrivKey ScanSecret)> _silentPaymentScanData = new();


	/// <summary>
	/// Creates a wallet from a fresh seed and gives back its SLIP-39 backup with the default threshold and share count.
	/// </summary>
	/// <param name="shares">All the shares of the backup.</param>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It is not part of the backup.</param>
	public static KeyManager CreateNew(out Share[] shares, string password, Network network, string? filePath = null)
	{
		shares = WalletGenerator.GenerateShares();

		return CreateNew(shares.Take(WalletGenerator.DefaultShamirThreshold).ToArray(), password, network, filePath);
	}

	/// <param name="shares">As many SLIP-39 shares as their threshold requires. They alone determine the keys of the wallet.</param>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It is not part of the backup.</param>
	public static KeyManager CreateNew(Share[] shares, string password, Network network, string? filePath = null)
	{
		password ??= "";
		var seed = Shamir.Combine(shares);
		return CreateNew(seed, password, network, filePath);
	}

	private static KeyManager CreateNew(byte[] seed, string password, Network network, string? filePath = null)
	{
		var extKey = ExtKey.CreateFromSeed(seed);
		var encryptedSecret = extKey.PrivateKey.GetEncryptedBitcoinSecret(password, network);

		HDFingerprint masterFingerprint = extKey.Neuter().PubKey.GetHDFingerPrint();

		// Where the history of the wallet starts is told once the height of the chain is known, see StartAt.
		BlockchainState blockchainState = new(network);

		KeyPath taprootAccountKeyPath = GetAccountKeyPath(network);
		ExtPubKey taprootExtPubKey = extKey.Derive(taprootAccountKeyPath).Neuter();

		ExtPubKey silentPaymentScanExtPubKey = extKey.Derive(GetAccountKeyPath(network, KeyPurpose.Scan)).Neuter();
		ExtPubKey silentPaymentSpendExtPubKey = extKey.Derive(GetAccountKeyPath(network, KeyPurpose.Spend)).Neuter();

		return new KeyManager(encryptedSecret, extKey.ChainCode, masterFingerprint, taprootExtPubKey, silentPaymentScanExtPubKey, silentPaymentSpendExtPubKey, AbsoluteMinGapLimit, blockchainState, filePath, taprootAccountKeyPath);
	}

	/// <param name="shares">As many SLIP-39 shares as their threshold requires. They alone determine the keys of the wallet.</param>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It does not have to be the one the wallet was created with.</param>
	/// <param name="birthHeight">Height of the first block to look for the transactions of the wallet in. By default the block that Taproot activated at.</param>
	public static KeyManager Recover(Share[] shares, string password, Network network, string? filePath = null, int minGapLimit = AbsoluteMinGapLimit, ChainHeight? birthHeight = null)
	{
		password ??= "";
		var seed = Shamir.Combine(shares);
		return Recover(seed, password, network, filePath, minGapLimit, birthHeight);
	}

	private static KeyManager Recover(byte[] seed, string password, Network network, string? filePath = null, int minGapLimit = AbsoluteMinGapLimit, ChainHeight? birthHeight = null)
	{
		ExtKey extKey = ExtKey.CreateFromSeed(seed);
		var encryptedSecret = extKey.PrivateKey.GetEncryptedBitcoinSecret(password, network);

		HDFingerprint masterFingerprint = extKey.Neuter().PubKey.GetHDFingerPrint();

		KeyPath taprootAccountKeyPath = GetAccountKeyPath(network);
		ExtPubKey taprootExtPubKey = extKey.Derive(taprootAccountKeyPath).Neuter();
		ExtPubKey silentPaymentScanExtPubKey = extKey.Derive(GetAccountKeyPath(network, KeyPurpose.Scan)).Neuter();
		ExtPubKey silentPaymentSpendExtPubKey = extKey.Derive(GetAccountKeyPath(network, KeyPurpose.Spend)).Neuter();

		birthHeight ??= network.TaprootActivationHeight;
		var blockchainState = new BlockchainState(network, birthHeight: birthHeight);
		blockchainState.ScanFrom(birthHeight);
		var km = new KeyManager(encryptedSecret, extKey.ChainCode, masterFingerprint, taprootExtPubKey, silentPaymentScanExtPubKey, silentPaymentSpendExtPubKey, minGapLimit, blockchainState, filePath, taprootAccountKeyPath);
		km.AssertCleanKeysIndexedNoLock();

		// The backup does not tell the unlock heights of the bonds of the wallet.
		km.SearchFidelityBondsFromNoLock(birthHeight);
		km.ToFile();
		return km;
	}

	public static KeyManager FromFile(string filePath)
	{
		if (!File.Exists(filePath))
		{
			throw new FileNotFoundException($"Wallet file not found at: `{filePath}`.");
		}

		string jsonString = File.SafelyReadAllText(filePath, Encoding.UTF8);

		KeyManager km = JsonDecoder.FromString(jsonString, Decoder)
			?? throw new DataException($"Wallet file at: `{filePath}` is not a valid wallet file or it is corrupted.");

		km.SetFilePath(filePath);

		return km;
	}

	public void SetFilePath(string? filePath)
	{
		FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
		if (FilePath is null)
		{
			return;
		}

		IoHelpers.EnsureContainingDirectoryExists(FilePath);
	}

	internal HdPubKey GenerateNewKey(KeyState keyState, bool isInternal)
	{
		var hdPubKeyRegistry = isInternal ? _taprootInternalKeyGenerator : TaprootExternalKeyGenerator;

		lock (_criticalStateLock)
		{
			var view = _hdPubKeyCache.GetView(hdPubKeyRegistry.KeyPath);
			var (keyPath, extPubKey) = hdPubKeyRegistry.GenerateNewKey(view);
			var hdPubKey = new HdPubKey(extPubKey.PubKey, keyPath, keyState);
			_hdPubKeyCache.AddKey(hdPubKey);
			return hdPubKey;
		}
	}

	/// <summary>
	/// Gives out a receive address that was not given out before, and remembers that it was.
	/// </summary>
	public HdPubKey GetNextReceiveKey() =>
		GetNextReceiveKey(KeyPurpose.Loud, handOut: true);

	/// <param name="handOut">Whether to remember that the key was given out, so that it is not offered again.</param>
	public HdPubKey GetNextReceiveKey(KeyPurpose purpose, bool handOut)
	{
		lock (_criticalStateLock)
		{
			var (generator, generatorSetter) = purpose switch
			{
				KeyPurpose.LoudPaymentKey => (TaprootExternalKeyGenerator, (Action<HdPubKeyGenerator>)(g => TaprootExternalKeyGenerator = g)),
				KeyPurpose.SilentPaymentKey.ScanKey => (_silentPaymentScanKeyGenerator, g => _silentPaymentScanKeyGenerator = g),
				KeyPurpose.SilentPaymentKey.SpendKey => (_silentPaymentSpendKeyGenerator, g => _silentPaymentSpendKeyGenerator = g),
				_ => throw new NotSupportedException("Key purpose is unknown.")
			};

			if (generator is null)
			{
				throw new NotSupportedException("Silent payment keys are not available in this wallet.");
			}

			var (newKey, newlyGeneratedKeySet, newHdPubKeyGenerator) = GetNextReceiveKey(generator);
			generatorSetter(newHdPubKeyGenerator);
			_hdPubKeyCache.AddRangeKeys(newlyGeneratedKeySet);
			if (handOut)
			{
				newKey.SetHandedOut();
			}

			ToFileNoLock();
			return newKey;
		}
	}

	private (HdPubKey, HdPubKey[], HdPubKeyGenerator) GetNextReceiveKey(HdPubKeyGenerator hdPubKeyGenerator)
	{
		// Find the next clean external key that was not given out yet.
		HdPubKeyPathView externalView;

		lock (_criticalStateLock)
		{
			externalView = _hdPubKeyCache.GetView(hdPubKeyGenerator.KeyPath);
		}

		if (externalView.CleanKeys.FirstOrDefault(x => !x.IsHandedOut) is { } cachedKey)
		{
			return (cachedKey, [], hdPubKeyGenerator);
		}

		var newHdPubKeyGenerator = hdPubKeyGenerator with { MinGapLimit = hdPubKeyGenerator.MinGapLimit + 1 };
		var newHdPubKeys = newHdPubKeyGenerator.AssertCleanKeysIndexed(externalView).Select(CreateHdPubKey).ToArray();

		var newKey = newHdPubKeys.First();
		return (newKey, newHdPubKeys, newHdPubKeyGenerator);
	}

	public HdPubKey GetNextChangeKey() =>
		GetKeys(x =>
			x.KeyState == KeyState.Clean &&
			x.IsInternal)
			.First();

	public ImmutableArray<HdPubKey> GetNextCoinJoinKeys() =>
		GetKeys(x =>
				x.KeyState == KeyState.Locked &&
				x.IsInternal == true);

	public ImmutableArray<HdPubKey> GetKeys(Func<HdPubKey, bool>? wherePredicate)
	{
		// BIP44-ish derivation scheme
		// m / purpose' / coin_type' / account' / change / address_index
		lock (_criticalStateLock)
		{
			AssertCleanKeysIndexedNoLock();
			var predicate = wherePredicate ?? (_ => true);
			return _hdPubKeyCache.HdPubKeys.Where(predicate).OrderBy(x => x.Index).ToImmutableArray();
		}
	}

	public ImmutableArray<HdPubKey> GetKeys(KeyState? keyState = null, bool? isInternal = null) =>
		(keyState, isInternal) switch
		{
			(null, null) => GetKeys(x => true),
			(null, { } i) => GetKeys(x => x.IsInternal == i),
			({ } k, null) => GetKeys(x => x.KeyState == k),
			({ } k, { } i) => GetKeys(x => x.IsInternal == i && x.KeyState == k)
		};

	/// <summary>
	/// The number of keys that the wallet watches. It grows when a key is used for the first time, to keep the gap of clean keys.
	/// </summary>
	public int GetKeyCount()
	{
		lock (_criticalStateLock)
		{
			return _hdPubKeyCache.Count;
		}
	}

	public bool TryGetKeyForScriptPubKey(Script scriptPubKey, [NotNullWhen(true)] out HdPubKey? hdPubKey)
	{
		lock (_criticalStateLock)
		{
			return _hdPubKeyCache.TryGetPubKey(scriptPubKey, out hdPubKey);
		}
	}

	public IEnumerable<Key> GetSecrets(string password, params Script[] scripts)
	{
		ExtKey extKey = GetMasterExtKey(password);

		foreach (HdPubKey key in GetKeys(x => scripts.Contains(x.P2Taproot)))
		{
			yield return extKey.Derive(key.FullKeyPath).PrivateKey;
		}
	}

	/// <summary>
	/// The address to pay to in order to lock coins until the chain reaches the height. The wallet watches the address
	/// from now on, and the wallet file remembers that before anyone gets to see the address.
	/// </summary>
	public BitcoinAddress GetFidelityBondAddress(uint unlockHeight)
	{
		FidelityBonds.Register(unlockHeight);
		return FidelityBonds.GetAddress(unlockHeight, GetNetwork());
	}

	/// <summary>The private key that redeems the bonds that unlock at the given height.</summary>
	/// <exception cref="SecurityException">The password is wrong.</exception>
	public Key GetFidelityBondSecret(string password, uint unlockHeight)
	{
		var key = GetMasterExtKey(password).Derive(FidelityBonds.GetKeyPath(unlockHeight)).PrivateKey;

		if (key.PubKey != FidelityBonds.GetPubKey(unlockHeight))
		{
			throw new InvalidOperationException($"The key of the fidelity bonds that unlock at block {unlockHeight} does not belong to the account of the wallet.");
		}

		return key;
	}

	/// <summary>
	/// The lowest unlock height that the wallet looks for bonds at without remembering their addresses, or null if it only
	/// watches the addresses that it remembers.
	/// </summary>
	/// <remarks>
	/// The backup of a wallet does not tell which unlock heights its bonds have. So a wallet that goes through the chain
	/// again, because it was recovered or told to resync, looks for a bond at every height that it could have chosen, from
	/// the first block that it goes through on. See <see cref="FidelityBondKeys.PrepareSearch"/>.
	/// </remarks>
	public uint? GetFidelityBondSearchStart()
	{
		lock (_criticalStateLock)
		{
			return _fidelityBondSearchStart;
		}
	}

	/// <summary>
	/// The wallet went through the chain: the bonds that were to be found are found, and their addresses are watched like
	/// the others from now on.
	/// </summary>
	public void EndFidelityBondSearch()
	{
		lock (_criticalStateLock)
		{
			if (_fidelityBondSearchStart is null)
			{
				return;
			}

			_fidelityBondSearchStart = null;
			ToFileNoLock();
		}

		FidelityBonds.EndSearch();
	}

	/// <summary>Makes the wallet look for bonds that it does not remember, in the blocks from the given one on.</summary>
	private void SearchFidelityBondsFromNoLock(ChainHeight firstBlockHeight)
	{
		// A bond is locked until a block that was still to come when its address was made.
		var start = Math.Max(1, firstBlockHeight.Height);
		_fidelityBondSearchStart = Math.Min(_fidelityBondSearchStart ?? uint.MaxValue, start);
	}

	private (byte[] PasswordHash, ExtKey MasterKey)? MasterKeyAndPasswordHash { get; set; }

	public ExtKey GetMasterExtKey(string password)
	{
		password ??= "";

		var passwordHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(password));

		if (MasterKeyAndPasswordHash is { MasterKey: var masterKey, PasswordHash: var storedPasswordHash })
		{
			if (!CryptographicOperations.FixedTimeEquals(passwordHash, storedPasswordHash))
			{
				throw new SecurityException("Invalid password.");
			}

			return masterKey;
		}

		try
		{
			Key secret = EncryptedSecret.GetKey(password);
			var extKey = new ExtKey(secret, ChainCode);

			MasterFingerprint ??= secret.PubKey.GetHDFingerPrint();
			DeriveSilentPaymentExtPubKeys(extKey);

			MasterKeyAndPasswordHash = (passwordHash, extKey);

			return extKey;
		}
		catch (SecurityException ex)
		{
			throw new SecurityException("Invalid password.", ex);
		}
	}

	public void SetKeyState(KeyState newKeyState, HdPubKey hdPubKey)
	{
		if (hdPubKey.KeyState == newKeyState)
		{
			return;
		}

		hdPubKey.SetKeyState(newKeyState);
		if (newKeyState is KeyState.Locked or KeyState.Used)
		{
			// Only the two chains of the account keep a gap of clean keys. Silent payment keys do not.
			if (GetHdPubKeyGenerator(hdPubKey) is { } keySource)
			{
				lock (_criticalStateLock)
				{
					var view = _hdPubKeyCache.GetView(keySource.KeyPath);
					var keys = keySource.AssertCleanKeysIndexed(view).Select(CreateHdPubKey);
					_hdPubKeyCache.AddRangeKeys(keys);
				}
			}
		}
	}

	/// <returns>The generator of the chain the key belongs to, or null if it is not a key of the account's receive or change chain.</returns>
	private HdPubKeyGenerator? GetHdPubKeyGenerator(HdPubKey hdPubKey)
	{
		var chain = hdPubKey.FullKeyPath.Parent;

		return chain == TaprootExternalKeyGenerator.KeyPath
			? TaprootExternalKeyGenerator
			: chain == _taprootInternalKeyGenerator.KeyPath
				? _taprootInternalKeyGenerator
				: null;
	}

	private void DeriveSilentPaymentExtPubKeys(ExtKey extKey)
	{
		SilentPaymentScanExtPubKey ??= extKey.Derive(GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Scan)).Neuter();
		SilentPaymentSpendExtPubKey ??= extKey.Derive(GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Spend)).Neuter();
		_silentPaymentScanKeyGenerator = new HdPubKeyGenerator(SilentPaymentScanExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Scan), SilentPaymentKeyGapLimit);
		_silentPaymentSpendKeyGenerator = new HdPubKeyGenerator(SilentPaymentSpendExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Spend), SilentPaymentKeyGapLimit);

		var defaultSilentPaymentAddress = new SilentPaymentAddress(0, GetNextReceiveKey(KeyPurpose.Scan, handOut: false).PubKey, GetNextReceiveKey(KeyPurpose.Spend, handOut: false).PubKey);
		Logger.LogDebug($"Default Silent Payment Address: {defaultSilentPaymentAddress.ToWip(_blockchainState.Network)} ");
		var scanKeys = GetKeys(x => x.FullKeyPath.GetAccountKeyPath() == GetAccountKeyPath(Network.Main, KeyPurpose.Scan));
		var spendKeys = GetKeys(x => x.FullKeyPath.GetAccountKeyPath() == GetAccountKeyPath(Network.Main, KeyPurpose.Spend));

		foreach (var (scanKey, spendKey) in Enumerable.Zip(scanKeys, spendKeys))
		{
			var address = new SilentPaymentAddress(0, scanKey.PubKey, spendKey.PubKey);
			var scanSecret = extKey.Derive(scanKey.FullKeyPath);
			_silentPaymentScanData.Add((address, ECPrivKey.Create(scanSecret.PrivateKey.ToBytes())));
		}
	}

	private void AssertCleanKeysIndexedNoLock()
	{
		var keys = new[]
			{
				_taprootInternalKeyGenerator,
				TaprootExternalKeyGenerator
			}
			.SelectMany(gen => gen.AssertCleanKeysIndexed(_hdPubKeyCache.GetView(gen.KeyPath)))
			.Select(CreateHdPubKey);

		_hdPubKeyCache.AddRangeKeys(keys);
	}

	/// <summary>
	/// Make sure there's always locked internal keys generated and indexed.
	/// </summary>
	public void AssertLockedInternalKeysIndexedAndPersist(int howMany)
	{
		lock (_criticalStateLock)
		{
			if (AssertLockedInternalKeysIndexedNoLock(howMany))
			{
				ToFileNoLock();
			}
		}
	}

	private bool AssertLockedInternalKeysIndexedNoLock(int howMany)
	{
		var hdPubKeyGenerator = _taprootInternalKeyGenerator;

		Guard.InRangeAndNotNull(nameof(howMany), howMany, 0, hdPubKeyGenerator.MinGapLimit);
		var internalView = _hdPubKeyCache.GetView(hdPubKeyGenerator.KeyPath);
		var lockedKeyCount = internalView.LockedKeys.Count();
		var missingLockedKeys = Math.Max(howMany - lockedKeyCount, 0);

		_hdPubKeyCache.AddRangeKeys(hdPubKeyGenerator.AssertCleanKeysIndexed(internalView).Select(CreateHdPubKey));

		var availableCandidates = _hdPubKeyCache
			.GetView(hdPubKeyGenerator.KeyPath)
			.CleanKeys
			.Take(missingLockedKeys)
			.ToList();

		foreach (var hdPubKeys in availableCandidates)
		{
			SetKeyState(KeyState.Locked, hdPubKeys);
		}

		return availableCandidates.Count > 0;
	}

	public void ToFile()
	{
		lock (_criticalStateLock)
		{
			ToFileNoLock();
		}
	}

	private void ToFileNoLock()
	{
		if (FilePath is not { } filePath)
		{
			return;
		}

		string jsonString = JsonEncoder.ToReadableString(this, EncodeKeyManagerNoLock);
		File.SafelyWriteAllText(filePath, jsonString, Encoding.UTF8);
	}

	#region _blockchainState

	/// <summary>Height of the last block that the wallet processed.</summary>
	/// <returns><c>null</c> as long as the wallet does not know where its history starts, see <see cref="StartAt"/>.</returns>
	public ChainHeight? GetBestHeight()
	{
		lock (_criticalStateLock)
		{
			return _blockchainState.Height;
		}
	}

	/// <summary>Height of the last block that the wallet processed, with the hashes of the last processed blocks. The last hash is the one of that block.</summary>
	public (ChainHeight? Height, ImmutableArray<uint256> BlockHashes) GetProcessedBlocks()
	{
		lock (_criticalStateLock)
		{
			return (_blockchainState.Height, [.. _blockchainState.BlockHashes]);
		}
	}

	public Network GetNetwork()
	{
		return _blockchainState.Network;
	}

	public ChainHeight? GetBirthHeight()
	{
		lock (_criticalStateLock)
		{
			return _blockchainState.BirthHeight;
		}
	}

	/// <summary>
	/// Tells a new wallet the height of the chain: nothing up to there concerns it.
	/// </summary>
	/// <remarks>Does nothing if the wallet knows already where its history starts.</remarks>
	public void StartAt(ChainHeight tipHeight)
	{
		lock (_criticalStateLock)
		{
			if (_blockchainState.Height is null)
			{
				_blockchainState.StartAt(tipHeight);
				ToFileNoLock();
			}
		}
	}

	/// <summary>
	/// Remembers the block as the last one that the wallet processed. The blocks that were processed before it stay remembered
	/// only when it is the next one after them.
	/// </summary>
	public void SetBestBlock(ChainHeight height, uint256 blockHash, bool toFile = true)
	{
		lock (_criticalStateLock)
		{
			_blockchainState.SetProcessedBlock(height, blockHash);
			if (toFile)
			{
				ToFileNoLock();
			}
		}
	}

	/// <summary>
	/// Makes the wallet look for its transactions again, from the given block on.
	/// </summary>
	public void SetResyncParameters(ChainHeight newStartingHeight, int newMinGapLimit)
	{
		lock (_criticalStateLock)
		{
			_blockchainState.ScanFrom(newStartingHeight);
			MinGapLimit = newMinGapLimit;

			// A bond that this wallet file does not remember may come up too: one of another copy of the wallet, for example.
			SearchFidelityBondsFromNoLock(newStartingHeight);
			ToFileNoLock();
		}
	}

	/// <summary>
	/// Sets the wallet back to the given height, if it is above it. The blocks above are going to be processed again.
	/// </summary>
	public void SetMaxBestHeight(ChainHeight newHeight)
	{
		lock (_criticalStateLock)
		{
			var prevHeight = _blockchainState.Height;
			if (_blockchainState.RewindTo(newHeight))
			{
				ToFileNoLock();
				Logger.LogWarning($"Wallet height has been set back by {prevHeight!.Height - newHeight.Height}. From {prevHeight} to {newHeight}.");
			}
		}
	}

	#endregion _blockchainState

	private static HdPubKey CreateHdPubKey((KeyPath KeyPath, ExtPubKey ExtPubKey) x) =>
		new(x.ExtPubKey.PubKey, x.KeyPath, KeyState.Clean);

	internal void SetExcludedCoinsFromCoinJoin(IEnumerable<OutPoint> excludedOutpoints)
	{
		ExcludedCoinsFromCoinJoin = excludedOutpoints.ToList();
		ToFile();
	}

	public CoinjoinCosts? GetCoinjoinCosts(uint256 transactionId)
	{
		lock (_criticalStateLock)
		{
			return CoinjoinCosts.GetValueOrDefault(transactionId);
		}
	}

	public void AddCoinjoinCosts(uint256 transactionId, CoinjoinCosts coinjoinCosts)
	{
		lock (_criticalStateLock)
		{
			CoinjoinCosts[transactionId] = coinjoinCosts;
			ToFileNoLock();
		}
	}

	private static JsonNode EncodeKeyManagerNoLock(KeyManager keyManager) =>
		Encode.Object([
			("EncryptedSecret", Encode.BitcoinEncryptedSecretNoEC(keyManager.EncryptedSecret)),
			("ChainCode", Encode.ChainCode(keyManager.ChainCode)),
			("MasterFingerprint", Encode.Optional(keyManager.MasterFingerprint, Encode.HDFingerprint)),
			("TaprootExtPubKey", Encode.ExtPubKey(keyManager.TaprootExtPubKey)),
			("SilentPaymentScanExtPubKey", Encode.Optional(keyManager.SilentPaymentScanExtPubKey, Encode.ExtPubKey)),
			("SilentPaymentSpendExtPubKey", Encode.Optional(keyManager.SilentPaymentSpendExtPubKey, Encode.ExtPubKey)),
			("MinGapLimit", Encode.Int(keyManager.GetEffectiveMinGapLimit())),
			("TaprootAccountKeyPath", Encode.KeyPath(keyManager.TaprootAccountKeyPath)),
			("BlockchainState", Encode.BlockchainState(keyManager._blockchainState)),
			("MaxCoinjoinLeftover", Encode.MoneyBitcoins(keyManager.MaxCoinjoinLeftover)),
			("RedCoinIsolation", Encode.Bool(keyManager.NonPrivateCoinIsolation)),
			("OnlyUsePrivateFundsForPayments", Encode.Bool(keyManager.OnlyUsePrivateFundsForPayments)),
			("ExcludedCoinsFromCoinJoin", Encode.Array(keyManager.ExcludedCoinsFromCoinJoin.Select(Encode.Outpoint))),
			("CoinjoinCosts", Encode.Array(keyManager.CoinjoinCosts.Select(Encode.CoinjoinCosts))),
			("FidelityBondHeights", Encode.Array(keyManager.FidelityBonds.KnownHeights.Select(Encode.UInt))),
			("FidelityBondSearchStart", keyManager._fidelityBondSearchStart is { } searchStart ? Encode.UInt(searchStart) : null),
			("HdPubKeys", Encode.Array(keyManager._hdPubKeyCache.HdPubKeys.Select(Encode.HdPubKey)))
		]);

	private static readonly Decoder<KeyManager> Decoder =
		Decode.Object(get =>
		{
			var fingerprint = Decode.Field("MasterFingerprint", Decode.HDFingerprint)(get.Value).Match(v => v, _ => (HDFingerprint?)null);

			// todo: review again
			var blockchainState = get.Required("BlockchainState", Decode.BlockchainState);
			var network = blockchainState.Network;
			var encryptedSecretDecoder = Decode.String.Map(s => new BitcoinEncryptedSecretNoEC(s, network));
			var km = new KeyManager(
				get.Required("EncryptedSecret", encryptedSecretDecoder),
				get.Required("ChainCode", Decode.ByteArray),
				fingerprint,
				get.Required("TaprootExtPubKey", Decode.ExtPubKey),
				get.Optional("SilentPaymentScanExtPubKey", Decode.ExtPubKey),
				get.Optional("SilentPaymentSpendExtPubKey", Decode.ExtPubKey),
				get.Optional("MinGapLimit", Decode.Int),
				blockchainState,
				(string?) "",
				get.Optional("TaprootAccountKeyPath", Decode.KeyPath)
			)
			{
				MaxCoinjoinLeftover = get.Optional("MaxCoinjoinLeftover", Decode.MoneyBitcoins) ?? DefaultMaxCoinjoinLeftover,
				NonPrivateCoinIsolation = get.Optional("RedCoinIsolation", Decode.Bool, false),
				OnlyUsePrivateFundsForPayments = get.Optional("OnlyUsePrivateFundsForPayments", Decode.Bool, false),
				ExcludedCoinsFromCoinJoin = get.Optional("ExcludedCoinsFromCoinJoin", Decode.Array(Decode.OutPoint))?.ToList() ?? [],
				CoinjoinCosts = get.Optional("CoinjoinCosts", Decode.Array(Decode.CoinjoinCosts))?.ToDictionary() ?? []
			};
			km._hdPubKeyCache.AddRangeKeys(km.AssertKeysBelongToWallet(get.Required("HdPubKeys", Decode.Array(Decode.HdPubKey))));
			km.FidelityBonds.Load(get.Optional("FidelityBondHeights", Decode.Array(Decode.UInt)) ?? []);

			// The field is missing unless a search is on. A search never starts below the first block.
			km._fidelityBondSearchStart = get.Optional("FidelityBondSearchStart", Decode.UInt, 0u) is var searchStart and > 0 ? searchStart : null;
			return km;
		});

	/// <summary>
	/// Every key is turned into a Taproot address, so a wallet file may only hold keys of the wallet's own BIP86
	/// account (and its silent payment keys). Keys of other accounts, for example of a SegWit v0 account written by
	/// another wallet software, would silently become addresses that nobody ever paid to.
	/// </summary>
	private HdPubKey[] AssertKeysBelongToWallet(HdPubKey[] keys)
	{
		var network = GetNetwork();
		KeyPath[] chains =
		[
			TaprootExternalKeyGenerator.KeyPath,
			_taprootInternalKeyGenerator.KeyPath,
			GetAccountKeyPath(network, KeyPurpose.Scan),
			GetAccountKeyPath(network, KeyPurpose.Spend)
		];

		if (keys.FirstOrDefault(key => !chains.Contains(key.FullKeyPath.Parent)) is { } foreignKey)
		{
			throw new NotSupportedException($"The wallet file holds the key {foreignKey.FullKeyPath}, which is not a key of the wallet's Taproot account {TaprootAccountKeyPath}.");
		}

		return keys;
	}
}

public static class HdPubKeyExtensions
{
	/// <summary>The Taproot (BIP86) address of the key.</summary>
	public static BitcoinAddress GetAddress(this HdPubKey me, Network network) =>
		me.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, network);

	/// <summary>The Taproot (BIP86) script of the key.</summary>
	public static Script GetAssumedScriptPubKey(this HdPubKey me) =>
		me.P2Taproot;
}

public abstract record KeyPurpose
{
	public static readonly KeyPurpose Scan = new SilentPaymentKey.ScanKey();
	public static readonly KeyPurpose Spend = new SilentPaymentKey.SpendKey();
	public static readonly KeyPurpose Account = new SilentPaymentKey.AccountKey();

	/// <summary>A key of an ordinary (Taproot) address.</summary>
	public static readonly KeyPurpose Loud = new LoudPaymentKey();

	public abstract record SilentPaymentKey : KeyPurpose
	{
		public record ScanKey : SilentPaymentKey;

		public record SpendKey : SilentPaymentKey;
		public record AccountKey : SilentPaymentKey;
	};

	public record LoudPaymentKey : KeyPurpose;
}
