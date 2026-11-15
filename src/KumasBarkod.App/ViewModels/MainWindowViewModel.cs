using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.App.Printing;
using KumasBarkod.App.ServerSync;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.App.ViewModels;

/// <summary>Ana pencere: alt durum çubuğundaki bilgiler.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsRepository _settings;
    private readonly CutRepository _cuts;
    private readonly ServerConnection _server;

    public MainWindowViewModel(SettingsRepository settings, CutRepository cuts, SelectablePrinter printer, SyncWorker sync,
        ServerConnection server)
    {
        _settings = settings;
        _cuts = cuts;
        _server = server;
        PrinterName = printer.DisplayName;
        PrinterIsPhysical = printer.IsPhysical;
        printer.Changed += () =>
        {
            PrinterName = printer.DisplayName;
            PrinterIsPhysical = printer.IsPhysical;
        };

        // Kayıt/baskı/iptal: sayıları yenile ve göndericiyi dürt (kısa süre sonra gönderir).
        WeakReferenceMessenger.Default.Register<MainWindowViewModel, CutChangedMessage>(
            this, (vm, _) => { vm.Refresh(); sync.Nudge(); vm._server.Worker.Nudge(); });
        WeakReferenceMessenger.Default.Register<MainWindowViewModel, DatabaseRestoredMessage>(
            this, static (vm, _) => vm.Refresh());
        WeakReferenceMessenger.Default.Register<MainWindowViewModel, SettingsChangedMessage>(
            this, static (vm, _) => vm.Refresh());

        // Gönderici arka plan iş parçacığından bildirir; arayüze aktar.
        sync.StatusChanged += status =>
            Application.Current?.Dispatcher.BeginInvoke(() => ApplySyncStatus(status));
        ApplySyncStatus(sync.Status);

        server.Worker.StatusChanged += status =>
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyServerStatus(status));
        ApplyServerStatus(server.Worker.Status);
    }

    [ObservableProperty]
    public partial string StationCode { get; set; } = "";

    /// <summary>Kalite kontrol bilgisayarı: istasyon ve Sheets bilgisi durum çubuğunda görünür.</summary>
    [ObservableProperty]
    public partial bool IsQcRole { get; set; } = true;

    [ObservableProperty]
    public partial string RoleText { get; set; } = "";

    /// <summary>Sheets'e henüz gönderilmemiş kayıt sayısı.</summary>
    [ObservableProperty]
    public partial long PendingSyncCount { get; set; }

    [ObservableProperty]
    public partial string SyncStatusText { get; set; } = "";

    /// <summary>Bağlantı yok / hata / ayarlanmadı: durum metni uyarı rengiyle.</summary>
    [ObservableProperty]
    public partial bool SyncNeedsAttention { get; set; }

    /// <summary>Depo sunucusuna henüz gönderilmemiş kayıt sayısı.</summary>
    [ObservableProperty]
    public partial long PendingServerCount { get; set; }

    [ObservableProperty]
    public partial string ServerStatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool ServerNeedsAttention { get; set; }

    [ObservableProperty]
    public partial string PrinterName { get; set; } = "";

    /// <summary>Gösterim/test yazıcısında durum çubuğu uyarı rengiyle gösterir: etiket gerçekte basılmıyor.</summary>
    [ObservableProperty]
    public partial bool PrinterIsPhysical { get; set; }

    public void Refresh()
    {
        StationCode = _settings.GetStationCode();
        var role = _settings.GetStationRole();
        IsQcRole = role == StationRoles.Qc;
        RoleText = role switch
        {
            StationRoles.Dock => "Sevkiyat (rıhtım)",
            StationRoles.Office => "Yönetim",
            _ => "Kalite kontrol",
        };
        PendingSyncCount = _cuts.CountPendingSync();
        PendingServerCount = _server.Outbox.CountPendingCuts();
    }

    private void ApplyServerStatus(SyncStatus s)
    {
        if (s.State != SyncState.Sending) PendingServerCount = s.PendingCount;
        (ServerStatusText, ServerNeedsAttention) = DescribeServer(s);
    }

    /// <summary>
    /// Depo sunucusu durumu. Ayarlanmamış olması uyarı rengi DEĞİL: sunucu kurulana kadar QC normal çalışır,
    /// kayıtlar bekler ve sunucu ayarlanınca gider (bekleyen sayısı görünür).
    /// </summary>
    public static (string Text, bool Attention) DescribeServer(SyncStatus s) => s.State switch
    {
        SyncState.NotConfigured => ("Depo sunucusu ayarlanmadı", false),
        SyncState.Sending => ("Depoya gönderiliyor…", false),
        SyncState.Idle when s.PendingCount == 0 && s.LastSuccessUtc is { } t =>
            ($"Depo güncel · {t.ToLocalTime():HH:mm}", false),
        SyncState.Idle => ("Depo: gönderim bekliyor", false),
        SyncState.Offline => ($"Depoya ulaşılamıyor · {NextShort(s)} tekrar", true),
        SyncState.Error => ($"Depo hatası: {s.LastError}", true),
        _ => ("", false),
    };

    private static string NextShort(SyncStatus s) =>
        s.NextAttemptUtc is { } n ? $"{n.ToLocalTime():HH:mm:ss}'de" : "sonra";

    private void ApplySyncStatus(SyncStatus s)
    {
        if (s.State != SyncState.Sending) PendingSyncCount = s.PendingCount;
        (SyncStatusText, SyncNeedsAttention) = Describe(s);
    }

    public static (string Text, bool Attention) Describe(SyncStatus s) => s.State switch
    {
        SyncState.NotConfigured => ("Sheets gönderimi ayarlanmadı (Ayarlar)", true),
        SyncState.Sending => ("Sheets'e gönderiliyor…", false),
        SyncState.Idle when s.PendingCount == 0 && s.LastSuccessUtc is { } t =>
            ($"Sheets güncel · son gönderim {t.ToLocalTime():HH:mm}", false),
        SyncState.Idle => ("Sheets: gönderim bekliyor", false),
        SyncState.Offline => ($"İnternet yok · {Next(s)} tekrar denenecek", true),
        SyncState.Error => ($"Sheets hatası: {s.LastError} · {Next(s)} tekrar denenecek", true),
        _ => ("", false),
    };

    private static string Next(SyncStatus s) =>
        s.NextAttemptUtc is { } n ? $"{n.ToLocalTime():HH:mm}'de" : "sonra";
}
