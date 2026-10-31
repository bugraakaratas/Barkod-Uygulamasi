using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

/// <summary>Ayarlar: Google Sheets bağlantısı, metre üst sınırı, bilgi.</summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>Üst sınır ayarının kendi sınırı: 10.000 m (ayar hatasını da yakalasın).</summary>
    private const long MaxConfigurableCm = 10_000_00;

    private readonly SettingsRepository _settings;
    private readonly SyncService _sync;
    private readonly SyncWorker _worker;

    public SettingsViewModel(SettingsRepository settings, SyncService sync, SyncWorker worker)
    {
        _settings = settings;
        _sync = sync;
        _worker = worker;

        worker.StatusChanged += s => Application.Current?.Dispatcher.BeginInvoke(() => ApplyStatus(s));
        ApplyStatus(worker.Status);
    }

    public string DatabasePath => AppPaths.DatabasePath;
    public string LabelDirectory => AppPaths.LabelOutputDirectory;

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
        MaxLengthText = Length.Format(_settings.GetMaxLengthCm());
        StationCode = _settings.GetStationCode();
        MessageIsOpen = false;
    }

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
        if (!Length.TryParseMeters(MaxLengthText, MaxConfigurableCm, out var cm, out var error))
        {
            Show(InfoBarSeverity.Error, "Üst sınır kaydedilmedi", error);
            return;
        }
        _settings.SetMaxLengthCm(cm);
        MaxLengthText = Length.Format(cm);
        Show(InfoBarSeverity.Success, "Üst sınır kaydedildi", $"Bundan sonra en fazla {Length.Format(cm)} m girilebilir.");
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
