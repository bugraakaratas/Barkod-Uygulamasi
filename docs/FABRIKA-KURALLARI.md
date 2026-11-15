# Fabrika kurulum ve kullanım kuralları

Sistem en sade hâliyle tasarlandı; doğru çalışması bu kurallara bağlı. Kurulumda fabrikaya verilir.
Her geliştirme aşamasında güncellenir. **Yalnızca uygulamada gerçekten bulunan özelliklere dayanan kurallar yazılır.**
Son güncelleme: 2026-11-15 (Faz 5).

## A. Altyapı (kurulumdan önce)

1. **Sunucu bilgisayarı ayrı olur ve hiç kapatılmaz.** Depo sunucusu ekransız, sürekli açık bir mini PC'dir.
   Başka iş için kullanılmaz. Patron ya da depo bilgisayarı sunucu yapılmaz (kapatılır, güncelleme alır).
2. **Sunucu, ağ anahtarı ve Wi-Fi erişim noktası UPS'e bağlanır.** Elektrik kesilince kayıtlar korunur
   (veritabanı her kaydı diske yazmadan "tamam" demez), ama sevkiyat okutması ağ olmadan çalışmaz.
3. **Sunucuya modemden sabit IP verilir** (ya da sabit bilgisayar adı). QC bilgisayarları sunucuyu bu adresle bulur.
4. **Sunucuya takılı kalan bir USB disk** (ya da ağdaki başka bir bilgisayarda klasör) ikinci yedek yeri olarak
   kurulumda seçilir. Yedekler sunucuyla aynı diskte kalırsa disk bozulduğunda yedekler de gider.
5. **Depoda ve rıhtımda (tır yükleme kapısı) Wi-Fi kapsaması olmalı** — sevkiyat ekranı (Faz 4) ve ileride el
   terminali ağ olmadan çalışmaz. Kurulumdan önce telefonla rıhtımda sinyal kontrol edilir; zayıfsa erişim noktası eklenir.
6. Kablolu bağlanabilen bilgisayarlar (QC, rıhtım) **kabloyla** bağlanır; Wi-Fi yalnızca hareket eden cihazlar için.

## B. Kalite kontrol bilgisayarları

1. **Her QC bilgisayarının istasyon kodu farklıdır** (K1, K2, …). Aynı kod iki bilgisayarda kullanılırsa sunucu
   ikincisini kabul etmez ve uyarı verir. Bilgisayar değiştirilirse eski kodun sunucuda serbest bırakılması gerekir
   (`docs/SUNUCU-KURULUM.md` → yönetim komutları).
2. Her QC bilgisayarında **Ayarlar → Depo sunucusu** adresi girilir ve durum çubuğunda **"Depo güncel"** görülür.
3. **Durum çubuğunda "Depo bekleyen" sayısı uzun süre 0'a dönmüyorsa** (turuncu "Depoya ulaşılamıyor") yetkiliye haber
   verilir. Kesim ve etiket bu sırada da çalışır; kayıtlar kaybolmaz, ağ gelince gider. Ama sunucuya ulaşmamış top
   sevkiyatta "sistemde yok" görünür.
4. **Etiketi basılan top stoktadır.** Yanlış girilen kayıt silinmez, **iptal edilir** (sebep seçilerek) ve yeni kayıt açılır;
   iptal edilen top stoktan düşer, geçmişi kalır.
5. **Aynı barkod iki topa yapıştırılmaz.** Bozulan etiket yeniden basılırken (F6) eskisi topun üzerinden sökülür.

## C. Bilgisayar görevleri ve lotlar

1. Uygulama her bilgisayarda aynıdır; **Ayarlar → Bu bilgisayarın görevi** kurulumda seçilir:
   kalite kontrol bilgisayarı "Kalite kontrol", rıhtım bilgisayarı "Sevkiyat (rıhtım)", patron/ofis bilgisayarı "Yönetim".
   Görev yanlış seçilirse ekranlar eksik görünür; kayıtlar etkilenmez.
2. **Lot adı firmayı gösterir.** Lotlar (firmalar) yönetim bilgisayarında **Lotlar** ekranından bir kez tanımlanır.
   Kalite kontrolde lot adı aynı yazılır; büyük/küçük harf, Türkçe harf, boşluk ve tire farkı sorun değildir
   ("LOT-3" = "lot 3"). **Rakam farkı fark sayılır** ("LOT-3" ≠ "LOT-03").
3. Depo Stoğu'ndaki **"eşleşmemiş lot yazımı" uyarısı** yönetimde her gün kontrol edilir: farklı yazılmış lot adları
   Lotlar ekranında doğru lota bağlanır (bağlama düzeltilebilir, geçmişe yazılır). Bağlanmamış toplar firmaya göre
   stok raporunda görünmez ve sevkiyatta lot seçimiyle bulunamaz (Faz 4).
4. Kullanılmayan lot silinmez, **pasif** yapılır (eski topları bağlı kalır).
5. **Kalite kontrolde lot yazarken altta "Tanımlı lotlar" çıkar: listeden seçmek (↓ ya da tıklama) yazmaktan güvenlidir.**
   Yazım farkı varsa etikete tanımlı ad basılır.
6. Tanımlı olmayan bir lot yazılınca uygulama sorar. **Benzer lot önerilirse (ör. "LOT-3 mü?") personel emin değilse
   yöneticiye sorar; tahminle seçmez.** Gerçekten yeni bir firmaysa "2 — yazıldığı gibi" seçilir ve yönetim lotu tanımlar.
   Aynı yeni lot o gün tekrar sorulmaz.

## D. Sevkiyat (rıhtım)

1. **Her personelin kendi kartı ve PIN'i olur**; kart ve PIN paylaşılmaz (okutulan her top okutanın adıyla kaydedilir).
   Personeli yönetim bilgisayarında **Personel** ekranından yetkili ekler. Ayrılan personel silinmez, **pasif** yapılır.
2. **Varsayılan düzen:** rıhtım kapısında kabloyla bağlı bilgisayar + büyük ekran (tıra dönük) + kablosuz okuyucu.
   **Kapıda görevli tek kişi her topu tıra girmeden okutur.** Okutulmayan top tıra yüklenmez.
3. Sevkiyat **lot seçilerek** açılır. Ekranda lot büyük yazılır; yanlış lot topu **kırmızı + uzun uyarı sesi** ile reddedilir.
4. Renkler: **yeşil = yükle**, **kırmızı = YÜKLEME, topu ayır**, sarı = uyarı (ör. aynı top tekrar okutuldu; zararsız),
   koyu kırmızı = **bağlantı yok: okutmayı ve yüklemeyi durdur** (okutulan kayıtlar kaybolmaz, bağlantı gelince gider).
5. Tıra yanlışlıkla giren / indirilen top **Çıkarma modu (F7)** ile okutulur; sevkiyattan çıkar, stoğa döner.
6. Yükleme bitince **Onayla (F9)**: ekrandaki top sayısı tırdakiyle karşılaştırılır, sonra onaylanır. Onaydan sonra toplar
   "sevk edildi" olur ve başka sevkiyata giremez. Kısmi sevkiyat normaldir: lotun okutulmayan topları stokta kalır.
7. Tır gelmezse / yanlış lot seçildiyse **İptal (F8)** + sebep: okutulan toplar stoğa döner. Sevkiyat kayıtları silinmez.
8. Yarım kalan sevkiyat (bilgisayar kapandı, vardiya bitti) **Açık sevkiyatlar** listesinden herkes tarafından devam ettirilir.
   2 saattir işlem görmeyen sevkiyat turuncu, 24 saati geçen kırmızı görünür: yetkili kontrol edip onaylatır ya da iptal eder.
   Sistem açık sevkiyatı kendiliğinden iptal etmez (tıra yüklenmiş toplar yanlışlıkla stokta görünmesin).
9. Kırmızı "sistemde yok" uyarısı: o topun kalite kontrol kaydı depoya ulaşmamış olabilir (QC bilgisayarı ağdan kopuk).
   Top yüklenmez, yetkiliye haber verilir. "Karantina" uyarısı: aynı barkod iki topta — top ayrılır, yetkiliye bildirilir.
10. El terminali kullanılacaksa: Wi-Fi'a bağlanır, tarayıcıda `http://depo-sunucu:5080` açılır (sevkiyat sayfası), okuyucusu
    "klavye gibi + Enter" ayarında olmalı. Kurallar ve kayıtlar rıhtım bilgisayarıyla aynıdır.

## E. Yetkili işleri (yönetim bilgisayarı)

1. **İstisnalar ekranı her gün açılır** (üstte "Bekleyen: …" özeti). "Bekleyen istisna yok" görülmeli.
2. **Karantina (aynı barkod iki topta):** iki top bulunur; SONRADAN kaydedilen top kalite kontrolde iptal edilip yeni etiketle
   kaydedilir ve eski etiketi sökülür. Sonra İstisnalar'da ne yapıldığı yazılarak "Çözüldü" işaretlenir; ilk top sevk edilebilir olur.
3. **Sevk edildikten sonra QC'de iptal edilen top:** tır gitmiş, kayıt sonradan iptal edilmiş. Müşteriyle/rıhtımla kontrol edilir.
4. **Onayı geri alma** yalnızca yanlışlıkla onaylanan (tıra gitmemiş) sevkiyat içindir; sebep yazılır. Toplar stoğa döner,
   onay ve geri alma kayıtta kalır. Tıra gitmiş topun iadesi bu yolla yapılmaz (iade modülü henüz yok).
5. **Raporlar** (güncel stok, günlük sevkiyat, sevk edilen toplar, sevkiyat geçmişi) Excel için CSV olarak kaydedilir.

## F. Yedek ve kontrol (yetkili)

1. Haftada bir sunucudaki ikinci yedek klasöründe (USB) son tarihli yedeklerin olduğu kontrol edilir.
2. Ayda bir sunucuda `KumasBarkodSunucu.exe --kontrol` çalıştırılır ("Veri bütünlüğü: sorun yok" görülmeli).

## G. Sonraki aşamalarda eklenecek (henüz uygulamada YOK)

Google Sheets'e stok ve sevkiyat kopyası (Faz 6); tek top iadesi, depo sayımı, raf yeri (isteğe bağlı).
