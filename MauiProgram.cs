using Gomoku.Services;
using Microsoft.Extensions.Logging;

namespace Gomoku;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if ANDROID
		builder.Services.AddSingleton<IBluetoothService, Platforms.Android.Services.BluetoothService>();
#else
		builder.Services.AddSingleton<IBluetoothService, UnsupportedBluetoothService>();
#endif

		builder.Services.AddTransient<MenuPage>();
		builder.Services.AddTransient<GamePage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
