using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tor;
using WalletWasabi.Tor.Control;
using WalletWasabi.Tor.Control.Messages;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Tor;

/// <summary>
/// The onion service of the application: Tor is asked for it, its key is kept so that its address stays the same, it
/// is made again whenever Tor starts again, and Tor is told to forget it when it is taken down.
/// </summary>
public class OnionServiceHostTests
{
	private static readonly OnionServiceOptions Options = new(80, new IPEndPoint(IPAddress.Loopback, 45123)) { MaxStreamsPerCircuit = 4 };

	private static readonly string Host = $"{FakeTor.DefaultServiceId}.onion";

	/// <summary>The key file of a test, in a folder of its own that is empty when the test starts.</summary>
	private static async Task<OnionServiceKeyFile> NewKeyFileAsync([CallerMemberName] string testName = "") =>
		new(Path.Combine(await Common.GetEmptyWorkDirAsync(callerMemberName: testName), "Relay", "OnionServiceKey"));

	private static bool IsOnItsWay(OnionServiceState state) =>
		state.Status is OnionServiceStatus.WaitingForTor or OnionServiceStatus.Publishing;

	/// <summary>
	/// The first time there is no key: Tor makes one, and it is written down before the address is given out. The
	/// service counts as published once Tor tells so.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task NewServiceGetsAKeyThatIsKeptAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();

		await using var tor = new FakeTor();
		await using var host = new OnionServiceHost(_ => Task.FromResult(tor.Attempt), keyFile, Options);

		Assert.Equal(OnionServiceState.Stopped, host.State);

		host.Start();

		Assert.Equal(
			"ADD_ONION NEW:ED25519-V3 Flags=MaxStreamsCloseCircuit MaxStreams=4 Port=80,127.0.0.1:45123",
			await tor.WaitForCommandAsync("ADD_ONION", timeoutCts.Token));

		var made = await host.WaitWhileAsync(state => state.Status == OnionServiceStatus.WaitingForTor, timeoutCts.Token);

		Assert.Equal(new OnionServiceState(OnionServiceStatus.Publishing, Host), made);
		Assert.Equal(FakeTor.DefaultKey, keyFile.Read());

		// Tor is asked for its events before it is asked for the service, so that none is missed.
		Assert.Equal(["SETEVENTS HS_DESC", "ADD_ONION NEW:ED25519-V3 Flags=MaxStreamsCloseCircuit MaxStreams=4 Port=80,127.0.0.1:45123"], tor.Commands);

		// What Tor tells about another service says nothing about this one.
		await tor.PublishAsync(new string('b', 55) + "d");
		await tor.PublishAsync();

		var published = await host.WaitWhileAsync(IsOnItsWay, timeoutCts.Token);

		Assert.Equal(new OnionServiceState(OnionServiceStatus.Published, Host), published);

		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile.Path));
		}
	}

	/// <summary>With a key from before, Tor is asked for the service of that key, which has the address of before.</summary>
	[Fact(Timeout = 120000)]
	public async Task KnownServiceIsMadeFromItsKeyAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();
		keyFile.Write(FakeTor.DefaultKey);

		await using var tor = new FakeTor();
		await using var host = new OnionServiceHost(_ => Task.FromResult(tor.Attempt), keyFile, Options);

		host.Start();

		Assert.Equal(
			$"ADD_ONION {FakeTor.DefaultKey} Flags=DiscardPK,MaxStreamsCloseCircuit MaxStreams=4 Port=80,127.0.0.1:45123",
			await tor.WaitForCommandAsync("ADD_ONION", timeoutCts.Token));

		var made = await host.WaitWhileAsync(state => state.Status == OnionServiceStatus.WaitingForTor, timeoutCts.Token);

		Assert.Equal(new OnionServiceState(OnionServiceStatus.Publishing, Host), made);
	}

	/// <summary>
	/// A Tor that stops takes the service with it. The Tor that is started in its place is asked for the service
	/// again, with the same key.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task ServiceIsMadeAgainWhenTorStartsAgainAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();

		await using var firstTor = new FakeTor();
		await using var secondTor = new FakeTor();

		var currentTor = firstTor;
		var secondTorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		async Task<(CancellationToken, TorControlClient?)> WaitForTorAsync(CancellationToken cancellationToken)
		{
			if (Volatile.Read(ref currentTor) == firstTor && firstTor.Stopped.IsCancellationRequested)
			{
				// Tor is starting again, which takes its time.
				await secondTorStarted.Task.WaitAsync(cancellationToken);
				Volatile.Write(ref currentTor, secondTor);
			}

			return Volatile.Read(ref currentTor).Attempt;
		}

		await using var host = new OnionServiceHost(WaitForTorAsync, keyFile, Options);

		host.Start();
		await tor1PublishedAsync();

		await firstTor.StopAsync();

		var waiting = await host.WaitWhileAsync(state => state.Status == OnionServiceStatus.Published, timeoutCts.Token);

		Assert.Equal(new OnionServiceState(OnionServiceStatus.WaitingForTor), waiting);

		secondTorStarted.SetResult();

		Assert.Equal(
			$"ADD_ONION {FakeTor.DefaultKey} Flags=DiscardPK,MaxStreamsCloseCircuit MaxStreams=4 Port=80,127.0.0.1:45123",
			await secondTor.WaitForCommandAsync("ADD_ONION", timeoutCts.Token));

		await host.WaitWhileAsync(state => state.Status == OnionServiceStatus.WaitingForTor, timeoutCts.Token);
		await secondTor.PublishAsync();

		Assert.Equal(new OnionServiceState(OnionServiceStatus.Published, Host), await host.WaitWhileAsync(IsOnItsWay, timeoutCts.Token));

		// A Tor that stopped has forgotten the service: it is not told to.
		Assert.DoesNotContain(firstTor.Commands, command => command.StartsWith("DEL_ONION", StringComparison.Ordinal));

		async Task tor1PublishedAsync()
		{
			await host.WaitWhileAsync(state => state.Status == OnionServiceStatus.WaitingForTor, timeoutCts.Token);
			await firstTor.PublishAsync();
			await host.WaitWhileAsync(IsOnItsWay, timeoutCts.Token);
		}
	}

	/// <summary>Taking the service down tells Tor to forget it: Tor would keep it for as long as the application runs.</summary>
	[Fact(Timeout = 120000)]
	public async Task TorForgetsAServiceThatIsTakenDownAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();

		await using var tor = new FakeTor();
		var host = new OnionServiceHost(_ => Task.FromResult(tor.Attempt), keyFile, Options);

		host.Start();
		await host.WaitWhileAsync(state => state.Status == OnionServiceStatus.WaitingForTor, timeoutCts.Token);

		await host.DisposeAsync();

		Assert.Equal(OnionServiceState.Stopped, host.State);
		Assert.Equal($"DEL_ONION {FakeTor.DefaultServiceId}", tor.Commands[^1]);

		// The key stays: the service has its address again when it is wanted again.
		Assert.Equal(FakeTor.DefaultKey, keyFile.Read());

		// Taking it down twice is taking it down.
		await host.DisposeAsync();
		Assert.Throws<ObjectDisposedException>(host.Start);
	}

	/// <summary>A Tor that the application did not start has no control port for it, and cannot be asked for a service.</summary>
	[Fact(Timeout = 120000)]
	public async Task TorOfSomebodyElseCannotBeAskedAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		using var torStoppedCts = new CancellationTokenSource();
		var keyFile = await NewKeyFileAsync();

		await using var host = new OnionServiceHost(_ => Task.FromResult((torStoppedCts.Token, (TorControlClient?)null)), keyFile, Options);

		host.Start();

		var state = await host.WaitWhileAsync(IsOnItsWay, timeoutCts.Token);

		Assert.Equal(new OnionServiceState(OnionServiceStatus.Failed, Problem: OnionServiceHost.NoControlPortProblem), state);
		Assert.Null(keyFile.Read());
	}

	/// <summary>What Tor refuses is told in its words, and asked for again after a while.</summary>
	[Fact(Timeout = 120000)]
	public async Task RefusedServiceIsAskedForAgainAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();
		var refusals = 1;

		await using var tor = new FakeTor();
		tor.Answer = command =>
			command.StartsWith("ADD_ONION", StringComparison.Ordinal) && Interlocked.Decrement(ref refusals) >= 0
				? "550 Onion address collision\r\n"
				: null;

		await using var host = new OnionServiceHost(_ => Task.FromResult(tor.Attempt), keyFile, Options, retryDelay: TimeSpan.FromMilliseconds(50));

		host.Start();

		var failed = await host.WaitWhileAsync(IsOnItsWay, timeoutCts.Token);

		Assert.Equal(OnionServiceStatus.Failed, failed.Status);
		Assert.Contains("550 Onion address collision", failed.Problem, StringComparison.Ordinal);
		Assert.Null(failed.Host);

		var made = await host.WaitWhileAsync(state => state.Status != OnionServiceStatus.Publishing, timeoutCts.Token);

		Assert.Equal(Host, made.Host);
		Assert.Equal(2, tor.Commands.Count(command => command.StartsWith("ADD_ONION", StringComparison.Ordinal)));
	}

	/// <summary>
	/// A key file that holds no key is not replaced: a new key would be a new address. The service is not made, and
	/// the reason names the file.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task KeyFileThatHoldsNoKeyIsNotReplacedAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();

		Directory.CreateDirectory(Path.GetDirectoryName(keyFile.Path)!);
		await File.WriteAllTextAsync(keyFile.Path, "not a key", timeoutCts.Token);

		await using var tor = new FakeTor();
		await using var host = new OnionServiceHost(_ => Task.FromResult(tor.Attempt), keyFile, Options, retryDelay: TimeSpan.FromMilliseconds(50));

		host.Start();

		var failed = await host.WaitWhileAsync(IsOnItsWay, timeoutCts.Token);

		Assert.Equal(OnionServiceStatus.Failed, failed.Status);
		Assert.Contains(keyFile.Path, failed.Problem, StringComparison.Ordinal);
		Assert.Equal("not a key", await File.ReadAllTextAsync(keyFile.Path, timeoutCts.Token));
		Assert.DoesNotContain(tor.Commands, command => command.StartsWith("ADD_ONION", StringComparison.Ordinal));
	}

	/// <summary>Whoever waits for the service can stop waiting.</summary>
	[Fact(Timeout = 120000)]
	public async Task WaitingForTheServiceCanBeGivenUpAsync()
	{
		using var giveUpCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		giveUpCts.CancelAfter(TimeSpan.FromMilliseconds(100));
		var keyFile = await NewKeyFileAsync();

		// A Tor that never comes.
		await using var host = new OnionServiceHost(
			async cancellationToken =>
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				throw new InvalidOperationException();
			},
			keyFile,
			Options);

		host.Start();

		Assert.Equal(new OnionServiceState(OnionServiceStatus.WaitingForTor), host.State);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.WaitWhileAsync(IsOnItsWay, giveUpCts.Token));
	}
}

/// <summary>The file with the key of an onion service.</summary>
public class OnionServiceKeyFileTests
{
	/// <summary>The key file of a test, in a folder of its own that is empty when the test starts.</summary>
	private static async Task<OnionServiceKeyFile> NewKeyFileAsync([CallerMemberName] string testName = "") =>
		new(Path.Combine(await Common.GetEmptyWorkDirAsync(callerMemberName: testName), "OnionServiceKey"));

	[Fact]
	public async Task KeyIsWrittenAndReadBackAsync()
	{
		var keyFile = await NewKeyFileAsync();

		Assert.Null(keyFile.Read());

		keyFile.Write(FakeTor.DefaultKey);

		Assert.Equal(FakeTor.DefaultKey, keyFile.Read());
		Assert.Equal([Path.GetFileName(keyFile.Path)], Directory.GetFiles(Path.GetDirectoryName(keyFile.Path)!).Select(Path.GetFileName));
	}

	/// <summary>A line break after the key, as an editor leaves it, does not make the key another one.</summary>
	[Fact]
	public async Task LineBreakAfterTheKeyIsNoPartOfItAsync()
	{
		var keyFile = await NewKeyFileAsync();

		Directory.CreateDirectory(Path.GetDirectoryName(keyFile.Path)!);
		File.WriteAllText(keyFile.Path, FakeTor.DefaultKey + "\n");

		Assert.Equal(FakeTor.DefaultKey, keyFile.Read());
	}

	[Theory]
	[InlineData("")]
	[InlineData("not a key")]
	[InlineData("ED25519-V3:AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+Pw==\nSIGNAL SHUTDOWN")]
	public async Task FileThatHoldsNoKeyIsToldAsync(string content)
	{
		var keyFile = await NewKeyFileAsync();

		Directory.CreateDirectory(Path.GetDirectoryName(keyFile.Path)!);
		File.WriteAllText(keyFile.Path, content);

		Assert.Throws<InvalidDataException>(() => keyFile.Read());
	}

	[Fact]
	public async Task WhatIsNoKeyIsNotWrittenAsync()
	{
		var keyFile = await NewKeyFileAsync();

		Assert.Throws<ArgumentException>(() => keyFile.Write("not a key"));
		Assert.False(File.Exists(keyFile.Path));
	}
}
