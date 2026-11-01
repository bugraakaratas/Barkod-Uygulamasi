using ZXing;
using ZXing.Common;
using ZXing.OneD;

namespace KumasBarkod.Core.Printing;

/// <summary>
/// Code128 barkodun modül dizisi (true = siyah çubuk). Ekran önizlemesi ve normal yazıcı/PDF
/// çıktısı bununla çizilir. Etiket yazıcısı ise barkodu KENDİ komutuyla (ZPL ^BC) basar.
/// Alt küme seçimi otomatik: "K1000123" → B kümesinde "K1", C kümesinde "000123" (rakamlar
/// ikişer sıkıştırılır) — Zebra'nın ^BC otomatik moduyla aynı mantık.
/// </summary>
public static class Code128
{
    /// <summary>Sessiz bölge (iki yanda boşluk) için önerilen en az modül sayısı.</summary>
    public const int QuietZoneModules = 10;

    /// <summary>Sessiz bölge HARİÇ modüller.</summary>
    public static bool[] Encode(string data)
    {
        ArgumentException.ThrowIfNullOrEmpty(data);

        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0 };
        BitMatrix matrix = new Code128Writer().encode(data, BarcodeFormat.CODE_128, 0, 1, hints);

        var modules = new bool[matrix.Width];
        for (var x = 0; x < matrix.Width; x++) modules[x] = matrix[x, 0];
        return modules;
    }
}
