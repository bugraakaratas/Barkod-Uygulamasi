using System.Globalization;
using Dapper;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Data;

public static class SettingKeys
{
    public const string StationCode = "station_code";
    public const string MaxLengthCm = "max_length_cm";
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
}
