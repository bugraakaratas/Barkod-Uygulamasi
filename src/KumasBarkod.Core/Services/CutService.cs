using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Services;

/// <summary>
/// Yeni kesim kaydı oluşturur. Sayaç artışı, kesim satırı ve gönderim kuyruğu
/// TEK işlemde (transaction) yazılır: ya hepsi kalır ya hiçbiri.
/// </summary>
public sealed class CutService(SqliteConnectionFactory db, SettingsRepository settings, TimeProvider clock)
{
    public const int MaxLotLength = 50;

    public Cut CreateCut(string lotCode, long lengthCm, string? @operator = null)
    {
        var lot = NormalizeLot(lotCode);
        var maxCm = settings.GetMaxLengthCm();
        if (lengthCm <= 0)
            throw new CutValidationException("Uzunluk 0'dan büyük olmalı.");
        if (lengthCm > maxCm)
            throw new CutValidationException($"Uzunluk en fazla {Length.FormatCm(maxCm)} cm olabilir.");

        var station = settings.GetStationCode();
        var now = Timestamps.Format(clock.GetUtcNow());
        var op = string.IsNullOrWhiteSpace(@operator) ? null : @operator.Trim();

        using var c = db.Open();
        // BEGIN IMMEDIATE: yazma kilidi baştan alınır, iki kayıt aynı sayıyı okuyamaz.
        using var tx = c.BeginTransaction(deferred: false);

        var last = c.ExecuteScalar<long?>(
            "SELECT last_sequence FROM station_counter WHERE station_code = @station",
            new { station }, tx) ?? 0;

        var sequence = last + 1;
        if (sequence > CutIds.MaxSequence)
            throw new CutValidationException(
                $"{station} istasyonunun sıra numaraları doldu (999999). Ayarlar'dan yeni istasyon kodu verin.");

        c.Execute("""
            INSERT INTO station_counter (station_code, last_sequence) VALUES (@station, @sequence)
            ON CONFLICT (station_code) DO UPDATE SET last_sequence = excluded.last_sequence
            """, new { station, sequence }, tx);

        var cut = new Cut
        {
            CutId = CutIds.Format(station, sequence),
            StationCode = station,
            SequenceNo = sequence,
            LotCode = lot,
            LengthCm = lengthCm,
            Operator = op,
            Status = CutStatus.Active,
            CreatedAt = now,
        };

        c.Execute("""
            INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, operator, status, created_at)
            VALUES (@CutId, @StationCode, @SequenceNo, @LotCode, @LengthCm, @Operator, @Status, @CreatedAt)
            """, cut, tx);

        c.Execute("""
            INSERT INTO sync_outbox (cut_id, operation, created_at) VALUES (@cutId, 'upsert', @now)
            """, new { cutId = cut.CutId, now }, tx);

        tx.Commit();
        return cut;
    }

    public const int MaxVoidReasonLength = 200;

    /// <summary>
    /// Kaydı iptal eder. Yanlış kayıt düzeltilmez: iptal edilir, yerine yeni kayıt açılır
    /// (basılı etiket ile kayıt hiçbir zaman farklı olmasın diye).
    /// </summary>
    public Cut VoidCut(string cutId, string? reason)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length == 0)
            throw new CutValidationException("İptal sebebini yazın.");
        if (text.Length > MaxVoidReasonLength)
            throw new CutValidationException($"İptal sebebi en fazla {MaxVoidReasonLength} karakter olabilir.");
        if (text.Any(char.IsControl))
            throw new CutValidationException("İptal sebebi görünmeyen karakter içeremez.");

        var now = Timestamps.Format(clock.GetUtcNow());

        using (var c = db.Open())
        using (var tx = c.BeginTransaction(deferred: false))
        {
            var status = c.ExecuteScalar<string?>(
                "SELECT status FROM cuts WHERE cut_id = @cutId", new { cutId }, tx)
                ?? throw new CutValidationException($"{cutId} kaydı bulunamadı.");

            if (status == CutStatus.Voided)
                throw new CutValidationException($"{cutId} zaten iptal edilmiş.");

            c.Execute("""
                UPDATE cuts SET status = 'voided', void_reason = @text, voided_at = @now
                WHERE cut_id = @cutId
                """, new { cutId, text, now }, tx);

            c.Execute("INSERT INTO sync_outbox (cut_id, operation, created_at) VALUES (@cutId, 'upsert', @now)",
                new { cutId, now }, tx);

            tx.Commit();
        }

        return FindById(cutId)!;
    }

    /// <summary>
    /// Sıradaki parça: kaynak kesimle aynı lot ve uzunlukta YENİ kesim (yeni ID, sayaç, kuyruk).
    /// İptal edilmiş kesim kopyalanmaz (yanlış veri çoğalmasın); etiketi çıkmamış kesimin ardına
    /// yenisi açılmaz (etiketsiz ama aktif kayıt kalmasın).
    /// </summary>
    public Cut CreateNextPiece(string sourceCutId)
    {
        var source = FindById(sourceCutId)
            ?? throw new CutValidationException($"{sourceCutId} kaydı bulunamadı.");
        if (source.IsVoided)
            throw new CutValidationException(
                $"{source.CutId} iptal edildi; sıradaki parça oluşturulamaz. Lot ve uzunluğu elle girin.");
        if (source.PrintCount == 0)
            throw new CutValidationException($"Önce {source.CutId} etiketini basın (F6).");

        return CreateCut(source.LotCode, source.LengthCm, source.Operator);
    }

    public Cut? FindById(string cutId)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Cut>(
            $"SELECT {Cut.SelectColumns} FROM cuts WHERE cut_id = @cutId", new { cutId });
    }

    /// <summary>Baştaki/sondaki boşluk kırpılır; harf büyüklüğü değiştirilmez (Türkçe i/İ sorunu).</summary>
    public static string NormalizeLot(string? lotCode)
    {
        var lot = lotCode?.Trim() ?? "";
        if (lot.Length == 0)
            throw new CutValidationException("Lot girin.");
        if (lot.Length > MaxLotLength)
            throw new CutValidationException($"Lot en fazla {MaxLotLength} karakter olabilir.");
        if (lot.Any(char.IsControl))
            throw new CutValidationException("Lot görünmeyen karakter içeremez.");
        return lot;
    }
}
