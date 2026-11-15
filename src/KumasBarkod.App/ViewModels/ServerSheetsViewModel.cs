using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

/// <summary>
/// Ayarlar → "Depo sunucusunun Google Sheets kopyası" (yalnızca yönetim bilgisayarında, yetkili girişiyle).
/// Ayar sunucuda durur; sunucu Kesimler + Stok + Sevkiyatlar sayfalarını gönderir. Anahtar hiç geri okunmaz.
/// </summary>
public partial class ServerSheetsViewModel : ObservableObject
{
    private readonly DepotApiClient _api;

    public ServerSheetsViewModel(DepotApiClient api)
    {
        _api = api;
        _api.SessionChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshAsync());
    }

    /// <summary>Kart yalnızca yönetim görevli bilgisayarda görünür.</summary>
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial bool IsConfigured { get; set; }
    [ObservableProperty] public partial bool NeedsLogin { get; set; }
    [ObservableProperty] public partial bool IsReady { get; set; }
    [ObservableProperty] public partial string UserText { get; set; } = "";
    [ObservableProperty] public partial string LoginBadge { get; set; } = "";
    [ObservableProperty] public partial string LoginPin { get; set; } = "";

    [ObservableProperty] public partial string Url { get; set; } = "";
    [ObservableProperty] public partial string Key { get; set; } = "";
    [ObservableProperty] public partial bool KeySet { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool NeedsAttention { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TurnOffCommand), nameof(ReloadCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    public string KeyPlaceholder => KeySet ? "Kayıtlı (değiştirmek için yeni anahtarı yazın)" : "Apps Script'te tanımladığınız gizli anahtar";

    partial void OnKeySetChanged(bool value) => OnPropertyChanged(nameof(KeyPlaceholder));

    /// <summary>Sayfa açılınca ve oturum değişince: yetkili girişi varsa sunucudaki durumu okur.</summary>
    public async Task RefreshAsync()
    {
        IsConfigured = _api.IsConfigured;
        var user = _api.CurrentUser;
        UserText = user is null ? "" : $"{user.Name} · {UserRoles.Display(user.Role)}";
        IsReady = IsConfigured && user?.Role == UserRoles.Supervisor;
        NeedsLogin = IsConfigured && !IsReady;
        if (!IsReady || !IsVisible) return;
        try
        {
            Apply(await _api.GetSheetsStatusAsync(), keepUrl: false);
        }
        catch (SyncException ex)
        {
            StatusText = "Okunamadı: " + ex.Message;
            NeedsAttention = true;
        }
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task ReloadAsync() => RefreshAsync();

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Show(InfoBarSeverity.Warning, "Adres boş", "Kapatmak için \"Kopyayı kapat\" düğmesini kullanın.");
            return;
        }
        IsBusy = true;
        Show(InfoBarSeverity.Informational, "Deneniyor…", "Sunucu Google'a bağlanıp anahtarı ve script sürümünü deniyor (ilk açılışta 10–20 sn sürebilir).");
        try
        {
            Apply(await _api.SaveSheetsAsync(Url.Trim(), string.IsNullOrWhiteSpace(Key) ? null : Key.Trim()), keepUrl: false);
            Key = "";
            Show(InfoBarSeverity.Success, "Kaydedildi",
                "Sunucu Kesimler, Stok ve Sevkiyatlar sayfalarını göndermeye başladı. İlk gönderim birkaç dakika sürebilir.");
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Error, "Kaydedilmedi", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task TurnOffAsync()
    {
        IsBusy = true;
        try
        {
            Apply(await _api.SaveSheetsAsync("", null), keepUrl: false);
            Key = "";
            Show(InfoBarSeverity.Success, "Kopya kapatıldı", "Sunucu artık Google Sheets'e göndermiyor. Tablodaki veriler silinmedi.");
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Error, "Kapatılamadı", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            var u = await _api.LoginAsync(LoginBadge, LoginPin);
            LoginBadge = LoginPin = "";
            if (u.Role != UserRoles.Supervisor)
                Show(InfoBarSeverity.Warning, "Yetkili değil", $"{u.Name} depo personeli; bu ayar için yetkili girişi gerekir.");
        }
        catch (SyncException ex)
        {
            LoginPin = "";
            Show(InfoBarSeverity.Error, "Giriş yapılamadı", ex.Message);
        }
    }

    private void Apply(SheetsStatusResponse s, bool keepUrl)
    {
        if (!keepUrl) Url = s.Url ?? "";
        KeySet = s.KeySet;
        if (!s.Configured)
        {
            StatusText = "Kapalı (sunucu Google Sheets'e göndermiyor)";
            NeedsAttention = false;
            return;
        }
        var parts = new List<string>();
        if (s.LastError is not null)
            parts.Add($"Gönderilemiyor: {s.LastError}" + (s.LastErrorAt is null ? "" : $" ({StockViewModel.Local(s.LastErrorAt)})"));
        else parts.Add(s.LastSuccessAt is null ? "Açık, ilk gönderim bekleniyor" : $"Çalışıyor · son kontrol {StockViewModel.Local(s.LastSuccessAt)}");
        if (s.LastSnapshotAt is not null) parts.Add($"Stok/Sevkiyatlar {StockViewModel.Local(s.LastSnapshotAt)}");
        parts.Add($"bekleyen top {s.PendingCuts:N0}");
        StatusText = string.Join("  ·  ", parts);
        NeedsAttention = s.LastError is not null;
    }

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
