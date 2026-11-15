using System.IO;
using System.Media;

namespace KumasBarkod.App.Shipments;

/// <summary>
/// Rıhtımda okutma sonucu sesi: personel ekrana bakmadan anlasın.
/// <list type="bullet">
/// <item>Kabul: kısa, ince tek bip.</item>
/// <item>Uyarı (zararsız; ör. aynı top tekrar): orta, çift kısa bip.</item>
/// <item>Ret (top tıra GİRMEMELİ): kalın, uzun, üç darbeli uyarı.</item>
/// <item>Çıkarma: alçalan iki ton.</item>
/// </list>
/// Sesler bellek içinde üretilir (dosya gerekmez), çalma arka planda; ses kartı yoksa sessizce geçer.
/// </summary>
public static class FeedbackSounds
{
    private static readonly Lazy<byte[]> SuccessWav = new(() => Wav([(1320, 110)]));
    private static readonly Lazy<byte[]> WarningWav = new(() => Wav([(740, 90), (0, 60), (740, 90)]));
    private static readonly Lazy<byte[]> ErrorWav = new(() => Wav([(220, 260), (0, 70), (220, 260), (0, 70), (220, 420)]));
    private static readonly Lazy<byte[]> RemoveWav = new(() => Wav([(880, 110), (520, 160)]));

    public static void Success() => Play(SuccessWav.Value);
    public static void Warning() => Play(WarningWav.Value);
    public static void Error() => Play(ErrorWav.Value);
    public static void Removed() => Play(RemoveWav.Value);

    private static void Play(byte[] wav)
    {
        try
        {
            using var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();   // eşzamansız
        }
        catch (Exception)
        {
            // ses aygıtı yok: görsel geri bildirim yeterli
        }
    }

    /// <summary>16 bit mono 22.05 kHz PCM; her parça (frekans Hz, süre ms), 0 Hz = sessizlik.</summary>
    private static byte[] Wav(IReadOnlyList<(int Hz, int Ms)> parts)
    {
        const int rate = 22050;
        var samples = new List<short>();
        foreach (var (hz, ms) in parts)
        {
            var n = rate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                // Yumuşak başlangıç/bitiş (çıtırtı olmasın) + kare benzeri dalga (gürültülü ortamda daha iyi duyulur)
                var edge = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.005));
                var sine = hz == 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate);
                var shaped = Math.Tanh(3 * sine);
                samples.Add((short)(shaped * edge * 0.6 * short.MaxValue));
            }
        }

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
