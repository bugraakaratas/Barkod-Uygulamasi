using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

/// <summary>Sol listedeki lot filtresi: tüm lotlar, eşleşmemiş metinler ya da bir lot.</summary>
public sealed record LotFilterItem(string Title, string Detail, long? LotId, bool Unmatched, bool IsWarning = false);

public sealed record StockRowView(
    string CutId, string Barcode, string Lot, bool LotUnmatched, string Meters, string QcAt, string StateText, string State,
    string? Tooltip)
{
    public static StockRowView From(StockRow r) => new(
        r.CutId, r.Barcode,
        r.LotName ?? r.LotRaw,
        r.LotId is null,
        Length.FormatGrouped(r.LengthCm) + " m",
        StockViewModel.Local(r.QcCreatedAt),
        RollStateNames.Display(r.State),
        r.State,
        r.LotId is null ? $"\"{r.LotRaw}\" hiçbir lota bağlı değil (Lotlar ekranından bağlanır)"
            : r.LotRaw != r.LotName ? $"QC'de yazılan: \"{r.LotRaw}\"" : null);
}

public sealed record MovementView(string Time, string Title, string? Detail);

/// <summary>
/// Depo Stoğu (salt okunur): sunucudaki topların listesi, lot ve durum filtreleri, özetler, bir topun bütün geçmişi.
/// Bütün sayılar sunucuda gerçek kayıtlardan hesaplanır. Ekran açıkken 3 sn'de bir değişiklik sorulur.
/// </summary>
public partial class StockViewModel : ObservableObject
{
    public const int PageSize = 200;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Durum filtresi seçenekleri (açılır listedeki sırayla).</summary>
    public static readonly (string Title, string State)[] StateOptions =
    [
        ("Stokta", RollStateNames.InStock),
        ("Sevkiyatta (onay bekliyor)", RollStateNames.Reserved),
        ("Sevk edildi", RollStateNames.Shipped),
        ("İptal", RollStateNames.Voided),
        ("Karantina", RollStateNames.Quarantined),
        ("Tüm durumlar", RollStateNames.All),
    ];

    private readonly DepotApiClient _api;
    private readonly DataVersionPoller _poller;
    private int _requestId;
    private int _detailRequestId;
    private bool _suspend;
    private CancellationTokenSource? _debounce;

    public StockViewModel(DepotApiClient api)
    {
        _api = api;
        _poller = new DataVersionPoller(api);
        _poller.Changed += ReloadAsync;
        _poller.ConnectionChanged += OnConnection;
        foreach (var o in StateOptions) StateTitles.Add(o.Title);
    }

    public ObservableCollection<string> StateTitles { get; } = [];
    public ObservableCollection<LotFilterItem> LotFilters { get; } = [];
    public ObservableCollection<StockRowView> Rows { get; } = [];
    public ObservableCollection<MovementView> Movements { get; } = [];

    [ObservableProperty] public partial LotFilterItem? SelectedLotFilter { get; set; }
    [ObservableProperty] public partial int StateIndex { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial StockRowView? SelectedRow { get; set; }

    [ObservableProperty] public partial string InStockText { get; set; } = "–";
    [ObservableProperty] public partial string InStockMetersText { get; set; } = "";
    [ObservableProperty] public partial string FilteredText { get; set; } = "–";
    [ObservableProperty] public partial string FilteredMetersText { get; set; } = "";
    [ObservableProperty] public partial string WarningsText { get; set; } = "–";
    [ObservableProperty] public partial bool HasWarnings { get; set; }
    [ObservableProperty] public partial string PageText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotConfigured))]
    public partial bool IsConfigured { get; set; } = true;

    public bool NotConfigured => !IsConfigured;
    [ObservableProperty] public partial string ConnectionText { get; set; } = "";
    [ObservableProperty] public partial bool ConnectionProblem { get; set; }

    [ObservableProperty] public partial bool IsDetailOpen { get; set; }
    [ObservableProperty] public partial string DetailTitle { get; set; } = "";
    [ObservableProperty] public partial string DetailSubtitle { get; set; } = "";

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    private int _offset;
    private long _filteredCount;

    public void Activate()
    {
        IsConfigured = _api.IsConfigured;
        if (!IsConfigured)
        {
            ConnectionText = "Depo sunucusu ayarlanmadı: Ayarlar → Depo sunucusu";
            ConnectionProblem = true;
            return;
        }
        _poller.Start();
    }

    public void Deactivate() => _poller.Stop();

    partial void OnSelectedLotFilterChanged(LotFilterItem? value) { if (!_suspend) ResetAndReload(); }
    partial void OnStateIndexChanged(int value) { if (!_suspend) ResetAndReload(); }

    partial void OnSearchTextChanged(string value)
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) System.Windows.Application.Current?.Dispatcher.BeginInvoke(ResetAndReload);
        }, TaskScheduler.Default);
    }

    partial void OnSelectedRowChanged(StockRowView? value)
    {
        if (value is not null) _ = LoadDetailAsync(value.CutId);
    }

    private void ResetAndReload()
    {
        _offset = 0;
        _ = SafeReloadAsync();
    }

    /// <summary>Kullanıcı işleminden sonra: hata ekranda gösterilir (otomatik yenilemede hata bağlantı satırında).</summary>
    private async Task SafeReloadAsync()
    {
        try
        {
            await ReloadAsync();
        }
        catch (SyncException ex)
        {
            Show(InfoBarSeverity.Warning, "Depo bilgisi alınamadı", ex.Message);
        }
    }

    /// <summary>Lot listesi + stok sayfası. Eski isteğin geç gelen sonucu yenisinin üstüne yazılmaz.</summary>
    public async Task ReloadAsync()
    {
        var id = ++_requestId;
        var filter = SelectedLotFilter;
        var state = StateOptions[Math.Clamp(StateIndex, 0, StateOptions.Length - 1)].State;
        var lots = await _api.GetLotsAsync();
        var stock = await _api.GetStockAsync(state, filter?.LotId, filter?.Unmatched ?? false, SearchText, _offset, PageSize);
        if (id != _requestId) return;

        ApplyLots(lots, stock.UnmatchedLotCount, filter);

        InStockText = $"{stock.InStock.Count.ToString("N0", Turkish)} top";
        InStockMetersText = $"{Length.FormatGrouped(stock.InStock.LengthCm)} m";
        _filteredCount = stock.Filtered.Count;
        FilteredText = $"{stock.Filtered.Count.ToString("N0", Turkish)} top";
        FilteredMetersText = $"{Length.FormatGrouped(stock.Filtered.LengthCm)} m";
        HasWarnings = stock.UnmatchedLotCount > 0 || stock.QuarantinedCount > 0;
        WarningsText = HasWarnings
            ? string.Join(" · ", new[]
              {
                  stock.UnmatchedLotCount > 0 ? $"{stock.UnmatchedLotCount} eşleşmemiş lot yazımı" : null,
                  stock.QuarantinedCount > 0 ? $"{stock.QuarantinedCount} top karantinada" : null,
              }.Where(x => x is not null))
            : "Yok";

        var selected = SelectedRow?.CutId;
        Rows.Clear();
        foreach (var r in stock.Rows) Rows.Add(StockRowView.From(r));
        IsEmpty = Rows.Count == 0;
        var pages = Math.Max(1, (int)Math.Ceiling(_filteredCount / (double)PageSize));
        PageText = $"Sayfa {_offset / PageSize + 1} / {pages} · {_filteredCount.ToString("N0", Turkish)} top";
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();

        // Açık ayrıntı varsa (başka ekranda değişmiş olabilir) yenile
        if (selected is not null && Rows.FirstOrDefault(r => r.CutId == selected) is { } again)
        {
            SelectedRow = again;
        }
        else if (IsDetailOpen && DetailTitle.Length > 0)
        {
            _ = LoadDetailAsync(DetailTitle);
        }
    }

    private void ApplyLots(List<LotInfo> lots, long unmatchedCount, LotFilterItem? current)
    {
        _suspend = true;
        try
        {
            LotFilters.Clear();
            LotFilters.Add(new LotFilterItem("Tüm lotlar", "", null, false));
            if (unmatchedCount > 0)
                LotFilters.Add(new LotFilterItem("Eşleşmemiş", $"{unmatchedCount} yazım", null, true, IsWarning: true));
            foreach (var l in lots.Where(l => l.Active || l.InStockCount > 0))
                LotFilters.Add(new LotFilterItem(l.Name,
                    l.InStockCount > 0 ? $"{l.InStockCount} top · {Length.FormatGrouped(l.InStockCm)} m" : "stok yok",
                    l.LotId, false));
            SelectedLotFilter = current is null ? LotFilters[0]
                : LotFilters.FirstOrDefault(f => f.LotId == current.LotId && f.Unmatched == current.Unmatched) ?? LotFilters[0];
        }
        finally
        {
            _suspend = false;
        }
    }

    private async Task LoadDetailAsync(string code)
    {
        var id = ++_detailRequestId;
        try
        {
            var d = await _api.GetRollAsync(code);
            if (id != _detailRequestId) return;
            DetailTitle = d.Roll.CutId;
            DetailSubtitle = $"Barkod {d.Roll.Barcode} · {d.Roll.LotName ?? d.Roll.LotRaw} · {Length.FormatGrouped(d.Roll.LengthCm)} m · " +
                             RollStateNames.Display(d.Roll.State);
            Movements.Clear();
            foreach (var m in d.Movements.Reverse()) Movements.Add(Describe(m));
            IsDetailOpen = true;
        }
        catch (SyncException ex)
        {
            if (id != _detailRequestId) return;
            Show(InfoBarSeverity.Warning, "Top açılamadı", ex.Message);
        }
    }

    /// <summary>Okutulan (ya da yazılan) barkodun geçmişini aç. Listede değilse de sunucudan getirilir.</summary>
    public void OpenRoll(string code)
    {
        SelectedRow = Rows.FirstOrDefault(r => r.CutId == code);
        if (SelectedRow is null) _ = LoadDetailAsync(code);
    }

    [RelayCommand]
    private void CloseDetail()
    {
        IsDetailOpen = false;
        SelectedRow = null;
        Movements.Clear();
        DetailTitle = "";
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage() { _offset = Math.Max(0, _offset - PageSize); _ = SafeReloadAsync(); }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() { _offset += PageSize; _ = SafeReloadAsync(); }

    private bool CanPrevious() => _offset > 0;
    private bool CanNext() => _offset + PageSize < _filteredCount;

    private void OnConnection(string? error)
    {
        if (error is null)
        {
            ConnectionProblem = false;
            ConnectionText = $"Güncel · {DateTime.Now:HH:mm:ss}";
        }
        else
        {
            ConnectionProblem = true;
            ConnectionText = _poller.LastSuccess is { } t
                ? $"Depoya ulaşılamıyor — gösterilen bilgi {t:HH:mm:ss} itibarıyla"
                : "Depoya ulaşılamıyor";
        }
    }

    private static MovementView Describe(MovementInfo m)
    {
        var (title, detail) = m.Kind switch
        {
            "qc_created" => ("Kalite kontrol kaydı — stoğa girdi", m.StationCode is null ? null : $"İstasyon {m.StationCode}"),
            "qc_voided" => ("Kalite kontrolde iptal edildi", m.Note),
            "lot_assigned" => ("Lot bağlandı", m.Note),
            "quarantined" => ("Karantinaya alındı", m.Note),
            "released" => ("Karantinadan çıkarıldı", m.Note),
            "reserved" => ("Sevkiyata okutuldu", m.Note),
            "unreserved" => ("Sevkiyattan çıkarıldı", m.Note),
            "shipped" => ("Sevk edildi", m.Note),
            "ship_reversed" => ("Sevk geri alındı", m.Note),
            _ => (RollStateNames.Display(m.ToState), m.Note),
        };
        return new MovementView(Local(m.OccurredAt), title, detail);
    }

    public static string Local(string utc) =>
        DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish)
            : utc;

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
