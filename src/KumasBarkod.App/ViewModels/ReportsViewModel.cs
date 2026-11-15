using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.ViewModels;

// Rapor satırları: sütun başlıkları [DisplayName]'den (tablo ve CSV aynı başlıkları kullanır).
public sealed class StockLotReportRow
{
    [DisplayName("Lot")] public string Lot { get; init; } = "";
    [DisplayName("Not")] public string Note { get; init; } = "";
    [DisplayName("Top")] public long Count { get; init; }
    [DisplayName("Metre")] public string Meters { get; init; } = "";
    [DisplayName("En eski kalite kontrol")] public string Oldest { get; init; } = "";
}

public sealed class DailyReportRow
{
    [DisplayName("Gün")] public string Day { get; init; } = "";
    [DisplayName("Sevkiyat")] public long Shipments { get; init; }
    [DisplayName("Top")] public long Rolls { get; init; }
    [DisplayName("Metre")] public string Meters { get; init; } = "";
}

public sealed class ShippedRollReportRow
{
    [DisplayName("Barkod")] public string CutId { get; init; } = "";
    [DisplayName("Lot")] public string Lot { get; init; } = "";
    [DisplayName("Metre")] public string Meters { get; init; } = "";
    [DisplayName("Sevkiyat")] public string ShipmentNo { get; init; } = "";
    [DisplayName("Sevk tarihi")] public string ShippedAt { get; init; } = "";
    [DisplayName("Onaylayan")] public string ShippedBy { get; init; } = "";
}

public sealed class HistoryReportRow
{
    [DisplayName("Sevkiyat")] public string No { get; init; } = "";
    [DisplayName("Durum")] public string Status { get; init; } = "";
    [DisplayName("Lot")] public string Lots { get; init; } = "";
    [DisplayName("Top")] public long Rolls { get; init; }
    [DisplayName("Metre")] public string Meters { get; init; } = "";
    [DisplayName("Açan")] public string OpenedBy { get; init; } = "";
    [DisplayName("Açılış")] public string OpenedAt { get; init; } = "";
    [DisplayName("Kapatan")] public string ClosedBy { get; init; } = "";
    [DisplayName("Kapanış")] public string ClosedAt { get; init; } = "";
    [DisplayName("Not")] public string Note { get; init; } = "";
}

/// <summary>
/// Raporlar (yönetim): güncel stok (lot bazlı), günlük sevkiyat özeti, sevk edilen toplar, sevkiyat geçmişi.
/// Hepsi sunucuda gerçek kayıtlardan hesaplanır. CSV: noktalı virgül + UTF-8 (Türkçe Excel doğrudan açar).
/// </summary>
public partial class ReportsViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly DepotApiClient _api;

    public ReportsViewModel(DepotApiClient api)
    {
        _api = api;
        ToDate = DateTime.Today;
        FromDate = DateTime.Today.AddDays(-6);
    }

    public string[] Kinds { get; } = ["Güncel stok (lot bazlı)", "Günlük sevkiyat özeti", "Sevk edilen toplar", "Sevkiyat geçmişi"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRange))]
    public partial int KindIndex { get; set; }

    public bool NeedsRange => KindIndex != 0;

    [ObservableProperty] public partial DateTime? FromDate { get; set; }
    [ObservableProperty] public partial DateTime? ToDate { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExportCommand))] public partial IEnumerable? Rows { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";

    [ObservableProperty] public partial bool MessageIsOpen { get; set; }
    [ObservableProperty] public partial string MessageTitle { get; set; } = "";
    [ObservableProperty] public partial string MessageText { get; set; } = "";
    [ObservableProperty] public partial InfoBarSeverity MessageSeverity { get; set; }

    partial void OnKindIndexChanged(int value) => _ = LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        MessageIsOpen = false;
        if (!_api.IsConfigured)
        {
            Show(InfoBarSeverity.Warning, "Depo sunucusu ayarlanmadı", "Ayarlar → Depo sunucusu bölümüne sunucu adresini yazıp kaydedin.");
            return;
        }
        var from = DateOnly.FromDateTime(FromDate ?? DateTime.Today);
        var to = DateOnly.FromDateTime(ToDate ?? DateTime.Today);
        try
        {
            switch (KindIndex)
            {
                case 0:
                    var stock = await _api.GetStockByLotAsync();
                    Rows = stock.Select(s => new StockLotReportRow
                    {
                        Lot = s.Lot, Note = s.Unmatched ? "eşleşmemiş yazım" : "", Count = s.Count,
                        Meters = Length.FormatGrouped(s.LengthCm), Oldest = s.OldestQcAt is null ? "" : StockViewModel.Local(s.OldestQcAt),
                    }).ToList();
                    Summary = $"Toplam stok: {stock.Sum(s => s.Count).ToString("N0", Turkish)} top · {Length.FormatGrouped(stock.Sum(s => s.LengthCm))} m · {stock.Count} lot";
                    break;
                case 1:
                    var daily = await _api.GetDailyShipmentsAsync(from, to);
                    Rows = daily.Select(d => new DailyReportRow
                    {
                        Day = DateOnly.ParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy ddd", Turkish),
                        Shipments = d.Shipments, Rolls = d.Rolls, Meters = Length.FormatGrouped(d.LengthCm),
                    }).ToList();
                    Summary = $"{RangeText(from, to)}: {daily.Sum(d => d.Shipments)} sevkiyat · {daily.Sum(d => d.Rolls).ToString("N0", Turkish)} top · {Length.FormatGrouped(daily.Sum(d => d.LengthCm))} m (geri alınanlar ve iptaller hariç)";
                    break;
                case 2:
                    var shipped = await _api.GetShippedRollsAsync(from, to);
                    Rows = shipped.Select(s => new ShippedRollReportRow
                    {
                        CutId = s.CutId, Lot = s.Lot, Meters = Length.FormatGrouped(s.LengthCm), ShipmentNo = s.ShipmentNo,
                        ShippedAt = StockViewModel.Local(s.ShippedAt), ShippedBy = s.ShippedBy,
                    }).ToList();
                    Summary = $"{RangeText(from, to)}: {shipped.Count.ToString("N0", Turkish)} top · {Length.FormatGrouped(shipped.Sum(s => s.LengthCm))} m sevk edildi";
                    break;
                default:
                    var history = await _api.GetShipmentHistoryAsync(from, to);
                    Rows = history.Select(h => new HistoryReportRow
                    {
                        No = h.ShipmentNo, Status = ShipmentStatuses.Display(h.Status), Lots = h.Lots ?? "", Rolls = h.Rolls,
                        Meters = Length.FormatGrouped(h.LengthCm), OpenedBy = h.OpenedBy, OpenedAt = StockViewModel.Local(h.OpenedAt),
                        ClosedBy = h.ClosedBy ?? "", ClosedAt = h.ClosedAt is null ? "" : StockViewModel.Local(h.ClosedAt), Note = h.Note ?? "",
                    }).ToList();
                    Summary = $"{RangeText(from, to)}: {history.Count} kapanmış sevkiyat " +
                              $"({history.Count(h => h.Status == ShipmentStatuses.Confirmed)} onaylı, {history.Count(h => h.Status == ShipmentStatuses.Cancelled)} iptal, {history.Count(h => h.Status == ShipmentStatuses.Reversed)} geri alınmış)";
                    break;
            }
        }
        catch (SyncException ex)
        {
            Rows = null;
            Summary = "";
            Show(InfoBarSeverity.Error, "Rapor alınamadı", ex.Message);
        }
    }

    private bool CanExport() => Rows is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Raporu kaydet",
            Filter = "CSV (Excel) (*.csv)|*.csv",
            FileName = $"{Kinds[KindIndex]} {DateTime.Now:yyyy-MM-dd HHmm}.csv".Replace("(", "").Replace(")", ""),
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            WriteCsv(dialog.FileName);
            Show(InfoBarSeverity.Success, "Kaydedildi", dialog.FileName);
        }
        catch (IOException ex)
        {
            Show(InfoBarSeverity.Error, "Kaydedilemedi", ex.Message);
        }
    }

    /// <summary>Noktalı virgülle ayrılmış, UTF-8 (BOM'lu) CSV: Türkçe Excel ondalık virgülü ve Türkçe harfleri doğru okur.</summary>
    public void WriteCsv(string path)
    {
        var rows = Rows!.Cast<object>().ToList();
        var props = (rows.FirstOrDefault()?.GetType() ?? typeof(object)).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', props.Select(p => Escape(p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name))));
        foreach (var r in rows)
            sb.AppendLine(string.Join(';', props.Select(p => Escape(Convert.ToString(p.GetValue(r), Turkish) ?? ""))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string v) =>
        v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" :
        v.Length > 0 && "=+-@".Contains(v[0]) ? "'" + v : v;   // Excel formül sanmasın

    private static string RangeText(DateOnly f, DateOnly t) =>
        f == t ? f.ToString("dd.MM.yyyy", Turkish) : $"{f.ToString("dd.MM.yyyy", Turkish)} – {t.ToString("dd.MM.yyyy", Turkish)}";

    private void Show(InfoBarSeverity severity, string title, string text)
    {
        MessageSeverity = severity;
        MessageTitle = title;
        MessageText = text;
        MessageIsOpen = true;
    }
}
