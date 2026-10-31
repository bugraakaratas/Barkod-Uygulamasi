using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Printing;

/// <summary>Etikete basılacak bilgiler; şablondan bağımsız.</summary>
public sealed record LabelData(
    string CutId,
    string Barcode,
    string LotCode,
    long LengthCm,
    DateTime CreatedAtLocal,
    string StationCode)
{
    public static LabelData FromCut(Cut cut, TimeZoneInfo timeZone) => new(
        cut.CutId,
        cut.Barcode,
        cut.LotCode,
        cut.LengthCm,
        TimeZoneInfo.ConvertTimeFromUtc(Timestamps.Parse(cut.CreatedAt), timeZone),
        cut.StationCode);
}

/// <summary>Etiketi yazıcının diline (ZPL, TSPL...) çevirir. Marka değişirse yalnızca şablon değişir.</summary>
public interface ILabelTemplate
{
    byte[] Render(LabelData label);
}

/// <summary>Hazır etiket komutunu yazıcıya (veya teste) gönderir.</summary>
public interface ILabelPrinter
{
    /// <summary>Ekranda gösterilecek ad: "Zebra ZD421" ya da "Dosyaya yaz (test)".</summary>
    string DisplayName { get; }

    /// <summary>Gerçek bir yazıcı mı? Test yazıcısında kullanıcıya bu açıkça gösterilir.</summary>
    bool IsPhysical { get; }

    /// <exception cref="PrinterException">Baskı gönderilemedi.</exception>
    void Print(LabelData label);
}

public sealed class PrinterException(string message, Exception? inner = null) : Exception(message, inner);
