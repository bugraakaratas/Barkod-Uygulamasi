# Fabrika kurulum ve kullanım kuralları

Sistem en sade hâliyle tasarlandı; doğru çalışması bu kurallara bağlı. Kurulumda fabrikaya verilir.
Her geliştirme aşamasında güncellenir. **Yalnızca uygulamada gerçekten bulunan özelliklere dayanan kurallar yazılır.**
Son güncelleme: 2026-11-15 (Faz 1).

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

## C. Yedek ve kontrol (yetkili)

1. Haftada bir sunucudaki ikinci yedek klasöründe (USB) son tarihli yedeklerin olduğu kontrol edilir.
2. Ayda bir sunucuda `KumasBarkodSunucu.exe --kontrol` çalıştırılır ("Veri bütünlüğü: sorun yok" görülmeli).

## D. Sonraki aşamalarda eklenecek (henüz uygulamada YOK)

Lot/firma tanımları, depo stok ekranı, sevkiyat okutma kuralları (lot seçmeden okutma yok, yanlış lot reddi, onay ve iptal),
personel kartı ile giriş, rıhtım ekranı yerleşimi.
