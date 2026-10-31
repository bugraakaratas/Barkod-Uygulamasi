using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Data;

namespace KumasBarkod.App.ViewModels;

/// <summary>Ana pencere: alt durum çubuğundaki bilgiler.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsRepository _settings;
    private readonly CutRepository _cuts;

    public MainWindowViewModel(SettingsRepository settings, CutRepository cuts)
    {
        _settings = settings;
        _cuts = cuts;

        WeakReferenceMessenger.Default.Register<MainWindowViewModel, CutCreatedMessage>(
            this, static (vm, _) => vm.Refresh());
    }

    [ObservableProperty]
    public partial string StationCode { get; set; } = "";

    /// <summary>Sheets'e henüz gönderilmemiş kayıt sayısı.</summary>
    [ObservableProperty]
    public partial long PendingSyncCount { get; set; }

    [ObservableProperty]
    public partial string SyncStatusText { get; set; } = "Gönderim henüz yapılandırılmadı";

    public void Refresh()
    {
        StationCode = _settings.GetStationCode();
        PendingSyncCount = _cuts.CountPendingSync();
    }
}
