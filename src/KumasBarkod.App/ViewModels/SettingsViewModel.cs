using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.App.Printing;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Diagnostics;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

/// <summary>Ayarlar: Google Sheets bağlantısı, uzunluk üst sınırı, bilgi.</summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>Üst sınır ayarının kendi sınırı: 10.000 m (ayar hatasını da yakalasın).</summary>
    private const long MaxConfigurableCm = 10_000_00;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly SettingsRepository _settings;
    private readonly SyncService _sync;
    private readonly SyncWorker _worker;
    private readonly BackupService _backup;
    private readonly RestoreService _restore;
    private readonly ILog _log;

    private readonly SelectablePrinter _printer;

    public SettingsViewModel(SettingsRepository settings, SyncService sync, SyncWorker worker,
        BackupService backup, RestoreService restore, ILog log, SelectablePrinter printer)
    {
        _settings = settings;
        _sync = sync;
        _worker = worker;
        _backup = backup;
        _restore = restore;
        _log = log;
        _printer = printer;

        worker.StatusChanged += s => Application.Current?.Dispatcher.BeginInvoke(() => ApplyStatus(s));
        ApplyStatus(worker.Status);
        WeakReferenceMessenger.Default.Register<SettingsViewModel, BackupChangedMessage>(
            this, static (vm, _) => vm.RefreshBackupInfo());
    }

    public string DatabasePath => AppPaths.DatabasePath;
    public string LabelDirectory => AppPaths.LabelOutputDirectory;
    public string BackupDirectory => AppPaths.BackupDirectory;
    public string LogDirectory => AppPaths.LogDirectory;

    // ---------- Yazıcı ----------

    public sealed record PrinterOption(string Setting, string Title, string Description);

    public System.Collections.ObjectModel.ObservableCollection<PrinterOption> PrinterOptions { get; } = [];

    [ObservableProperty] public partial PrinterOption? SelectedPrinter { get; set; }

    private void LoadPrinterOptions()
    {
        PrinterOptions.Clear();
        PrinterOptions.Add(new(SelectablePrinter.ScreenSetting, "Gösterim (yalnızca ekran)",
            "Hiçbir şey basılmaz; etiket Kesim Girişi ekranında görünür. Sunum ve deneme için."));
        foreach (var name in PagePrinter.InstalledPrinters())
            PrinterOptions.Add(new(SelectablePrinter.PagePrefix + name, $"Kağıda bas: {name}",
                "Etiket sayfanın sol üstüne gerçek boyutta (50 × 30 mm) basılır; kesip yapıştırılabilir."));
        PrinterOptions.Add(new(SelectablePrinter.FileSetting, "Test (dosyaya ZPL yazar)",
            "Etiket yazıcısı komutu dosyaya yazılır (geliştirme için)."));

        var current = _printer.CurrentSetting;
        SelectedPrinter = PrinterOptions.FirstOrDefault(o => o.Setting == current) ?? PrinterOptions[0];
    }

    [RelayCommand]
    private void SavePrinter()
    {
        if (SelectedPrinter is null) return;
        _printer.Select(SelectedPrinter.Setting);
        _log.Info($"Yazıcı seçildi: {SelectedPrinter.Title}");
        Show(InfoBarSeverity.Success, "Yazıcı kaydedildi", SelectedPrinter.Title);
    }

    // ---------- Yedek ----------

    [ObservableProperty] public partial string LastBackupText { get; set; } = "";
    [ObservableProperty] public partial bool IsBackingUp { get; set; }

    // ---------- Geri kurma ----------

    /// <summary>Geri kurma yalnızca boş veritabanına ve Sheets ayarlıyken.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginRestoreCommand))]
    public partial bool CanRestore { get; set; }

    [ObservableProperty] public partial string RestoreHint { get; set; } = "";
    [ObservableProperty] public partial bool IsRestoreConfirmOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmRestoreCommand), nameof(BeginRestoreCommand))]
    public partial bool IsRestoring { get; set; }

    [ObservableProperty] public partial string RestoreProgress { get; set; } = "";

    [ObservableProperty] public partial string SheetsUrl { get; set; } = "";
    [ObservableProperty] public partial string SheetsKey { get; set; } = "";
    [ObservableProperty] public partial string MaxLengthText { get; set; } = "";
    [ObservableProperty] public partial string StationCode { get; set; } = "";

    [ObservableProperty] public partial string SyncStatusText { get; set; } = "";
    [ObservableProperty] public partial bool SyncNeedsAttention { get; set; }
    [ObservableProperty] public partial long PendingCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(SaveSheetsCommand))]
    public partial bool IsTesting { get; set; }

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    public void Load()
    {
        var target = _settings.GetSyncTarget();
        SheetsUrl = target?.Url ?? "";
        SheetsKey = target?.Key ?? "";
        MaxLengthText = Length.FormatCm(_settings.GetMaxLengthCm());
        StationCode = _settings.GetStationCode();
        MessageIsOpen = false;
        IsRestoreConfirmOpen = false;
        LoadPrinterOptions();
        RefreshBackupInfo();
        RefreshRestoreAvailability();
    }

    private void RefreshBackupInfo()
    {
        var list = _backup.List();
        LastBackupText = list.Count == 0
            ? "Henüz yedek yok"
            : $"Son yedek: {list[0].Day.ToString("dd.MM.yyyy", Turkish)} · {list[0].SizeBytes / 1024.0 / 1024.0:0.0} MB · toplam {list.Count} yedek (son {BackupService.KeepDays} gün saklanır)";
    }

    private void RefreshRestoreAvailability()
    {
        var empty = _restore.IsLocalDatabaseEmpty();
        var configured = _settings.GetSyncTarget() is not null;
        CanRestore = empty && configured && !IsRestoring;
        RestoreHint = (empty, configured) switch
        {
            (false, _) => "Bu bilgisayarda kayıt var; geri kurma yalnızca yeni/boş kurulumda kullanılır.",
            (true, false) => "Önce yukarıda Sheets adresini ve anahtarını girip kaydedin.",
            _ => "Veritabanı boş. Sheets'teki kayıtlarla kurulabilir.",
        };
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        IsBackingUp = true;
        try
        {
            var path = await Task.Run(_backup.CreateBackup);
            _backup.Prune();
            _log.Info($"Elle yedek alındı: {path}");
            RefreshBackupInfo();
            Show(InfoBarSeverity.Success, "Yedek alındı", path);
        }
        catch (Exception ex)
        {
            _log.Error("Elle yedek alınamadı", ex);
            Show(InfoBarSeverity.Error, "Yedek alınamadı", ex.Message);
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder() => OpenFolder(AppPaths.BackupDirectory);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.LogDirectory);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand(CanExecute = nameof(CanBeginRestore))]
    private void BeginRestore()
    {
        RefreshRestoreAvailability();
        if (CanRestore) IsRestoreConfirmOpen = true;
    }

    private bool CanBeginRestore() => CanRestore && !IsRestoring;

    [RelayCommand]
    private void CancelRestore() => IsRestoreConfirmOpen = false;

    [RelayCommand(CanExecute = nameof(NotRestoring))]
    private async Task ConfirmRestoreAsync()
    {
        var target = _settings.GetSyncTarget();
        if (target is null) return;

        IsRestoring = true;
        IsRestoreConfirmOpen = false;
        RestoreProgress = "Başlıyor…";
        _log.Info("Sheets'ten geri kurma başladı");
        try
        {
            var progress = new Progress<string>(p => RestoreProgress = p);
            var result = await Task.Run(() => _restore.RestoreAsync(target, progress));
            var counters = string.Join(", ", result.Counters.Select(c => $"{c.Key} → sonraki {CutIds.Format(c.Key, Math.Min(c.Value + 1, CutIds.MaxSequence))}"));
            _log.Info($"Geri kurma tamamlandı: {result.Cuts} kayıt, {result.Voided} iptal, sayaç {counters}");
            Show(InfoBarSeverity.Success, "Geri kurma tamamlandı",
                $"{result.Cuts:N0} kayıt ({result.Voided:N0} iptal) kuruldu. Yeni ID'ler: {counters}.");
            WeakReferenceMessenger.Default.Send(new DatabaseRestoredMessage());
        }
        catch (Exception ex) when (ex is SyncException or CutValidationException)
        {
            _log.Error("Geri kurma yapılamadı", ex);
            Show(InfoBarSeverity.Error, "Geri kurma yapılamadı", ex.Message);
        }
        finally
        {
            IsRestoring = false;
            RestoreProgress = "";
            RefreshRestoreAvailability();
        }
    }

    private bool NotRestoring() => !IsRestoring;

    /// <summary>
    /// İstasyon kodu: yeni istasyon kurulurken verilir (K1, K2…). Değişince yeni kayıtlar yeni kodla
    /// ve o kodun kendi sayacıyla başlar; eski kayıtlar ve ID'leri aynen kalır.
    /// </summary>
    [RelayCommand]
    private void SaveStationCode()
    {
        var code = StationCode.Trim().ToUpperInvariant();
        var old = _settings.GetStationCode();
        if (code == old)
        {
            Show(InfoBarSeverity.Informational, "Değişiklik yok", $"İstasyon kodu zaten {old}.");
            return;
        }
        try
        {
            _settings.SetStationCode(code);
        }
        catch (CutValidationException ex)
        {
            Show(InfoBarSeverity.Error, "İstasyon kodu kaydedilmedi", ex.Message);
            return;
        }
        StationCode = code;
        _log.Info($"İstasyon kodu değişti: {old} → {code}");
        Show(InfoBarSeverity.Success, "İstasyon kodu kaydedildi",
            $"Yeni kayıtlar {code}-… ile başlar. Eski {old} kayıtları aynen kalır.");
        WeakReferenceMessenger.Default.Send(new SettingsChangedMessage());
    }

    public string VersionText { get; } =
        $"Sürüm {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}";

    [RelayCommand(CanExecute = nameof(NotTesting))]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(SheetsUrl) || string.IsNullOrWhiteSpace(SheetsKey))
        {
            Show(InfoBarSeverity.Error, "Eksik bilgi", "Web uygulaması adresini ve anahtarı girin.");
            return;
        }

        IsTesting = true;
        try
        {
            await _sync.TestConnectionAsync(new SyncTarget(SheetsUrl.Trim(), SheetsKey.Trim()));
            Show(InfoBarSeverity.Success, "Bağlantı başarılı", "Anahtar doğru, tabloya yazılabiliyor. Kaydet'e basarak gönderimi başlatın.");
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Error, "Bağlantı kurulamadı", ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotTesting))]
    private void SaveSheets()
    {
        try
        {
            _settings.SetSyncTarget(SheetsUrl, SheetsKey);
        }
        catch (CutValidationException ex)
        {
            Show(InfoBarSeverity.Error, "Kaydedilmedi", ex.Message);
            return;
        }

        RefreshRestoreAvailability();
        if (_settings.GetSyncTarget() is null)
        {
            Show(InfoBarSeverity.Informational, "Gönderim kapatıldı", "Adres veya anahtar boş. Kayıtlar yerelde birikir, ayar yapılınca gönderilir.");
        }
        else
        {
            Show(InfoBarSeverity.Success, "Kaydedildi", "Bekleyen kayıtlar şimdi gönderiliyor.");
            _worker.SendNow();
        }
    }

    [RelayCommand]
    private void SendNow()
    {
        _worker.SendNow();
        Show(InfoBarSeverity.Informational, "Gönderim başlatıldı", "Durum birkaç saniye içinde güncellenir.");
    }

    [RelayCommand]
    private void SaveMaxLength()
    {
        if (!Length.TryParseCentimeters(MaxLengthText, MaxConfigurableCm, out var cm, out var error))
        {
            Show(InfoBarSeverity.Error, "Üst sınır kaydedilmedi", error);
            return;
        }
        _settings.SetMaxLengthCm(cm);
        MaxLengthText = Length.FormatCm(cm);
        Show(InfoBarSeverity.Success, "Üst sınır kaydedildi", $"Bundan sonra en fazla {Length.FormatCm(cm)} cm girilebilir.");
    }

    private bool NotTesting() => !IsTesting;

    private void ApplyStatus(SyncStatus s)
    {
        (SyncStatusText, SyncNeedsAttention) = MainWindowViewModel.Describe(s);
        PendingCount = s.PendingCount;
    }

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
