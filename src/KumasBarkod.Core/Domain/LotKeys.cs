using System.Text;

namespace KumasBarkod.Core.Domain;

/// <summary>
/// Lot (= firma) adlarını karşılaştırma anahtarları. Lot adı ekranda ve etikette YAZILDIĞI GİBİ kalır;
/// anahtar yalnızca "aynı lot mu?" sorusu için kullanılır.
/// <para><see cref="Key"/>: yalnızca yazım farkını yok sayar — büyük/küçük harf, Türkçe harf (İ/I/ı, Ş/S…),
/// boşluk ve ayırıcılar (<c>- _ / . ,</c>). "LOT-3", "lot 3", "Lot_3" → "LOT3". Sunucu bu eşitlikte
/// topu lota KENDİLİĞİNDEN bağlar (yazım farkı dışında bir fark yok).</para>
/// <para><see cref="LooseKey"/>: ayrıca sayılardaki baştaki sıfırları atar ("LOT-003" → "LOT3").
/// Yalnızca ÖNERİ için: "A01" ile "A1" gerçekte farklı lot olabilir, bağlamayı yetkili onaylar.</para>
/// </summary>
public static class LotKeys
{
    public static string Key(string? lot)
    {
        var sb = new StringBuilder(lot?.Length ?? 0);
        foreach (var ch in lot ?? "")
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '/' or '.' or ',' or '\\') continue;
            sb.Append(Fold(ch));
        }
        return sb.ToString();
    }

    public static string LooseKey(string? lot)
    {
        var key = Key(lot);
        var sb = new StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            var startsNumber = char.IsAsciiDigit(ch) && (i == 0 || !char.IsAsciiDigit(key[i - 1]));
            var nextIsDigit = i + 1 < key.Length && char.IsAsciiDigit(key[i + 1]);
            if (ch == '0' && startsNumber && nextIsDigit)
            {
                // sayının başındaki sıfırları atla (en az bir rakam kalır)
                while (i + 1 < key.Length && key[i] == '0' && char.IsAsciiDigit(key[i + 1])) i++;
                ch = key[i];
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static char Fold(char ch) => ch switch
    {
        'i' or 'ı' or 'İ' or 'I' => 'I',
        'ş' or 'Ş' => 'S',
        'ğ' or 'Ğ' => 'G',
        'ü' or 'Ü' => 'U',
        'ö' or 'Ö' => 'O',
        'ç' or 'Ç' => 'C',
        _ => char.ToUpperInvariant(ch),
    };
}
