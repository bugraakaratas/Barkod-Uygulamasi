using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Tests.Data;
using KumasBarkod.Tests.Sync;

namespace KumasBarkod.Tests.Stress;

/// <summary>Rastgele girdi ve eşzamanlılık: kurallar yalnızca seçilmiş örneklerde değil, her girdide tutmalı.</summary>
public class RobustnessTests : IDisposable
{
    private const string Url = "https://script.google.com/macros/s/TEST/exec";
    private const int Seed = 20261106;      // hata çıkarsa aynı girdiler tekrar üretilsin

    private readonly TempDatabase _db = new();
    private readonly SettingsRepository _settings;
    private readonly CutService _cuts;

    public RobustnessTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _settings = new SettingsRepository(_db.Factory);
        _cuts = new CutService(_db.Factory, _settings, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private static string RandomText(Random r, int maxLength)
    {
        // Türkçe, ZPL komutları, boşluk türleri, denetim karakterleri, emoji, formül başlangıçları
        string[] pool =
        [
            "a", "Z", "0", "9", "ğ", "Ğ", "ü", "ş", "ı", "İ", "ö", "ç", "-", "_", "/", "\\", "'", "\"", "%", "=", "+", "@",
            "^", "~", "^XZ", "^XA", "~JA", "_5E", " ", "\t", "\n", "\r\n", " ", "​", "\u0000", "\u0007", "😀", "٣", "１",
        ];
        var sb = new StringBuilder();
        var n = r.Next(0, maxLength + 1);
        for (var i = 0; i < n; i++) sb.Append(pool[r.Next(pool.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Length_parser_accepts_exactly_positive_ascii_integers_up_to_limit()
    {
        var r = new Random(Seed);
        const long max = 30000;
        for (var i = 0; i < 200_000; i++)
        {
            var input = r.Next(3) switch
            {
                0 => r.Next(-5, 40000).ToString(),
                1 => RandomText(r, 6),
                _ => new string(Enumerable.Range(0, r.Next(1, 12)).Select(_ => "0123456789,. "[r.Next(13)]).ToArray()),
            };

            var ok = Length.TryParseCentimeters(input, max, out var cm, out var error);

            var trimmed = input.Trim();
            var digits = trimmed.TrimStart('0');
            var expected = Regex.IsMatch(trimmed, "^[0-9]+$") && digits.Length is > 0 and <= 5 && long.Parse(digits) <= max;
            Assert.True(expected == ok, $"girdi: [{input}] beklenen {expected}");
            if (ok) Assert.InRange(cm, 1, max);
            else { Assert.Equal(0, cm); Assert.NotEmpty(error); }
        }
    }

    [Fact]
    public void Scan_parser_never_throws_and_accepted_ids_are_canonical()
    {
        var r = new Random(Seed);
        for (var i = 0; i < 200_000; i++)
        {
            var input = r.Next(2) == 0 ? RandomText(r, 12) : $"K{r.Next(10)}{(r.Next(2) == 0 ? "-" : "")}{r.Next(0, 2000000)}";
            if (CutIds.TryParseScan(input, out var id))
                Assert.Matches("^[A-Z][A-Z0-9]-[0-9]{6}$", id);
        }
    }

    [Fact]
    public void Any_lot_is_either_rejected_cleanly_or_stored_exactly()
    {
        var r = new Random(Seed);
        for (var i = 0; i < 3000; i++)
        {
            var input = RandomText(r, 30);
            Cut cut;
            try
            {
                cut = _cuts.CreateCut(input, 100);
            }
            catch (CutValidationException)
            {
                continue;
            }

            Assert.Equal(cut.LotCode, _cuts.FindById(cut.CutId)!.LotCode);
            Assert.Equal(cut.LotCode.Trim(), cut.LotCode);
        }
    }

    [Fact]
    public void Label_never_lets_lot_text_inject_printer_commands()
    {
        var template = new ZplLabelTemplate();
        var r = new Random(Seed);
        for (var i = 0; i < 20_000; i++)
        {
            string lot;
            try { lot = CutService.NormalizeLot(RandomText(r, 40)); }
            catch (CutValidationException) { continue; }

            var zpl = Encoding.UTF8.GetString(template.Render(new LabelData("K1-000123", "K1000123", lot, 2550, new DateTime(2026, 11, 1, 9, 30, 0), "K1")));

            Assert.Equal(1, Regex.Count(zpl, @"\^XA"));
            Assert.Equal(1, Regex.Count(zpl, @"\^XZ"));
            var lotLine = zpl.Split('\n').Single(l => l.Contains("Lot:"));
            var field = lotLine[(lotLine.IndexOf("^FDLot:", StringComparison.Ordinal) + 3)..lotLine.LastIndexOf("^FS", StringComparison.Ordinal)];
            Assert.DoesNotContain('^', field);
            Assert.DoesNotContain('~', field);
            Assert.DoesNotContain('\uFFFD', zpl);     // kırpma emojiyi ikiye bölmemeli
        }
    }

    [Theory]
    [InlineData("LOT\u200B1")]      // sıfır genişlik boşluk: ekranda LOT1 gibi görünür, arama bulamaz
    [InlineData("\uFEFFLOT1")]
    [InlineData("LOT\u00AD1")]
    public void Invisible_format_characters_are_rejected(string lot)
    {
        Assert.Throws<CutValidationException>(() => CutService.NormalizeLot(lot));
    }

    [Fact]
    public void Parallel_writers_never_duplicate_or_skip_ids()
    {
        const int threads = 8, perThread = 250;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (var i = 0; i < perThread; i++) _cuts.CreateCut($"LOT-{t}", 100 + i);
        });

        using var c = _db.Factory.Open();
        var total = threads * perThread;
        Assert.Equal(total, c.ExecuteScalar<long>("SELECT count(*) FROM cuts"));
        Assert.Equal(total, c.ExecuteScalar<long>("SELECT max(sequence_no) FROM cuts"));
        Assert.Equal(total, c.ExecuteScalar<long>("SELECT last_sequence FROM station_counter WHERE station_code = 'K1'"));
        Assert.Equal(total, c.ExecuteScalar<long>("SELECT count(DISTINCT cut_id) FROM sync_outbox"));
    }

    /// <summary>Gönderici, kayıt, baskı ve iptalle yarışırken son durum tabloya eksiksiz ulaşmalı.</summary>
    [Fact]
    public async Task Sending_while_cutting_printing_and_voiding_ends_with_exact_sheet()
    {
        var sheet = new FakeAppsScript();
        _settings.SetSyncTarget(Url, FakeAppsScript.Key);
        var outbox = new OutboxRepository(_db.Factory);
        var sync = new SyncService(outbox, _settings, new StationCounters(_db.Factory),
            new AppsScriptTransport(new HttpClient(sheet)), TimeProvider.System);
        var print = new PrintService(_db.Factory, new NullPrinter(), TimeProvider.System, TimeZoneInfo.Utc);

        var done = false;
        var sender = Task.Run(async () =>
        {
            while (!Volatile.Read(ref done))
            {
                Assert.Null((await sync.RunOnceAsync()).Failure);
                await Task.Delay(2);
            }
        });

        for (var i = 1; i <= 600; i++)
        {
            var cut = _cuts.CreateCut("LOT", 100 + i);
            print.Print(cut.CutId);
            if (i % 10 == 0) _cuts.VoidCut(cut.CutId, VoidReasons.WrongLength);
        }
        Volatile.Write(ref done, true);
        await sender;
        for (var i = 0; i < 5 && outbox.CountPendingCuts() > 0; i++) await sync.RunOnceAsync();

        Assert.Equal(0, outbox.CountPendingCuts());
        Assert.Equal(600, sheet.Sheet.Count);
        for (var i = 1; i <= 600; i++)
        {
            var row = sheet.Row(CutIds.Format("K1", i));
            Assert.Equal(100 + i, row["length_cm"].GetInt64());
            Assert.Equal(1, row["print_count"].GetInt64());
            Assert.Equal(i % 10 == 0 ? "voided" : "active", row["status"].GetString());
        }
    }

    private sealed class NullPrinter : ILabelPrinter
    {
        public string DisplayName => "yok";
        public bool IsPhysical => false;
        public void Print(LabelData label) { }
    }
}
