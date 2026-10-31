using System.Windows;
using KumasBarkod.App.ViewModels;
using KumasBarkod.App.Views;
using KumasBarkod.App.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace KumasBarkod.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = ConfigureServices().BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        services.AddSingleton<CutEntryViewModel>();
        services.AddSingleton<CutEntryPage>();
        services.AddSingleton<ScanViewModel>();
        services.AddSingleton<ScanPage>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<HistoryPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();

        return services;
    }
}
