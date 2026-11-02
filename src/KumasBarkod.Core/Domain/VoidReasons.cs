namespace KumasBarkod.Core.Domain;

/// <summary>
/// İptal ekranında seçilebilen sabit sebepler (tek seçim). Seçilen metin <c>void_reason</c>
/// sütununa aynen yazılır; eski serbest metinli iptaller ve geri kurulan kayıtlar geçerli kalır.
/// Sıra = ekrandaki tuş numarası (1, 2, 3, 4). Metinleri değiştirmek raporlardaki sayımı böler.
/// </summary>
public static class VoidReasons
{
    public const string WrongLength = "Yanlış metre girildi";
    public const string WrongLot = "Yanlış lot girildi";
    public const string Duplicate = "Çift kayıt";
    public const string NotCut = "Kesim yapılmadı / vazgeçildi";

    public static IReadOnlyList<string> All { get; } = [WrongLength, WrongLot, Duplicate, NotCut];
}
