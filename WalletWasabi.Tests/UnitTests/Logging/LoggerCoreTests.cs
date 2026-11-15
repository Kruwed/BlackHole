using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Logging;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Logging;

public class LoggerCoreTests
{
	[Fact]
	public async Task TellsWhichLevelsAreLoggedAsync()
	{
		var workDir = await Common.GetEmptyWorkDirAsync();
		var filePath = Path.Combine(workDir, "Logs.txt");
		var logger = new LoggerCore(filePath, LogLevel.Info, [LogMode.File]);

		Assert.False(logger.IsEnabled(LogLevel.Trace));
		Assert.False(logger.IsEnabled(LogLevel.Debug));
		Assert.True(logger.IsEnabled(LogLevel.Info));
		Assert.True(logger.IsEnabled(LogLevel.Critical));

		// What is not enabled does not reach the file.
		logger.Log(LogLevel.Debug, "a detail");
		logger.Log(LogLevel.Info, "a message");

		var text = await File.ReadAllTextAsync(filePath);
		Assert.DoesNotContain("a detail", text);
		Assert.Contains("a message", text);

		// A logger that writes nowhere logs nothing.
		Assert.False(new LoggerCore(filePath, LogLevel.Trace, []).IsEnabled(LogLevel.Critical));
	}
}
