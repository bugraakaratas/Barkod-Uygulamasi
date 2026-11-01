# Sunum akışı — donanımsız gösterim

Barkod yazıcısı ve okuyucu olmadan sistemi uçtan uca göstermek için. Süre: ~10 dakika.

## Hazırlık (sunumdan önce)

1. Uygulamayı açın. **Ayarlar** (Ctrl+4) → **Yazıcı**:
   - Yalnızca ekranda göstermek için: **Gösterim (yalnızca ekran)** (varsayılan).
   - Kağıda basıp göstermek için: **Kağıda bas: \<ofis yazıcısı\>** veya **Microsoft Print to PDF**.
2. İsterseniz Google Sheets bağlantısı açık olsun (Ayarlar → Sheets); kayıtların tabloya düştüğünü canlı gösterebilirsiniz.
3. Telefonla gerçek okutma göstermek isterseniz: telefona "barkodu bilgisayara klavye gibi gönderen" bir uygulama kurup bilgisayara bağlayın (isteğe bağlı; uygulamada değişiklik gerekmez).

## Akış

| # | Ne yapılır | Ne gösterilir |
|---|---|---|
| 1 | **Kesim Girişi**: lot yaz → Enter → metre yaz → Enter | Fare yok, iki tuş. Kayıt anında; benzersiz ID (K1-000001). |
| 2 | Sağdaki **Son etiket** | Etiketin gerçek görünümü: Code128 barkod, büyük ID, lot, metre, tarih. Barkod gerçektir. |
| 3 | Aynı lotta 2–3 kesim daha | Lot seçili kalır; yalnızca metre girilir. "Son kayıtlar" listesi. |
| 4 | **Okut (gösterim)** düğmesi | Okuyucunun yaptığı: kayıt **Okutma** ekranında açılır. Etiket görüntüsü de orada. |
| 5 | **Okutma** (Ctrl+2): etiketteki kodu elle yaz (ör. `K1000002`) → Enter | Elle giriş de çalışır; okuyucu da aynı kutuya "yazar". |
| 6 | Okutma ekranında **İptal et (F8)** → sebep → Enter, sonra **Yerine yeni kayıt (F9)** | Yanlış kayıt düzeltilmez, iptal edilip yenisi basılır; iptal kalıcı ve görünür. |
| 7 | **Geçmiş** (Ctrl+3): tarih, lot, durum filtresi | Toplam metre, iptal sayısı; binlerce kayıtta anında arama. |
| 8 | (Kağıt seçiliyse) kayıt aç → kağıttaki etiketi göster | Gerçek boyut 50 × 30 mm; kesip kumaşa yapıştırma. |
| 9 | (Sheets açıksa) Google tablosunu göster | Kayıtların kopyası; internet kesilse de uygulama çalışır, sonra tamamlanır. |

## Sık sorulan

- **İnternet giderse?** Kayıt, etiket, okutma internetsiz çalışır. Alt çubukta "İnternet yok" yazar; bağlantı gelince bekleyenler tek seferde gider.
- **Bilgisayar bozulursa?** Her gün yerel yedek alınır; disk tamamen giderse veriler Google tablosundan geri kurulur (Ayarlar → Sheets'ten geri kur).
- **Etiket yazıcısı gelince ne değişir?** Ayarlar → Yazıcı'dan seçilir; ekran ve akış aynı kalır.
- **Okuyucu gelince?** Tak-çalıştır: klavye gibi çalışır, hangi ekranda okutulursa okutulsun kayıt açılır.
