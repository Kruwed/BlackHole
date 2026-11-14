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

	/// <summary>
	/// Sets the application up for the desktop. The user interface is drawn by the graphics card (GPU), and by the
	/// processor where that is not possible.
	/// </summary>
	public static AppBuilder SetupAppBuilder(this AppBuilder appBuilder)
	{
		return appBuilder
			.UsePlatformBackend()
			.UseManagedSystemDialogs<Window>()
			.WithInterFont()
			.With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter, $Default" })
			.With(new SkiaOptions { MaxGpuResourceSizeBytes = 2560 * 1600 * 4 * 12 })
			.With(new X11PlatformOptions
			{
				RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Software },
				WmClass = WalletWasabi.Helpers.Constants.AppName
			});
	}
}
