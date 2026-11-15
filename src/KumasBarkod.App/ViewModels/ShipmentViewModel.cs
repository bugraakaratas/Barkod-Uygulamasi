using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.App.Shipments;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.App.ViewModels;

public enum ShipmentScreen { NotConfigured, Login, List, Scanning }

/// <summary>Sonuç bandının durumu (renk): idle gri, success yeşil, warning sarı, error kırmızı, offline koyu kırmızı, remove turuncu.</summary>
public static class FeedbackKinds
{
    public const string Idle = "idle";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Offline = "offline";
    public const string Remove = "remove";
}

public sealed record OpenShipmentView(long ShipmentId, string No, string Lots, string Counts, string OpenedBy, string LastActivity, string Age, int Staleness);
public sealed record LotChoiceView(long LotId, string Name, string Stock);
public sealed record ShipmentItemView(string CutId, string Lot, string Meters, string Time, string User);
public sealed record ScanLogView(string Time, string Text, string Code, bool Accepted, string User);

/// <summary>
/// Sevkiyat (rıhtım). Akış: giriş (kart + PIN) → açık sevkiyata devam ya da lot seçip yeni sevkiyat → okut → onayla/iptal.
/// <para>Kurallar sunucuda; bu ekran yalnızca gösterir. Okutmalar SIRAYLA gönderilir; ağ koparsa okutma kuyrukta bekler,
/// aynı kimlikle tekrar denenir (sunucu ikinci kez işlemez) ve ekran kırmızı "bağlantı yok" uyarısı verir.</para>
/// <para>Okuyucunun art arda iki kez gönderdiği aynı barkod 1,5 sn içinde yok sayılır (sunucu da zaten korur).</para>
/// </summary>
public partial class ShipmentViewModel : ObservableObject
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan IdleLogout = TimeSpan.FromMinutes(30);
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly DepotApiClient _api;
    private readonly DataVersionPoller _poller;
    private readonly DispatcherTimer _idleTimer;
    private readonly Queue<(long ShipmentId, ScanRequest Request, bool Remove)> _queue = new();
    private bool _pumping;
    private string? _lastRaw;
    private DateTime _lastRawAt;
    private DateTime _lastActivity = DateTime.Now;
    private long _version;
    private string? _confirmRequestId;
    private string? _cancelRequestId;

    public ShipmentViewModel(DepotApiClient api)
    {
        _api = api;
        _poller = new DataVersionPoller(api);
        _poller.Changed += RefreshAsync;
        _poller.ConnectionChanged += e => { if (_queue.Count == 0) IsServerUnreachable = e is not null; };
        _api.SessionChanged += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(OnSessionChanged);
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += async (_, _) =>
        {
            if (_api.CurrentUser is not null && _queue.Count == 0 && DateTime.Now - _lastActivity > IdleLogout)
                await _api.LogoutAsync();
        };
        foreach (var r in CancelReasons.All) CancelReasonList.Add(r);
    }

    /// <summary>Görünüm odağı yönetir (giriş kutusu, okutma kutusu).</summary>
    public event Action<ShipmentScreen>? FocusRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoginScreen), nameof(IsListScreen), nameof(IsScanScreen), nameof(IsNotConfigured))]
    public partial ShipmentScreen Screen { get; set; } = ShipmentScreen.Login;

    public bool IsNotConfigured => Screen == ShipmentScreen.NotConfigured;
    public bool IsLoginScreen => Screen == ShipmentScreen.Login;
    public bool IsListScreen => Screen == ShipmentScreen.List;
    public bool IsScanScreen => Screen == ShipmentScreen.Scanning;

    [ObservableProperty] public partial string UserText { get; set; } = "";
    [ObservableProperty] public partial bool IsServerUnreachable { get; set; }

    // ---------- Giriş ----------

    [ObservableProperty] public partial string LoginBadge { get; set; } = "";
    [ObservableProperty] public partial string LoginPin { get; set; } = "";
    [ObservableProperty] public partial string LoginError { get; set; } = "";
    [ObservableProperty] public partial bool BootstrapNeeded { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoginCommand))] public partial bool IsBusy { get; set; }

    public async Task ActivateAsync()
    {
        _lastActivity = DateTime.Now;
        if (!_api.IsConfigured) { Screen = ShipmentScreen.NotConfigured; return; }
        _idleTimer.Start();
        _poller.Start();
        if (_api.CurrentUser is null)
        {
            Screen = ShipmentScreen.Login;
            try { BootstrapNeeded = (await _api.BootstrapNeededAsync()).Needed; } catch (SyncException) { }
            FocusRequested?.Invoke(ShipmentScreen.Login);
        }
        else if (Screen is ShipmentScreen.Login or ShipmentScreen.NotConfigured)
        {
            await ShowListAsync();
        }
        else
        {
            FocusRequested?.Invoke(Screen);
        }
    }

    public void Deactivate() => _poller.Stop();

    private bool CanLogin() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        IsBusy = true;
        LoginError = "";
        try
        {
            await _api.LoginAsync(LoginBadge, LoginPin);
            LoginPin = "";
            LoginBadge = "";
            _lastActivity = DateTime.Now;
            await ShowListAsync();
        }
        catch (SyncException ex)
        {
            LoginError = ex.Message;
            LoginPin = "";
            FocusRequested?.Invoke(ShipmentScreen.Login);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (_queue.Count > 0)
        {
            ShowFeedback(FeedbackKinds.Warning, "Gönderilmemiş okutma var", "Bağlantı gelip okutmalar gidince çıkış yapın.");
            return;
        }
        await _api.LogoutAsync();
    }

    private void OnSessionChanged()
    {
        var user = _api.CurrentUser;
        UserText = user is null ? "" : $"{user.Name} · {UserRoles.Display(user.Role)}";
        if (user is null && _queue.Count == 0)
        {
            Screen = _api.IsConfigured ? ShipmentScreen.Login : ShipmentScreen.NotConfigured;
            CurrentId = null;
            FocusRequested?.Invoke(Screen);
        }
    }

    // ---------- Liste / yeni sevkiyat ----------

    public ObservableCollection<OpenShipmentView> OpenShipments { get; } = [];
    public ObservableCollection<LotChoiceView> LotChoices { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ResumeCommand))] public partial OpenShipmentView? SelectedShipment { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] public partial LotChoiceView? SelectedLot { get; set; }
    [ObservableProperty] public partial string ListMessage { get; set; } = "";

    private async Task ShowListAsync(string? message = null)
    {
        CurrentId = null;
        IsRemoveMode = false;
        CloseConfirmPanel();
        CloseCancelPanel();
        Screen = ShipmentScreen.List;
        ListMessage = message ?? "";
        await LoadListAsync();
        FocusRequested?.Invoke(ShipmentScreen.List);
    }

    private async Task LoadListAsync()
    {
        try
        {
            var open = await _api.GetShipmentsAsync(open: true);
            var lots = await _api.GetLotsAsync();
            var selectedShipment = SelectedShipment?.ShipmentId;
            var selectedLot = SelectedLot?.LotId;
            OpenShipments.Clear();
            foreach (var s in open)
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.Parse(s.LastActivityAt, CultureInfo.InvariantCulture);
                OpenShipments.Add(new OpenShipmentView(s.ShipmentId, s.ShipmentNo, s.LotNames,
                    $"{s.Count} top · {Length.FormatGrouped(s.LengthCm)} m", s.OpenedBy, StockViewModel.Local(s.LastActivityAt),
                    age.TotalHours >= 24 ? "1 günden uzun süredir işlem yok — yetkili kontrol etmeli"
                        : age.TotalHours >= 2 ? $"{(int)age.TotalHours} saattir işlem yok" : "",
                    age.TotalHours >= 24 ? 2 : age.TotalHours >= 2 ? 1 : 0));
            }
            LotChoices.Clear();
            foreach (var l in lots.Where(l => l.Active).OrderByDescending(l => l.InStockCount > 0).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
                LotChoices.Add(new LotChoiceView(l.LotId, l.Name, $"{l.InStockCount} top · {Length.FormatGrouped(l.InStockCm)} m stokta"));
            SelectedShipment = OpenShipments.FirstOrDefault(s => s.ShipmentId == selectedShipment);
            SelectedLot = LotChoices.FirstOrDefault(l => l.LotId == selectedLot);
        }
        catch (SyncException ex)
        {
            ListMessage = ex.Message;
        }
    }

    private bool CanStart() => SelectedLot is not null;
    private bool CanResume() => SelectedShipment is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            var d = await _api.OpenShipmentAsync([SelectedLot!.LotId]);
            EnterShipment(d);
        }
        catch (SyncException ex)
        {
            ListMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        try
        {
            EnterShipment(await _api.GetShipmentAsync(SelectedShipment!.ShipmentId));
        }
        catch (SyncException ex)
        {
            ListMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task BackToListAsync() => ShowListAsync();

    // ---------- Okutma ekranı ----------

    [ObservableProperty] public partial long? CurrentId { get; set; }
    [ObservableProperty] public partial string ShipmentNo { get; set; } = "";
    [ObservableProperty] public partial string LotTitle { get; set; } = "";
    [ObservableProperty] public partial string ShipmentInfo { get; set; } = "";
    [ObservableProperty] public partial string Warnings { get; set; } = "";

    [ObservableProperty] public partial string FeedbackKind { get; set; } = FeedbackKinds.Idle;
    [ObservableProperty] public partial string FeedbackTitle { get; set; } = "";
    [ObservableProperty] public partial string FeedbackDetail { get; set; } = "";

    [ObservableProperty] public partial string CountText { get; set; } = "0";
    [ObservableProperty] public partial string MetersText { get; set; } = "0,00";
    [ObservableProperty] public partial string RejectedText { get; set; } = "0";
    [ObservableProperty] public partial string RemainingText { get; set; } = "0";
    [ObservableProperty] public partial string RemainingMetersText { get; set; } = "";
    [ObservableProperty] public partial int PendingCount { get; set; }

    [ObservableProperty] public partial string ScanText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanHint))]
    public partial bool IsRemoveMode { get; set; }

    public string ScanHint => IsRemoveMode
        ? "ÇIKARMA MODU: okutulan top sevkiyattan çıkarılır (F7 ile kapat)"
        : "Barkodu okutun ya da yazıp Enter'a basın";

    public ObservableCollection<ShipmentItemView> Items { get; } = [];
    public ObservableCollection<ScanLogView> ScanLog { get; } = [];

    private void EnterShipment(ShipmentDetail d)
    {
        CurrentId = d.ShipmentId;
        IsRemoveMode = false;
        Screen = ShipmentScreen.Scanning;
        ApplyDetail(d);
        ShowFeedback(FeedbackKinds.Idle, "Barkod okutun", $"Seçili lot: {LotTitle}");
        FocusRequested?.Invoke(ShipmentScreen.Scanning);
    }

    private void ApplyDetail(ShipmentDetail d)
    {
        _version = d.Version;
        ShipmentNo = d.ShipmentNo;
        LotTitle = string.Join(", ", d.Lots.Select(l => l.Name));
        ShipmentInfo = $"Açan: {d.OpenedBy} · {StockViewModel.Local(d.OpenedAt)} · {ShipmentStatuses.Display(d.Status)}";
        Warnings = string.Join("  ", d.Warnings);
        ApplyStats(d.Stats);
        Items.Clear();
        foreach (var i in d.Items)
            Items.Add(new ShipmentItemView(i.CutId, i.LotName, Length.FormatGrouped(i.LengthCm) + " m", Time(i.AddedAt), i.AddedBy));
        ScanLog.Clear();
        foreach (var s in d.RecentScans)
            ScanLog.Add(new ScanLogView(Time(s.ServerTime), s.Accepted ? s.Message : $"RET — {s.Message}", s.Code, s.Accepted, s.User));
        if (d.Status != ShipmentStatuses.Open)
            ShowFeedback(FeedbackKinds.Warning, $"{d.ShipmentNo} {ShipmentStatuses.Display(d.Status).ToLowerInvariant()}", "Bu sevkiyata okutma yapılamaz.");
    }

    private void ApplyStats(ShipmentStats s)
    {
        CountText = s.Count.ToString("N0", Turkish);
        MetersText = Length.FormatGrouped(s.LengthCm);
        RejectedText = s.Rejected.ToString("N0", Turkish);
        RemainingText = s.RemainingCount.ToString("N0", Turkish);
        RemainingMetersText = $"{Length.FormatGrouped(s.RemainingCm)} m";
    }

    /// <summary>Okutma (okuyucu ya da elle yazılıp Enter). Sırayla gönderilir.</summary>
    public void SubmitScan(string raw)
    {
        raw = raw.Trim();
        ScanText = "";
        if (raw.Length == 0 || CurrentId is not { } id) return;
        if (IsConfirmOpen || IsCancelOpen) CloseConfirmPanel();
        CloseCancelPanel();
        _lastActivity = DateTime.Now;

        // Okuyucunun aynı barkodu art arda iki kez göndermesi
        if (raw == _lastRaw && DateTime.Now - _lastRawAt < DuplicateWindow)
        {
            _lastRawAt = DateTime.Now;
            return;
        }
        _lastRaw = raw;
        _lastRawAt = DateTime.Now;

        _queue.Enqueue((id, new ScanRequest(Guid.NewGuid().ToString("N"), raw), IsRemoveMode));
        PendingCount = _queue.Count;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_queue.Count > 0)
            {
                var (shipmentId, request, remove) = _queue.Peek();
                try
                {
                    var r = await _api.ScanAsync(shipmentId, request, remove);
                    _queue.Dequeue();
                    PendingCount = _queue.Count;
                    IsServerUnreachable = false;
                    if (shipmentId == CurrentId) ApplyScan(r, remove);
                }
                catch (SyncException ex) when (ex.Kind == SyncFailureKind.Offline)
                {
                    // Ağ yok: okutma kaybolmaz, aynı kimlikle tekrar denenir (sunucu ikinci kez işlemez).
                    IsServerUnreachable = true;
                    ShowFeedback(FeedbackKinds.Offline, "BAĞLANTI YOK — OKUTMAYI DURDURUN",
                        $"{_queue.Count} okutma gönderilemedi; bağlantı gelince sırayla gönderilecek. Topları yüklemeye ara verin.");
                    FeedbackSounds.Error();
                    await Task.Delay(2000);
                }
                catch (SyncException ex)
                {
                    _queue.Dequeue();
                    PendingCount = _queue.Count;
                    ShowFeedback(FeedbackKinds.Error, "Okutma işlenemedi", ex.Message);
                    FeedbackSounds.Error();
                }
            }
            if (IsScanScreen && CurrentId is { } current)
            {
                try { ApplyDetail(await _api.GetShipmentAsync(current), keepFeedback: true); } catch (SyncException) { }
            }
        }
        finally
        {
            _pumping = false;
            if (_api.CurrentUser is null) OnSessionChanged();
        }
    }

    private void ApplyDetail(ShipmentDetail d, bool keepFeedback)
    {
        var (kind, title, detail) = (FeedbackKind, FeedbackTitle, FeedbackDetail);
        ApplyDetail(d);
        if (keepFeedback) ShowFeedback(kind, title, detail);
    }

    private void ApplyScan(ScanResponse r, bool remove)
    {
        _version = r.ShipmentVersion;
        ApplyStats(r.Stats);
        var roll = r.Roll is null ? "" : $"{r.Roll.LotName} · {Length.FormatGrouped(r.Roll.LengthCm)} m";
        switch (r.Severity)
        {
            case ScanSeverity.Success when remove:
                ShowFeedback(FeedbackKinds.Remove, $"ÇIKARILDI  {r.Roll?.CutId}", r.Message);
                FeedbackSounds.Removed();
                break;
            case ScanSeverity.Success:
                ShowFeedback(FeedbackKinds.Success, $"✓  {r.Roll?.CutId}", roll);
                FeedbackSounds.Success();
                break;
            case ScanSeverity.Warning:
                ShowFeedback(FeedbackKinds.Warning, "UYARI", r.Message);
                FeedbackSounds.Warning();
                break;
            default:
                ShowFeedback(FeedbackKinds.Error, "✗  YÜKLEMEYİN", r.Message);
                FeedbackSounds.Error();
                break;
        }
    }

    [RelayCommand]
    private void ToggleRemoveMode()
    {
        IsRemoveMode = !IsRemoveMode;
        _lastRaw = null;   // mod değişince aynı barkod hemen tekrar okutulabilsin (okut → çıkarma modu → aynı topu okut)
        ShowFeedback(IsRemoveMode ? FeedbackKinds.Remove : FeedbackKinds.Idle,
            IsRemoveMode ? "ÇIKARMA MODU" : "Barkod okutun",
            IsRemoveMode ? "Tırdan indirilen topu okutun: sevkiyattan çıkar, stoğa döner." : $"Seçili lot: {LotTitle}");
        FocusRequested?.Invoke(ShipmentScreen.Scanning);
    }

    private void ShowFeedback(string kind, string title, string detail)
    {
        FeedbackKind = kind;
        FeedbackTitle = title;
        FeedbackDetail = detail;
    }

    // ---------- Onay ----------

    [ObservableProperty] public partial bool IsConfirmOpen { get; set; }
    [ObservableProperty] public partial string ConfirmText { get; set; } = "";

    [RelayCommand]
    private void BeginConfirm()
    {
        if (CurrentId is null || _queue.Count > 0) return;
        CloseCancelPanel();
        IsRemoveMode = false;
        _confirmRequestId = Guid.NewGuid().ToString("N");
        ConfirmText = $"{ShipmentNo} onaylansın mı?  {CountText} top · {MetersText} m · {LotTitle}.  " +
                      "Onaydan sonra toplar \"sevk edildi\" olur. Tıra yüklenen top sayısını kontrol edin.";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!IsConfirmOpen || CurrentId is not { } id) return;
        try
        {
            var d = await _api.ConfirmShipmentAsync(id, _version, _confirmRequestId!);
            CloseConfirmPanel();
            FeedbackSounds.Success();
            await ShowListAsync($"{d.ShipmentNo} onaylandı: {d.Stats.Count} top · {Length.FormatGrouped(d.Stats.LengthCm)} m sevk edildi.");
        }
        catch (SyncException ex) when (ex.Code == ShipmentErrorCodes.VersionMismatch)
        {
            CloseConfirmPanel();
            ApplyDetail(await _api.GetShipmentAsync(id));
            ShowFeedback(FeedbackKinds.Warning, "Liste değişti", ex.Message);
            FeedbackSounds.Warning();
        }
        catch (SyncException ex)
        {
            ShowFeedback(FeedbackKinds.Error, "Onaylanamadı", ex.Message);   // panel açık kalır, tekrar denenebilir (aynı istek kimliği)
            FeedbackSounds.Error();
        }
    }

    [RelayCommand]
    public void CloseConfirmPanel()
    {
        IsConfirmOpen = false;
        FocusRequested?.Invoke(ShipmentScreen.Scanning);
    }

    // ---------- İptal ----------

    public ObservableCollection<string> CancelReasonList { get; } = [];
    [ObservableProperty] public partial bool IsCancelOpen { get; set; }
    [ObservableProperty] public partial int CancelChoice { get; set; } = -1;
    [ObservableProperty] public partial string CancelHint { get; set; } = "";

    [RelayCommand]
    private void BeginCancel()
    {
        if (CurrentId is null || _queue.Count > 0) return;
        IsConfirmOpen = false;
        IsRemoveMode = false;
        _cancelRequestId = Guid.NewGuid().ToString("N");
        CancelChoice = -1;
        CancelHint = $"{ShipmentNo} iptal edilecek; okutulan {CountText} top stoğa döner. Sebep seçin (1–{CancelReasons.All.Count}) ve Enter.";
        IsCancelOpen = true;
    }

    public void SelectCancelReason(int index)
    {
        if (IsCancelOpen && index >= 0 && index < CancelReasonList.Count) CancelChoice = index;
    }

    [RelayCommand]
    private async Task CancelShipmentAsync()
    {
        if (!IsCancelOpen || CurrentId is not { } id) return;
        if (CancelChoice < 0)
        {
            CancelHint = "Önce sebep seçin (1–3).";
            return;
        }
        try
        {
            var d = await _api.CancelShipmentAsync(id, CancelReasonList[CancelChoice], _cancelRequestId!);
            CloseCancelPanel();
            await ShowListAsync($"{d.ShipmentNo} iptal edildi; toplar stoğa döndü.");
        }
        catch (SyncException ex)
        {
            CancelHint = ex.Message;
        }
    }

    [RelayCommand]
    public void CloseCancelPanel()
    {
        IsCancelOpen = false;
        if (IsScanScreen) FocusRequested?.Invoke(ShipmentScreen.Scanning);
    }

    // ---------- Yenileme (başka cihazdaki değişiklik) ----------

    private async Task RefreshAsync()
    {
        if (Screen == ShipmentScreen.List) await LoadListAsync();
        else if (Screen == ShipmentScreen.Scanning && CurrentId is { } id && _queue.Count == 0 && !IsConfirmOpen)
            ApplyDetail(await _api.GetShipmentAsync(id), keepFeedback: true);
    }

    private static string Time(string utc) =>
        DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("HH:mm:ss", Turkish) : utc;
}
