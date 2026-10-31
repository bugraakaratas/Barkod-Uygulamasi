using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

public sealed record HistoryRow(
    string CutId,
    string LotCode,
    string Meters,
    string CreatedAt,
    string StatusText,
    bool IsVoided,
    long PrintCount,
    string? VoidReason)
{
    public static HistoryRow From(Cut c) => new(
        c.CutId,
        c.LotCode,
        Length.Format(c.LengthCm) + " m",
        Timestamps.Parse(c.CreatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
        c.IsVoided ? "İptal" : "Aktif",
        c.IsVoided,
        c.PrintCount,
        c.VoidReason);
}

/// <summary>
/// Geçmiş: tarih, lot/ID ve durum filtresi; sayfalı liste; özet; tekrar baskı.
/// Filtre değişince liste kendiliğinden yenilenir (arama kutusunda kısa bekleme ile).
/// Sorgular arka planda çalışır; eski bir sorgunun geç gelen sonucu yenisinin üstüne yazılmaz.
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    public const int PageSize = 200;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly CutRepository _cuts;
    private readonly PrintService _printService;

    private int _page;
    private int _pageCount = 1;
    private int _requestId;
    private bool _suspendRefresh;
    private CancellationTokenSource? _debounce;

    public HistoryViewModel(CutRepository cuts, PrintService printService)
    {
        _cuts = cuts;
        _printService = printService;
        SetRange(Today().AddDays(-6), Today());   // varsayılan: son 7 gün

        // Başka ekranda kayıt/iptal/baskı olunca, ekran açıksa liste güncellensin.
        WeakReferenceMessenger.Default.Register<HistoryViewModel, CutChangedMessage>(
            this, static (vm, message) => { if (vm.IsActive) _ = vm.RefreshAsync(); });
    }

    /// <summary>Ekran görünürken true (görünmezken gereksiz sorgu yapılmaz).</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Liste yeniden doldurulmadan hemen önce/sonra. Satırlar yeniden çizilince klavye odağı
    /// kaybolur; görünüm, odak listedeyse seçili satıra geri verir.
    /// </summary>
    public event Action? RowsReloading;
    public event Action? RowsReloaded;

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] public partial DateTime? FromDate { get; set; }
    [ObservableProperty] public partial DateTime? ToDate { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";

    /// <summary>0 = Tümü, 1 = Aktif, 2 = İptal</summary>
    [ObservableProperty] public partial int StatusIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReprintCommand), nameof(OpenSelectedCommand))]
    public partial HistoryRow? SelectedRow { get; set; }

    [ObservableProperty] public partial string TotalText { get; set; } = "";
    [ObservableProperty] public partial string ActiveMetersText { get; set; } = "";
    [ObservableProperty] public partial string VoidedText { get; set; } = "";
    [ObservableProperty] public partial string PageText { get; set; } = "";
    [ObservableProperty] public partial string ElapsedText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    partial void OnFromDateChanged(DateTime? value) => FilterChanged();
    partial void OnToDateChanged(DateTime? value) => FilterChanged();
    partial void OnStatusIndexChanged(int value) => FilterChanged();

    partial void OnSearchTextChanged(string value)
    {
        // Yazarken her tuşta sorgu atma: 300 ms durunca ara.
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        _ = Task.Delay(300, token).ContinueWith(_ => FilterChanged(),
            token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FilterChanged()
    {
        if (_suspendRefresh) return;
        _page = 0;
        _ = RefreshAsync();
    }

    [RelayCommand] private void RangeToday() => SetRangeAndRefresh(Today(), Today());
    [RelayCommand] private void RangeLast7Days() => SetRangeAndRefresh(Today().AddDays(-6), Today());
    [RelayCommand] private void RangeThisMonth() => SetRangeAndRefresh(new DateTime(Today().Year, Today().Month, 1), Today());
    [RelayCommand] private void RangeAll() => SetRangeAndRefresh(null, null);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task NextPageAsync() { _page++; return RefreshAsync(); }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private Task PreviousPageAsync() { _page--; return RefreshAsync(); }

    private bool CanGoNext() => !IsBusy && _page + 1 < _pageCount;
    private bool CanGoPrevious() => !IsBusy && _page > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected() => WeakReferenceMessenger.Default.Send(new OpenCutMessage(SelectedRow!.CutId));

    [RelayCommand(CanExecute = nameof(CanReprint))]
    private async Task ReprintAsync()
    {
        var cutId = SelectedRow!.CutId;
        try
        {
            var cut = await Task.Run(() => _printService.Print(cutId));
            ShowMessage(InfoBarSeverity.Success, $"{cutId} etiketi tekrar basıldı",
                _printService.Printer.IsPhysical ? "" : "Test yazıcısı: etiket dosyaya yazıldı.");
            WeakReferenceMessenger.Default.Send(new CutChangedMessage(cut));
        }
        catch (Exception ex) when (ex is PrinterException or CutValidationException)
        {
            ShowMessage(InfoBarSeverity.Warning, $"{cutId} etiketi basılamadı", ex.Message);
        }
    }

    private bool HasSelection() => SelectedRow is not null;
    private bool CanReprint() => SelectedRow is { IsVoided: false };

    public async Task RefreshAsync()
    {
        var requestId = ++_requestId;
        var query = new CutQuery(
            FromDate is { } f ? DateOnly.FromDateTime(f) : null,
            ToDate is { } t ? DateOnly.FromDateTime(t) : null,
            SearchText,
            (CutStatusFilter)StatusIndex,
            _page,
            PageSize);

        IsBusy = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => _cuts.Search(query, TimeZoneInfo.Local));
            sw.Stop();
            if (requestId != _requestId) return;   // daha yeni bir sorgu başladı

            var selectedId = SelectedRow?.CutId;
            RowsReloading?.Invoke();
            Rows.Clear();
            foreach (var cut in result.Items) Rows.Add(HistoryRow.From(cut));
            SelectedRow = Rows.FirstOrDefault(r => r.CutId == selectedId);
            RowsReloaded?.Invoke();

            _pageCount = result.PageCount;
            IsEmpty = result.TotalCount == 0;
            TotalText = result.TotalCount.ToString("N0", Turkish);
            ActiveMetersText = Length.FormatGrouped(result.ActiveLengthCm) + " m";
            VoidedText = result.VoidedCount.ToString("N0", Turkish);
            PageText = result.TotalCount == 0
                ? "Kayıt yok"
                : $"{result.FirstRow.ToString("N0", Turkish)}–{result.LastRow.ToString("N0", Turkish)} / {result.TotalCount.ToString("N0", Turkish)}   ·   Sayfa {result.Page + 1} / {result.PageCount}";
            ElapsedText = $"sorgu {sw.Elapsed.TotalMilliseconds:0} ms";
            if (MessageSeverity == InfoBarSeverity.Error) MessageIsOpen = false;
        }
        catch (CutValidationException ex)
        {
            if (requestId != _requestId) return;
            ShowMessage(InfoBarSeverity.Error, "Filtre geçersiz", ex.Message);
        }
        finally
        {
            if (requestId == _requestId)
            {
                IsBusy = false;
                NextPageCommand.NotifyCanExecuteChanged();
                PreviousPageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void SetRangeAndRefresh(DateTime? from, DateTime? to)
    {
        SetRange(from, to);
        FilterChanged();
    }

    /// <summary>İki tarihi tek seferde değiştir (iki ayrı sorgu atılmasın).</summary>
    private void SetRange(DateTime? from, DateTime? to)
    {
        _suspendRefresh = true;
        FromDate = from;
        ToDate = to;
        _suspendRefresh = false;
    }

    private static DateTime Today() => DateTime.Today;

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
