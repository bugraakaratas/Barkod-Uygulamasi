using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public sealed record QuarantineView(long ConflictId, string CutId, string Existing, string Incoming, string ReceivedAt);
public sealed record VoidedAfterShipView(string CutId, string Lot, string Meters, string Shipment, string Reason);
public sealed record ShipmentLineView(long ShipmentId, string No, string Lots, string Counts, string Who, string When);

/// <summary>
/// İstisnalar (yetkili): karantina (aynı barkodlu iki top) çözümü, sevk sonrası QC iptali, uzun süredir açık sevkiyatlar,
/// son onaylananlar ve onayı geri alma. Her işlem sunucuda yetkili adıyla kaydedilir; hiçbir kayıt silinmez.
/// </summary>
public partial class ExceptionsViewModel : ObservableObject
{
    private readonly DepotApiClient _api;
    private readonly DataVersionPoller _poller;
    private string? _reverseRequestId;

    public ExceptionsViewModel(DepotApiClient api)
    {
        _api = api;
        _poller = new DataVersionPoller(api);
        _poller.Changed += async () => { if (IsReady) await LoadAsync(); };
        _api.SessionChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await RefreshStateAsync());
    }

    public ObservableCollection<QuarantineView> Quarantined { get; } = [];
    public ObservableCollection<VoidedAfterShipView> VoidedAfterShip { get; } = [];
    public ObservableCollection<ShipmentLineView> Stale { get; } = [];
    public ObservableCollection<ShipmentLineView> RecentConfirmed { get; } = [];

    [ObservableProperty] public partial bool IsConfigured { get; set; } = true;
    [ObservableProperty] public partial bool NeedsLogin { get; set; }
    [ObservableProperty] public partial bool IsReady { get; set; }
    [ObservableProperty] public partial string UserText { get; set; } = "";
    [ObservableProperty] public partial string LoginBadge { get; set; } = "";
    [ObservableProperty] public partial string LoginPin { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ResolveCommand))] public partial QuarantineView? SelectedQuarantine { get; set; }
    [ObservableProperty] public partial string ResolveNote { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(BeginReverseCommand))] public partial ShipmentLineView? SelectedConfirmed { get; set; }
    [ObservableProperty] public partial string ReverseReason { get; set; } = "";
    [ObservableProperty] public partial bool IsReverseOpen { get; set; }
    [ObservableProperty] public partial string ReverseText { get; set; } = "";

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    public async Task ActivateAsync()
    {
        await RefreshStateAsync();
        if (IsConfigured) _poller.Start();
    }

    public void Deactivate() => _poller.Stop();

    private async Task RefreshStateAsync()
    {
        IsConfigured = _api.IsConfigured;
        var user = _api.CurrentUser;
        UserText = user is null ? "" : $"{user.Name} · {UserRoles.Display(user.Role)}";
        IsReady = IsConfigured && user?.Role == UserRoles.Supervisor;
        NeedsLogin = IsConfigured && !IsReady;
        if (IsReady) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            Apply(await _api.GetExceptionsAsync());
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Warning, "İstisnalar alınamadı", ex.Message);
        }
    }

    private void Apply(ExceptionsResponse e)
    {
        var selQ = SelectedQuarantine?.ConflictId;
        var selC = SelectedConfirmed?.ShipmentId;
        Quarantined.Clear();
        foreach (var q in e.Quarantined)
            Quarantined.Add(new QuarantineView(q.ConflictId, q.CutId,
                $"İlk kayıt: {q.ExistingLot} · {Length.FormatGrouped(q.ExistingLengthCm)} m · {StockViewModel.Local(q.ExistingCreatedAt)} · {q.ExistingStation}",
                $"Sonradan gelen: {q.IncomingLot} · {Length.FormatGrouped(q.IncomingLengthCm)} m · {StockViewModel.Local(q.IncomingCreatedAt)} · {q.IncomingStation}",
                StockViewModel.Local(q.ReceivedAt)));
        VoidedAfterShip.Clear();
        foreach (var v in e.VoidedAfterShip)
            VoidedAfterShip.Add(new VoidedAfterShipView(v.CutId, v.LotName, Length.FormatGrouped(v.LengthCm) + " m",
                $"{v.ShipmentNo} · {StockViewModel.Local(v.ShippedAt)}", v.VoidReason ?? ""));
        Stale.Clear();
        foreach (var s in e.StaleShipments) Stale.Add(Line(s, $"açan {s.OpenedBy}", $"son işlem {StockViewModel.Local(s.LastActivityAt)}"));
        RecentConfirmed.Clear();
        foreach (var s in e.RecentConfirmed) RecentConfirmed.Add(Line(s, $"onaylayan {s.ClosedBy}", StockViewModel.Local(s.ClosedAt!)));
        SelectedQuarantine = Quarantined.FirstOrDefault(q => q.ConflictId == selQ);
        SelectedConfirmed = RecentConfirmed.FirstOrDefault(c => c.ShipmentId == selC);

        var parts = new List<string>();
        if (e.Quarantined.Count > 0) parts.Add($"{e.Quarantined.Count} karantina");
        if (e.VoidedAfterShip.Count > 0) parts.Add($"{e.VoidedAfterShip.Count} sevk sonrası iptal");
        if (e.StaleShipments.Count > 0) parts.Add($"{e.StaleShipments.Count} uzun süredir açık sevkiyat");
        if (e.UnmatchedLotCount > 0) parts.Add($"{e.UnmatchedLotCount} eşleşmemiş lot yazımı (Lotlar ekranı)");
        Summary = parts.Count == 0 ? "Bekleyen istisna yok." : "Bekleyen: " + string.Join(" · ", parts);
    }

    private static ShipmentLineView Line(ShipmentSummary s, string who, string when) =>
        new(s.ShipmentId, s.ShipmentNo, s.LotNames, $"{s.Count} top · {Length.FormatGrouped(s.LengthCm)} m", who, when);

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            var u = await _api.LoginAsync(LoginBadge, LoginPin);
            LoginBadge = LoginPin = "";
            if (u.Role != UserRoles.Supervisor)
                Show(InfoBarSeverity.Warning, "Yetkili değil", $"{u.Name} depo personeli; istisnalar için yetkili girişi gerekir.");
        }
        catch (SyncException ex)
        {
            LoginPin = "";
            Show(InfoBarSeverity.Error, "Giriş yapılamadı", ex.Message);
        }
    }

    [RelayCommand]
    private Task LogoutAsync() => _api.LogoutAsync();

    private bool CanResolve() => SelectedQuarantine is not null;

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ResolveAsync()
    {
        try
        {
            var cut = SelectedQuarantine!.CutId;
            Apply(await _api.ResolveConflictAsync(SelectedQuarantine.ConflictId, ResolveNote));
            ResolveNote = "";
            Show(InfoBarSeverity.Success, "Karantina çözüldü", $"{cut}: sonradan gelen kayıt geçersiz sayıldı; top önceki durumuna döndü.");
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Error, "Çözülemedi", ex.Message);
        }
    }

    private bool CanBeginReverse() => SelectedConfirmed is not null;

    [RelayCommand(CanExecute = nameof(CanBeginReverse))]
    private void BeginReverse()
    {
        if (ReverseReason.Trim().Length < 3)
        {
            Show(InfoBarSeverity.Error, "Sebep gerekli", "Onayı neden geri aldığınızı yazın (en az 3 karakter).");
            return;
        }
        _reverseRequestId = Guid.NewGuid().ToString("N");
        ReverseText = $"{SelectedConfirmed!.No} ({SelectedConfirmed.Counts}) onayı geri alınsın mı? Toplar stoğa döner. Sebep: {ReverseReason.Trim()}";
        IsReverseOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmReverseAsync()
    {
        if (!IsReverseOpen || SelectedConfirmed is null) return;
        var counts = SelectedConfirmed.Counts;   // geri almadan sonra aktif kalem kalmaz: sayı önceden alınır
        try
        {
            var d = await _api.ReverseShipmentAsync(SelectedConfirmed.ShipmentId, ReverseReason.Trim(), _reverseRequestId!);
            IsReverseOpen = false;
            ReverseReason = "";
            await LoadAsync();
            Show(InfoBarSeverity.Success, "Onay geri alındı", $"{d.ShipmentNo} ({counts}): toplar stoğa döndü. Kayıt silinmedi.");
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Error, "Geri alınamadı", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelReverse() => IsReverseOpen = false;

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
