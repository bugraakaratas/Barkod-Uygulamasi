using System.Windows;
using KumasBarkod.App.ViewModels;
using KumasBarkod.App.Views;
using KumasBarkod.App.Views.Pages;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace KumasBarkod.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = ConfigureServices().BuildServiceProvider();

        try
        {
            DatabaseMigrator.Migrate(_services.GetRequiredService<SqliteConnectionFactory>());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Veritabanı açılamadı:\n\n{ex.Message}\n\nDosya: {AppPaths.DatabasePath}",
                "Kumaş Barkod", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

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

        services.AddSingleton(new SqliteConnectionFactory(AppPaths.DatabasePath));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<CutService>();
        services.AddSingleton<CutRepository>();

        // Yazıcı: gerçek yazıcı 10. adımda eklenecek; o zamana kadar etiketler dosyaya yazılır.
        services.AddSingleton<ILabelTemplate, ZplLabelTemplate>();
        // (ScanViewModel ve CutEntryViewModel MainWindow'a da enjekte edilir; tekil oldukları için aynı örnek.)
        services.AddSingleton<ILabelPrinter>(sp => new FileLabelPrinter(
            sp.GetRequiredService<ILabelTemplate>(), AppPaths.LabelOutputDirectory, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PrintService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<ILabelPrinter>(),
            sp.GetRequiredService<TimeProvider>()));

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
