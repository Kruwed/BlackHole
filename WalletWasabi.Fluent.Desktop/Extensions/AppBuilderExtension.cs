using Avalonia;
using Avalonia.Controls;
using Avalonia.Dialogs;
using Avalonia.Media;

namespace WalletWasabi.Fluent.Desktop.Extensions;

public static class AppBuilderExtension
{
	/// <summary>Selects the X11 windowing backend and the Skia renderer.</summary>
	public static AppBuilder UsePlatformBackend(this AppBuilder appBuilder) =>
		appBuilder.UseX11().UseSkia();

	public static AppBuilder SetupAppBuilder(this AppBuilder appBuilder, bool enableGpu)
	{
		return appBuilder
			.UsePlatformBackend()
			.UseManagedSystemDialogs<Window>()
			.WithInterFont()
			.With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter, $Default" })
			.With(new SkiaOptions { MaxGpuResourceSizeBytes = 2560 * 1600 * 4 * 12 })
			.With(new X11PlatformOptions
			{
				RenderingMode = enableGpu
					? new[] { X11RenderingMode.Glx, X11RenderingMode.Software }
					: new[] { X11RenderingMode.Software },
				WmClass = "Wasabi Wallet"
			});
	}
}
