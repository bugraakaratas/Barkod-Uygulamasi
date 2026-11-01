using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Diagnostics;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;

namespace KumasBarkod.Core.Services;

/// <summary>
/// Kesim etiketini basar ve baskıyı kayda işler. Kayıt baskıdan ÖNCE yapılmıştır:
/// baskı başarısız olursa kayıt kalır, aynı ID ile tekrar basılabilir.
/// </summary>
public sealed class PrintService(
    SqliteConnectionFactory db,
    ILabelPrinter printer,
    TimeProvider clock,
    TimeZoneInfo? timeZone = null,
    ILog? log = null)
{
    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;
    private readonly ILog _log = log ?? NullLog.Instance;

    public ILabelPrinter Printer => printer;

    /// <returns>Baskı sayısı güncellenmiş kayıt.</returns>
    /// <exception cref="CutValidationException">Kayıt yok ya da iptal edilmiş.</exception>
    /// <exception cref="PrinterException">Yazıcıya gönderilemedi; kayıt değişmez.</exception>
    public Cut Print(string cutId)
    {
        var cut = Find(cutId) ?? throw new CutValidationException($"{cutId} kaydı bulunamadı.");
        if (cut.IsVoided)
            throw new CutValidationException($"{cut.CutId} iptal edilmiş; etiketi basılamaz.");

        try
        {
            printer.Print(LabelData.FromCut(cut, _timeZone));
        }
        catch (PrinterException ex)
        {
            _log.Error($"{cut.CutId} etiketi basılamadı ({printer.DisplayName})", ex);
            throw;
        }

        MarkPrinted(cut.CutId);
        return Find(cut.CutId)!;
    }

    private void MarkPrinted(string cutId)
    {
        var now = Timestamps.Format(clock.GetUtcNow());
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);

        c.Execute("""
            UPDATE cuts SET print_count = print_count + 1, last_printed_at = @now
            WHERE cut_id = @cutId
            """, new { cutId, now }, tx);

        // Baskı bilgisi de Sheets kopyasına gider (geri kurmada korunur).
        c.Execute("INSERT INTO sync_outbox (cut_id, operation, created_at) VALUES (@cutId, 'upsert', @now)",
            new { cutId, now }, tx);

        tx.Commit();
    }

    private Cut? Find(string cutId)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Cut>($"SELECT {Cut.SelectColumns} FROM cuts WHERE cut_id = @cutId", new { cutId });
    }
}
