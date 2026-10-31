using System.Text;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using KumasBarkod.Tests.Data;

namespace KumasBarkod.Tests.Services;

public class PrintServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Istanbul =
        TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "TR", "TR");

    private readonly TempDatabase _db = new();
    private readonly CutService _cuts;
    private readonly FixedClock _clock = new(Now);
    private readonly string _labelDir;

    public PrintServiceTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _cuts = new CutService(_db.Factory, new SettingsRepository(_db.Factory), _clock);
        _labelDir = Path.Combine(Path.GetDirectoryName(_db.Factory.DatabasePath)!, "labels");
    }

    public void Dispose() => _db.Dispose();

    private PrintService ServiceWith(ILabelPrinter printer) => new(_db.Factory, printer, _clock, Istanbul);

    [Fact]
    public void Print_writes_label_file_with_zpl()
    {
        var printer = new FileLabelPrinter(new ZplLabelTemplate(), _labelDir, _clock);
        var cut = _cuts.CreateCut("LOT-A", 2550);

        ServiceWith(printer).Print(cut.CutId);

        var file = Assert.Single(Directory.GetFiles(_labelDir, "*.zpl"));
        Assert.Equal(printer.LastFilePath, file);
        Assert.StartsWith("K1-000001_", Path.GetFileName(file));

        var zpl = Encoding.UTF8.GetString(File.ReadAllBytes(file));
        Assert.Contains("^FDK1000001^FS", zpl);
        Assert.Contains("^FD01.11.2026 09:30^FS", zpl);   // 06:30 UTC → 09:30 yerel
    }

    [Fact]
    public void Print_increments_count_and_queues_sync()
    {
        var cut = _cuts.CreateCut("LOT-A", 2550);
        var service = ServiceWith(new RecordingPrinter());

        service.Print(cut.CutId);
        var after = service.Print(cut.CutId);

        Assert.Equal(2, after.PrintCount);
        Assert.Equal("2026-11-01T06:30:00.000Z", after.LastPrintedAt);
        Assert.Equal(3, OutboxRows(cut.CutId)); // oluşturma + 2 baskı
    }

    [Fact]
    public void Reprint_uses_the_same_id()
    {
        var printer = new RecordingPrinter();
        var cut = _cuts.CreateCut("LOT-A", 2550);
        var service = ServiceWith(printer);

        service.Print(cut.CutId);
        service.Print(cut.CutId);

        Assert.Equal(["K1-000001", "K1-000001"], printer.Printed.Select(l => l.CutId));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM cuts"));
    }

    [Fact]
    public void Printer_failure_keeps_cut_and_does_not_count_print()
    {
        var cut = _cuts.CreateCut("LOT-A", 2550);

        Assert.Throws<PrinterException>(() => ServiceWith(new FailingPrinter()).Print(cut.CutId));

        var stored = _cuts.FindById(cut.CutId)!;
        Assert.Equal(0, stored.PrintCount);
        Assert.Null(stored.LastPrintedAt);
        Assert.Equal(1, OutboxRows(cut.CutId));
    }

    [Fact]
    public void Voided_cut_is_not_printed()
    {
        var printer = new RecordingPrinter();
        var cut = _cuts.CreateCut("LOT-A", 2550);
        using (var c = _db.Factory.Open())
            c.Execute("UPDATE cuts SET status = 'voided', voided_at = '2026-11-01T07:00:00.000Z' WHERE cut_id = @id", new { id = cut.CutId });

        Assert.Throws<CutValidationException>(() => ServiceWith(printer).Print(cut.CutId));
        Assert.Empty(printer.Printed);
    }

    [Fact]
    public void Unknown_cut_is_rejected()
    {
        Assert.Throws<CutValidationException>(() => ServiceWith(new RecordingPrinter()).Print("K1-999999"));
    }

    [Fact]
    public void File_printer_reports_unwritable_folder_as_printer_error()
    {
        // Klasör yerine aynı adda bir dosya: yazılamaz
        Directory.CreateDirectory(Path.GetDirectoryName(_labelDir)!);
        File.WriteAllText(_labelDir, "engel");
        var printer = new FileLabelPrinter(new ZplLabelTemplate(), _labelDir, _clock);

        Assert.Throws<PrinterException>(() =>
            printer.Print(new LabelData("K1-000001", "K1000001", "LOT", 100, DateTime.Now, "K1")));
    }

    private long OutboxRows(string cutId) => Scalar($"SELECT count(*) FROM sync_outbox WHERE cut_id = '{cutId}'");

    private long Scalar(string sql)
    {
        using var c = _db.Factory.Open();
        return c.ExecuteScalar<long>(sql);
    }

    private sealed class RecordingPrinter : ILabelPrinter
    {
        public List<LabelData> Printed { get; } = [];
        public string DisplayName => "kayıt";
        public bool IsPhysical => false;
        public void Print(LabelData label) => Printed.Add(label);
    }

    private sealed class FailingPrinter : ILabelPrinter
    {
        public string DisplayName => "bozuk";
        public bool IsPhysical => true;
        public void Print(LabelData label) => throw new PrinterException("Kağıt bitti");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
