using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Dialogs;
using Avalonia.Media;

namespace WalletWasabi.Fluent.Desktop.Extensions;

public static class AppBuilderExtension
{
	/// <summary>Selects the windowing backend of the current operating system and the Skia renderer.</summary>
	public static AppBuilder UsePlatformBackend(this AppBuilder appBuilder) =>
		RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
			? appBuilder.UseX11().UseSkia()
			: appBuilder.UseAvaloniaNative().UseSkia();

	public static AppBuilder SetupAppBuilder(this AppBuilder appBuilder, bool enableGpu)
	{
		appBuilder.UsePlatformBackend();

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			appBuilder.UseManagedSystemDialogs<Window>();
		}

		return appBuilder
			.WithInterFont()
			.With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter, $Default" })
			.With(new SkiaOptions { MaxGpuResourceSizeBytes = 2560 * 1600 * 4 * 12 })
			.With(new X11PlatformOptions
			{
				RenderingMode = enableGpu
					? new[] { X11RenderingMode.Glx, X11RenderingMode.Software }
					: new[] { X11RenderingMode.Software },
				WmClass = "Wasabi Wallet"
			})
			.With(new AvaloniaNativePlatformOptions
			{
				RenderingMode = enableGpu
					? new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }
					: new[] { AvaloniaNativeRenderingMode.Software },
			})
			.With(new MacOSPlatformOptions { ShowInDock = true });
	}
}
