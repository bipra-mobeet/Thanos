using Microsoft.UI.Xaml;
using Serilog;
using Thanos.Core;
using Thanos.Infrastructure;

namespace Thanos.App;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Logging.Configure();
        UnhandledException += (_, args) => Log.Error(args.Exception, "Unhandled UI exception");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await AppServices.InitializeAsync();
            var settings = await AppServices.Settings.LoadAsync(CancellationToken.None);
            MainWindow = new MainWindow();
            MainWindow.ApplyTheme(settings.Theme);
            MainWindow.Activate();
            Log.Information("App started. Version={Version}", typeof(App).Assembly.GetName().Version);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed");
            throw;
        }
    }
}
