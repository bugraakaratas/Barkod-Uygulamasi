using System.Text;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Scanning;

/// <summary>
/// USB okuyucu klavye gibi çalışır: barkodu çok hızlı "yazar" ve sonuna Enter ekler.
/// Bu sınıf karakterlerin geliş zamanına bakarak okutmayı insan yazımından ayırır:
/// karakterler arası en fazla <see cref="MaxGapMs"/> ms, en az <see cref="MinLength"/> karakter,
/// ardından Enter ve içerik geçerli bir kesim ID'si olmalı.
/// Arayüzden bağımsızdır; zaman damgaları milisaniye (WPF <c>InputEventArgs.Timestamp</c>).
///
/// Eşik 100 ms: iki hata türünün bedeli çok farklı.
/// - Okutmayı yazım sanmak: ID lot kutusuna düşer → pahalı. Yavaş okuyucu veya meşgul bilgisayarda
///   karakter arası 50–60 ms'yi aşabiliyor (testte 60 ms eşiği bir kez kaçırdı).
/// - Yazımı okutma sanmak: kişinin tam ID biçiminde 8 karakteri her biri 100 ms'den hızlı yazması
///   gerekir; olsa bile sonuç yalnızca o kaydın açılmasıdır → zararsız.
/// </summary>
public sealed class ScanBurstDetector(int maxGapMs = 100, int minLength = 8)
{
    private readonly StringBuilder _buffer = new();
    private int _lastTimestamp;

    public int MaxGapMs { get; } = maxGapMs;
    public int MinLength { get; } = minLength;

    /// <summary>Bu karakter yeni bir hızlı dizinin ilki mi? (Çağıran, odaktaki kutunun eski hâlini saklar.)</summary>
    public bool StartsNewBurst(int timestamp) =>
        _buffer.Length == 0 || unchecked(timestamp - _lastTimestamp) > MaxGapMs;

    public void OnText(string text, int timestamp)
    {
        if (StartsNewBurst(timestamp)) _buffer.Clear();
        _buffer.Append(text);
        _lastTimestamp = timestamp;
    }

    /// <summary>Enter geldi. Okutmaysa kesim ID'sini, değilse null döner. Her durumda tampon boşalır.</summary>
    public string? OnEnter(int timestamp)
    {
        var text = _buffer.ToString();
        var isBurst = text.Length >= MinLength && unchecked(timestamp - _lastTimestamp) <= MaxGapMs;
        _buffer.Clear();

        return isBurst && CutIds.TryParseScan(text, out var cutId) ? cutId : null;
    }

    public void Reset() => _buffer.Clear();
}
