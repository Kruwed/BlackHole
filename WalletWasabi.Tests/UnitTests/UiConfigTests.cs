using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

/// <summary>
/// Tests for <see cref="UiConfig"/>.
/// </summary>
public class UiConfigTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("{invalid json")]
	public async Task LoadFileCompletesDefaultFileWriteBeforeReturningAsync(string? existingContent)
	{
		var workDir = await Common.GetEmptyWorkDirAsync();
		var filePath = Path.Combine(workDir, "config.json");

		if (existingContent is not null)
		{
			File.WriteAllText(filePath, existingContent);
		}

		var config = UiConfig.LoadFile(filePath);

		using (var persisted = JsonDocument.Parse(File.ReadAllText(filePath)))
		{
			Assert.True(persisted.RootElement.GetProperty(nameof(UiConfig.Oobe)).GetBoolean());
			Assert.False(persisted.RootElement.GetProperty(nameof(UiConfig.PrivacyMode)).GetBoolean());
		}

		// A subsequent save must not overlap the initial write or be overwritten by it.
		config.Oobe = false;
		config.ToFile();
		using var updated = JsonDocument.Parse(File.ReadAllText(filePath));
		Assert.False(updated.RootElement.GetProperty(nameof(UiConfig.Oobe)).GetBoolean());
	}

	/// <summary>
	/// The file of an earlier version has settings that do not exist any more. It is read all the same, what it says
	/// about the other settings is kept, and the settings that are gone are left out when it is saved again.
	/// </summary>
	[Fact]
	public async Task ReadsTheFileOfAnEarlierVersionAsync()
	{
		var workDir = await Common.GetEmptyWorkDirAsync();
		var filePath = Path.Combine(workDir, "UiConfig.json");

		File.WriteAllText(
			filePath,
			"""
			{
			  "Oobe": false,
			  "LastVersionHighlightsDisplayed": "2.8.0",
			  "WindowState": "Maximized",
			  "FeeTarget": 6,
			  "Autocopy": true,
			  "AutoPaste": true,
			  "IsCustomChangeAddress": false,
			  "PrivacyMode": true,
			  "DarkModeEnabled": false,
			  "RunOnSystemStartup": true,
			  "HideOnClose": true,
			  "WindowWidth": 1280.0,
			  "WindowHeight": 800.0
			}
			""");

		var config = UiConfig.LoadFile(filePath);

		// A file that cannot be read is replaced by the default settings, which these are not.
		Assert.False(config.Oobe);
		Assert.Equal(new Version(2, 8, 0), config.LastVersionHighlightsDisplayed);
		Assert.Equal("Maximized", config.WindowState);
		Assert.Equal(6, config.FeeTarget);
		Assert.True(config.PrivacyMode);
		Assert.Equal(1280.0, config.WindowWidth);
		Assert.Equal(800.0, config.WindowHeight);

		config.ToFile();

		using var saved = JsonDocument.Parse(File.ReadAllText(filePath));
		Assert.False(saved.RootElement.GetProperty(nameof(UiConfig.Oobe)).GetBoolean());
		Assert.True(saved.RootElement.GetProperty(nameof(UiConfig.PrivacyMode)).GetBoolean());

		foreach (var removedSetting in RemovedSettings)
		{
			Assert.False(saved.RootElement.TryGetProperty(removedSetting, out _), $"'{removedSetting}' is still saved.");
		}
	}

	/// <summary>Settings of earlier versions that the application does not have any more.</summary>
	private static readonly string[] RemovedSettings = ["DarkModeEnabled", "RunOnSystemStartup", "HideOnClose"];
}
