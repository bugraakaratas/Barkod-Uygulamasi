using CommunityToolkit.Mvvm.ComponentModel;

namespace KumasBarkod.App.ViewModels;

/// <summary>Ana pencere: alt durum çubuğundaki bilgiler.</summary>
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string StationCode { get; set; } = "K1";

    /// <summary>Sheets'e henüz gönderilmemiş kayıt sayısı (8. adımda bağlanacak).</summary>
    [ObservableProperty]
    public partial int PendingSyncCount { get; set; }

    [ObservableProperty]
    public partial string SyncStatusText { get; set; } = "Gönderim henüz yapılandırılmadı";
}

public partial class CutEntryViewModel : ObservableObject
{
}

public partial class ScanViewModel : ObservableObject
{
}

public partial class HistoryViewModel : ObservableObject
{
}

public partial class SettingsViewModel : ObservableObject
{
}
