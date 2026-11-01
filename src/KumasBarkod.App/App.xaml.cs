using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.App.Printing;
using KumasBarkod.App.ViewModels;
using KumasBarkod.App.Views;
using KumasBarkod.App.Views.Pages;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Diagnostics;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace KumasBarkod.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private FileLog _log = null!;
    private DispatcherTimer? _backupTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hata günlüğü her şeyden önce: açılışta olan hatalar da yazılsın.
        _log = new FileLog(AppPaths.LogDirectory, TimeProvider.System);
        _log.Info($"Uygulama açılıyor (sürüm {typeof(App).Assembly.GetName().Version}, veri: {AppPaths.DataDirectory})");
        HookUnhandledExceptions();

        // Tarih ve sayı biçimleri Windows ayarından bağımsız olarak Türkçe (01.11.2026, 25,50).
        var turkish = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = turkish;
        CultureInfo.DefaultThreadCurrentUICulture = turkish;
        Thread.CurrentThread.CurrentCulture = turkish;
        Thread.CurrentThread.CurrentUICulture = turkish;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(turkish.IetfLanguageTag)));

        _services = ConfigureServices(_log).BuildServiceProvider();

        try
        {
            DatabaseMigrator.Migrate(_services.GetRequiredService<SqliteConnectionFactory>());
        }
        catch (Exception ex)
        {
            _log.Error("Veritabanı açılamadı", ex);
            MessageBox.Show(
                $"Veritabanı açılamadı:\n\n{ex.Message}\n\nDosya: {AppPaths.DatabasePath}\n\n" +
                $"Yedekler: {AppPaths.BackupDirectory}",
                "Kumaş Barkod", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Sheets göndericisi arka planda; kayıt/baskı/okutma ona hiç bağlı değil.
        var sync = _services.GetRequiredService<SyncWorker>();
        LogSyncStateChanges(sync);
        sync.Start();

        // Günlük yerel yedek: açılışta ve uygulama açık kaldıkça saatte bir kontrol.
        _ = RunDailyBackupAsync();
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _backupTimer.Tick += async (_, _) => await RunDailyBackupAsync();
        _backupTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backupTimer?.Stop();
        if (_services is not null)
        {
            // Kapsayıcı DisposeAsync ile kapatılmalı: SyncWorker yalnızca IAsyncDisposable
            // (eşzamanlı Dispose hata atar — hata günlüğü sayesinde bulundu).
            // Gönderici en fazla birkaç saniye beklenir; yarım kalan paket bir sonraki açılışta tekrar gider.
            try
            {
                _services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _log?.Error("Kapanışta hata", ex);
            }
        }
        _log?.Info($"Uygulama kapandı (çıkış kodu {e.ApplicationExitCode})");
        base.OnExit(e);
    }

    private async Task RunDailyBackupAsync()
    {
        var backup = _services!.GetRequiredService<BackupService>();
        try
        {
            var path = await Task.Run(backup.EnsureDailyBackup);
            if (path is not null)
            {
                _log.Info($"Günlük yedek alındı: {path}");
                WeakReferenceMessenger.Default.Send(new BackupChangedMessage());
            }
        }
        catch (Exception ex)
        {
            _log.Error("Günlük yedek alınamadı", ex);
        }
    }

    /// <summary>Yalnızca durum değişimlerini yaz (her yeniden denemeyi değil).</summary>
    private void LogSyncStateChanges(SyncWorker sync)
    {
        var last = SyncState.Idle;
        sync.StatusChanged += s =>
        {
            if (s.State == SyncState.Sending || s.State == last) return;
            switch (s.State)
            {
                case SyncState.Offline: _log.Warn($"Sheets'e gönderilemiyor (internet): {s.LastError} · bekleyen {s.PendingCount}"); break;
                case SyncState.Error: _log.Error($"Sheets hatası: {s.LastError} · bekleyen {s.PendingCount}"); break;
                case SyncState.Idle when last is SyncState.Offline or SyncState.Error: _log.Info("Sheets gönderimi düzeldi"); break;
                case SyncState.NotConfigured: _log.Info("Sheets gönderimi ayarlanmamış"); break;
            }
            last = s.State;
        };
    }

    /// <summary>
    /// Beklenmeyen hatalar: günlüğe yazılır, kullanıcıya anlaşılır mesaj gösterilir ve
    /// mümkünse uygulama açık kalır (kayıtlar zaten veritabanında, işlem tek seferde yazılıyor).
    /// </summary>
    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Beklenmeyen hata (arayüz)", args.Exception);
            MessageBox.Show(
                "Beklenmeyen bir hata oldu ve günlüğe yazıldı. Uygulama çalışmaya devam ediyor.\n\n" +
                $"{args.Exception.Message}\n\nGünlük: {_log.CurrentFile}",
                "Kumaş Barkod", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Beklenmeyen hata (arka plan görevi)", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.Error($"Ölümcül hata (uygulama kapanıyor: {args.IsTerminating})", args.ExceptionObject as Exception);
    }

    private static ServiceCollection ConfigureServices(ILog log)
    {
        var services = new ServiceCollection();

        services.AddSingleton(log);
        services.AddSingleton(new SqliteConnectionFactory(AppPaths.DatabasePath));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<CutService>();
        services.AddSingleton<CutRepository>();
        services.AddSingleton(sp => new BackupService(
            sp.GetRequiredService<SqliteConnectionFactory>(), AppPaths.BackupDirectory, sp.GetRequiredService<TimeProvider>()));

        // Yazıcı: Ayarlar'dan seçilir — Gösterim (ekran, varsayılan), Kağıt (normal yazıcı/PDF), Test (dosyaya ZPL).
        // Etiket yazıcısına ham ZPL gönderimi Adım 10'da eklenecek.
        services.AddSingleton<ILabelTemplate, ZplLabelTemplate>();
        services.AddSingleton(sp => new FileLabelPrinter(
            sp.GetRequiredService<ILabelTemplate>(), AppPaths.LabelOutputDirectory, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SelectablePrinter>();
        services.AddSingleton<ILabelPrinter>(sp => sp.GetRequiredService<SelectablePrinter>());
        // (ScanViewModel ve CutEntryViewModel MainWindow'a da enjekte edilir; tekil oldukları için aynı örnek.)
        services.AddSingleton(sp => new PrintService(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<ILabelPrinter>(),
            sp.GetRequiredService<TimeProvider>(),
            log: sp.GetRequiredService<ILog>()));

        // Google Sheets kopyası (tek yönlü) ve geri kurma
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton<ISyncTransport, AppsScriptTransport>();
        services.AddSingleton<OutboxRepository>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<SyncWorker>();
        services.AddSingleton<RestoreService>();

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
