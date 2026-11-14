using NBitcoin.Secp256k1;
using NBitcoin.WalletPolicies;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WalletWasabi.Blockchain.BlockFilters;
using WalletWasabi.CoinJoinProfiles;
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
	public const bool DefaultAutoCoinjoin = false;

	public const int AbsoluteMinGapLimit = 21;
	public const int MaxGapLimit = 10_000;
	public static readonly Money DefaultPlebStopThreshold = Money.Coins(0.005m);

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
		TaprootExternalKeyGenerator = new HdPubKeyGenerator(TaprootExtPubKey.Derive(0), TaprootAccountKeyPath.Derive(0), MinGapLimit);
		_taprootInternalKeyGenerator = new HdPubKeyGenerator(TaprootExtPubKey.Derive(1), TaprootAccountKeyPath.Derive(1), MinGapLimit);

		if (SilentPaymentScanExtPubKey is { })
		{
			_silentPaymentScanKeyGenerator = new HdPubKeyGenerator(SilentPaymentScanExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Scan), MinGapLimit);
		}

		if (SilentPaymentSpendExtPubKey is { })
		{
			_silentPaymentSpendKeyGenerator = new HdPubKeyGenerator(SilentPaymentSpendExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Spend), MinGapLimit);
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

	public ExtPubKey? SilentPaymentScanExtPubKey { get; private set; }

	public ExtPubKey? SilentPaymentSpendExtPubKey { get; private set; }

	private readonly BlockchainState _blockchainState;

	public bool AutoCoinJoin { get; set; } = DefaultAutoCoinjoin;

	/// <summary>
	/// Won't coinjoin automatically if the confirmed wallet balance is below this.
	/// </summary>
	public Money PlebStopThreshold { get; set; } = DefaultPlebStopThreshold;

	public int AnonScoreTarget { get; set; } = PrivacyProfiles.DefaultProfile.AnonScoreTarget;

	public bool NonPrivateCoinIsolation { get; set; } = PrivacyProfiles.DefaultProfile.NonPrivateCoinIsolation;

	public bool OnlyUsePrivateFundsForPayments { get; set; } = PrivacyProfiles.DefaultProfile.OnlyUsePrivateFundsForPayments;

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

	public string WalletName => string.IsNullOrWhiteSpace(FilePath) ? "" : Path.GetFileNameWithoutExtension(FilePath);

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
		var birthHeight = FilterCheckpoints.GetMostRecentCheckpoint(network).Header.Height;
		BlockchainState blockchainState = new(network, birthHeight: birthHeight);

		KeyPath taprootAccountKeyPath = GetAccountKeyPath(network);
		ExtPubKey taprootExtPubKey = extKey.Derive(taprootAccountKeyPath).Neuter();

		ExtPubKey silentPaymentScanExtPubKey = extKey.Derive(GetAccountKeyPath(network, KeyPurpose.Scan)).Neuter();
		ExtPubKey silentPaymentSpendExtPubKey = extKey.Derive(GetAccountKeyPath(network, KeyPurpose.Spend)).Neuter();

		return new KeyManager(encryptedSecret, extKey.ChainCode, masterFingerprint, taprootExtPubKey, silentPaymentScanExtPubKey, silentPaymentSpendExtPubKey, AbsoluteMinGapLimit, blockchainState, filePath, taprootAccountKeyPath);
	}

	/// <param name="shares">As many SLIP-39 shares as their threshold requires. They alone determine the keys of the wallet.</param>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It does not have to be the one the wallet was created with.</param>
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

		birthHeight ??= FilterCheckpoints.GetWasabiGenesisFilter(network).Header.Height;
		var blockchainState = new BlockchainState(network, height: birthHeight, birthHeight: birthHeight);
		var km = new KeyManager(encryptedSecret, extKey.ChainCode, masterFingerprint, taprootExtPubKey, silentPaymentScanExtPubKey, silentPaymentSpendExtPubKey, minGapLimit, blockchainState, filePath, taprootAccountKeyPath);
		km.AssertCleanKeysIndexedNoLock();
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
	/// This function can only be called for wallet synchronization.
	/// It's unsafe because it doesn't assert that the GapLimit is respected.
	/// GapLimit should be enforced whenever a transaction is discovered.
	/// </summary>
	public byte[][] UnsafeGetSynchronizationInfos()
	{
		lock (_criticalStateLock)
		{
			// A copy must be returned because the cache may change.
			return _hdPubKeyCache.Select(x => x.ScriptPubKeyBytes).ToArray();
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
		_silentPaymentScanKeyGenerator = new HdPubKeyGenerator(SilentPaymentScanExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Scan), MinGapLimit);
		_silentPaymentSpendKeyGenerator = new HdPubKeyGenerator(SilentPaymentSpendExtPubKey, GetAccountKeyPath(_blockchainState.Network, KeyPurpose.Spend), MinGapLimit);

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

	public ChainHeight GetBestHeight()
	{
		lock (_criticalStateLock)
		{
			return _blockchainState.Height;
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

	public void SetBestHeight(ChainHeight height, bool toFile = true)
	{
		lock (_criticalStateLock)
		{
			_blockchainState.Height = height;
			if (toFile)
			{
				ToFileNoLock();
			}
		}
	}

	public void SetResyncParameters(ChainHeight newStartingHeight, int newMinGapLimit)
	{
		lock (_criticalStateLock)
		{
			_blockchainState.Height = newStartingHeight;
			MinGapLimit = newMinGapLimit;
			ToFileNoLock();
		}
	}

	public void SetMaxBestHeight(ChainHeight newHeight)
	{
		lock (_criticalStateLock)
		{
			var prevHeight = _blockchainState.Height;
			if (newHeight < prevHeight)
			{
				SetBestHeight(newHeight);
				Logger.LogWarning($"Wallet ({WalletName}) height has been set back by {prevHeight - newHeight}. From {prevHeight} to {newHeight}.");
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
			("AutoCoinJoin", Encode.Bool(keyManager.AutoCoinJoin)),
			("PlebStopThreshold", Encode.MoneyBitcoins(keyManager.PlebStopThreshold)),
			("AnonScoreTarget", Encode.Int(keyManager.AnonScoreTarget)),
			("RedCoinIsolation", Encode.Bool(keyManager.NonPrivateCoinIsolation)),
			("OnlyUsePrivateFundsForPayments", Encode.Bool(keyManager.OnlyUsePrivateFundsForPayments)),
			("ExcludedCoinsFromCoinJoin", Encode.Array(keyManager.ExcludedCoinsFromCoinJoin.Select(Encode.Outpoint))),
			("CoinjoinCosts", Encode.Array(keyManager.CoinjoinCosts.Select(Encode.CoinjoinCosts))),
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
				AutoCoinJoin = get.Optional("AutoCoinJoin", Decode.Bool, false),
				PlebStopThreshold = get.Optional("PlebStopThreshold", Decode.MoneyBitcoins) ?? DefaultPlebStopThreshold,
				AnonScoreTarget = get.Optional("AnonScoreTarget", Decode.Int, 10),
				NonPrivateCoinIsolation = get.Optional("RedCoinIsolation", Decode.Bool, false),
				OnlyUsePrivateFundsForPayments = get.Optional("OnlyUsePrivateFundsForPayments", Decode.Bool, false),
				ExcludedCoinsFromCoinJoin = get.Optional("ExcludedCoinsFromCoinJoin", Decode.Array(Decode.OutPoint))?.ToList() ?? [],
				CoinjoinCosts = get.Optional("CoinjoinCosts", Decode.Array(Decode.CoinjoinCosts))?.ToDictionary() ?? []
			};
			km._hdPubKeyCache.AddRangeKeys(km.AssertKeysBelongToWallet(get.Required("HdPubKeys", Decode.Array(Decode.HdPubKey))));
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
