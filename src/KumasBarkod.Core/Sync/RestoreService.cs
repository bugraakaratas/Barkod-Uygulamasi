using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Sync;

public sealed record RestoreResult(int Cuts, int Voided, IReadOnlyDictionary<string, long> Counters);

/// <summary>
/// Disk arızası / yeni bilgisayar: veritabanını Google Sheets kopyasından kurar.
/// - Yalnızca BOŞ veritabanına yapılır (yanlışlıkla mevcut verinin üstüne yazılmasın).
/// - Tablo baştan sona okunur ve doğrulanır; tek bir bozuk satır varsa hiçbir şey yazılmaz.
/// - İptal durumu ve baskı bilgisi korunur. Kuyruğa yazılmaz (veri zaten tabloda).
/// - Sayaç, her istasyonun en büyük sıra numarasının 1000 ÜSTÜNE ayarlanır: tabloya henüz
///   gönderilmemiş son kayıtların ID'leri bilinemez, böylece eski bir ID tekrar üretilmez.
/// </summary>
public sealed class RestoreService(SqliteConnectionFactory db, ISyncTransport transport, TimeProvider clock)
{
    public const int PageSize = 2000;
    public const long SequenceGap = 1000;

    public bool IsLocalDatabaseEmpty()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM cuts") == 0;
    }

    public async Task<RestoreResult> RestoreAsync(SyncTarget target, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocalDatabaseEmpty())
            throw new CutValidationException(
                "Bu bilgisayarda zaten kesim kaydı var. Geri kurma yalnızca boş veritabanına yapılır.");

        // 1) Tüm satırları oku
        var rows = new List<CutSyncRow>();
        int total;
        do
        {
            var page = await transport.ExportAsync(target, rows.Count, PageSize, cancellationToken);
            total = page.Total;
            rows.AddRange(page.Rows);
            progress?.Report($"Tablodan okunuyor: {rows.Count:N0} / {total:N0}");
            if (page.Rows.Count == 0) break;
        } while (rows.Count < total);

        if (rows.Count != total)
            throw new CutValidationException($"Tablo okunurken satır sayısı değişti ({rows.Count} / {total}). Tekrar deneyin.");
        if (rows.Count == 0)
            throw new CutValidationException("Tabloda geri kurulacak kayıt yok.");

        // 2) Doğrula (tek hata bile varsa yazma)
        progress?.Report("Kayıtlar doğrulanıyor…");
        var errors = new List<string>();
        var seen = new HashSet<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var error = Validate(rows[i]);
            if (error is null && !seen.Add(rows[i].CutId)) error = "aynı ID tabloda birden fazla";
            if (error is not null) errors.Add($"Tablo satırı {i + 2} ({rows[i].CutId ?? "ID yok"}): {error}");
        }
        if (errors.Count > 0)
        {
            throw new CutValidationException(
                $"Tabloda {errors.Count} hatalı satır var; geri kurma yapılmadı. Tablo elle değiştirilmiş olabilir.\n"
                + string.Join("\n", errors.Take(5)) + (errors.Count > 5 ? "\n…" : ""));
        }

        // 3) Tek işlemde yaz
        progress?.Report($"{rows.Count:N0} kayıt yazılıyor…");
        var counters = rows
            .GroupBy(r => r.StationCode)
            .ToDictionary(g => g.Key, g => Math.Min(g.Max(r => r.SequenceNo) + SequenceGap, CutIds.MaxSequence));

        using (var c = db.Open())
        using (var tx = c.BeginTransaction(deferred: false))
        {
            // Aynı işlem içinde tekrar kontrol: okuma sürerken uygulamada kayıt açılmış olabilir
            if (c.ExecuteScalar<long>("SELECT count(*) FROM cuts", transaction: tx) != 0)
                throw new CutValidationException("Geri kurma sırasında yeni kayıt açıldı; geri kurma iptal edildi.");

            c.Execute("""
                INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, operator, status,
                                  void_reason, voided_at, created_at, print_count, last_printed_at)
                VALUES (@CutId, @StationCode, @SequenceNo, @LotCode, @LengthCm, @Operator, @Status,
                        @VoidReason, @VoidedAt, @CreatedAt, @PrintCount, @LastPrintedAt)
                """, rows, tx);

            foreach (var (station, last) in counters)
            {
                c.Execute("""
                    INSERT INTO station_counter (station_code, last_sequence) VALUES (@station, @last)
                    ON CONFLICT (station_code) DO UPDATE SET last_sequence = max(last_sequence, excluded.last_sequence)
                    """, new { station, last }, tx);
            }

            c.Execute("""
                INSERT INTO settings (key, value) VALUES ('restored_at', @now)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value
                """, new { now = Timestamps.Format(clock.GetUtcNow()) }, tx);

            tx.Commit();
        }

        var voided = rows.Count(r => r.Status == CutStatus.Voided);
        progress?.Report($"Tamamlandı: {rows.Count:N0} kayıt ({voided:N0} iptal).");
        return new RestoreResult(rows.Count, voided, counters);
    }

    /// <summary>Satırın veritabanı kurallarına uyup uymadığı; uymuyorsa Türkçe sebep.</summary>
    public static string? Validate(CutSyncRow r)
    {
        if (string.IsNullOrEmpty(r.CutId)) return "ID boş";
        if (!Stations.IsValidCode(r.StationCode)) return "istasyon kodu geçersiz";
        if (r.SequenceNo is < 1 or > CutIds.MaxSequence) return "sıra numarası geçersiz";
        if (r.CutId != CutIds.Format(r.StationCode, r.SequenceNo)) return "ID istasyon ve sıra numarasıyla uyuşmuyor";
        if (string.IsNullOrEmpty(r.LotCode) || r.LotCode != r.LotCode.Trim()) return "lot boş veya boşluklu";
        if (r.LengthCm <= 0) return "metre geçersiz";
        if (r.PrintCount < 0) return "baskı sayısı geçersiz";
        if (!IsTimestamp(r.CreatedAt)) return "oluşturma zamanı geçersiz";
        if (r.LastPrintedAt is not null && !IsTimestamp(r.LastPrintedAt)) return "son baskı zamanı geçersiz";

        switch (r.Status)
        {
            case CutStatus.Active:
                if (r.VoidedAt is not null || r.VoidReason is not null) return "aktif kayıtta iptal bilgisi var";
                break;
            case CutStatus.Voided:
                if (r.VoidedAt is null || !IsTimestamp(r.VoidedAt)) return "iptal zamanı eksik veya geçersiz";
                break;
            default:
                return $"durum geçersiz ({r.Status})";
        }
        return null;
    }

    private static bool IsTimestamp(string? s)
    {
        if (s is null) return false;
        try { Timestamps.Parse(s); return true; }
        catch (FormatException) { return false; }
    }
}
