using System.Globalization;
using Dapper;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.Core.Data;

public static class SettingKeys
{
    public const string StationCode = "station_code";
    public const string MaxLengthCm = "max_length_cm";
    public const string SheetsUrl = "sheets_url";
    public const string SheetsKey = "sheets_key";
    public const string Printer = "printer";
    /// <summary>Sayaçların en son hangi Sheets adresinden öğrenildiği (yeni adreste bir kez sorulur).</summary>
    public const string SheetsCountersCheckedUrl = "sheets_counters_checked_url";
}

/// <summary><c>settings</c> tablosu: anahtar/değer. Kayıt yoksa varsayılan döner.</summary>
public sealed class SettingsRepository(SqliteConnectionFactory db)
{
    /// <summary>Varsayılan metre üst sınırı: 300 m. Yanlış yazımı (2550 yerine 25,50) yakalar.</summary>
    public const long DefaultMaxLengthCm = 300_00;

    public string? Get(string key)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public void Set(string key, string value)
    {
        using var c = db.Open();
        c.Execute("""
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """, new { key, value });
    }

    public string GetStationCode()
    {
        var code = Get(SettingKeys.StationCode);
        return Stations.IsValidCode(code) ? code! : Stations.DefaultCode;
    }

    public void SetStationCode(string code)
    {
        if (!Stations.IsValidCode(code))
            throw new CutValidationException("İstasyon kodu 2 karakter olmalı: büyük harf + harf/rakam (ör. K1).");
        Set(SettingKeys.StationCode, code);
    }

    public long GetMaxLengthCm()
    {
        var raw = Get(SettingKeys.MaxLengthCm);
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : DefaultMaxLengthCm;
    }

    public void SetMaxLengthCm(long cm)
    {
        if (cm <= 0) throw new CutValidationException("Üst sınır 0'dan büyük olmalı.");
        Set(SettingKeys.MaxLengthCm, cm.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Sheets gönderim adresi ve anahtarı; ikisi de girilmemişse null (gönderim kapalı).</summary>
    public SyncTarget? GetSyncTarget()
    {
        var url = Get(SettingKeys.SheetsUrl);
        var key = Get(SettingKeys.SheetsKey);
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) ? null : new SyncTarget(url, key);
    }

    /// <summary>Boş adres/anahtar gönderimi kapatır.</summary>
    public void SetSyncTarget(string? url, string? key)
    {
        url = url?.Trim() ?? "";
        key = key?.Trim() ?? "";
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new CutValidationException("Sheets adresi https:// ile başlayan geçerli bir adres olmalı.");

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var (k, v) in new[] { (SettingKeys.SheetsUrl, url), (SettingKeys.SheetsKey, key) })
        {
            if (v.Length == 0)
                c.Execute("DELETE FROM settings WHERE key = @k", new { k }, tx);
            else
                c.Execute("""
                    INSERT INTO settings (key, value) VALUES (@k, @v)
                    ON CONFLICT (key) DO UPDATE SET value = excluded.value
                    """, new { k, v }, tx);
        }
        tx.Commit();
    }
}
