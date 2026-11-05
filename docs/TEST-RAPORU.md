# Test raporu — sürüm 1.0.5 (Sheets script v7)

Tarih: 2026-11-06. Ortam: Windows 10, 1366×768, gerçek Google Apps Script ve gerçek e-tablo (TEST tablosu).
Arayüz testleri geçici veri klasöründe çalıştı, gerçek veritabanına yazılmadı. Test satırları sonunda tablodan silindi.

Tekrar çalıştırma: `tools/test/README.md`.

## Sonuç

| Katman | Sonuç |
|---|---|
| Birim ve kararlılık testleri (`dotnet test`) | 262 / 262 |
| Apps Script testleri (`node --test`) | 25 / 25 |
| Arayüz uçtan uca (tüm ekranlar, kısayollar, sınırlar, okuyucu, iptal, Geçmiş, Ayarlar, yeniden açılış) | 60 / 60 |
| Stres ve çökme (300 bin kayıt, 300 F5, 6 zorla kapatma, iki kopya) | 21 / 21 |
| Gerçek Google zinciri (ekleme, güncelleme, iptal, seri, geri kurma, çakışma) | 13 / 15 + yeniden kurulan çakışma testi 4 / 4 |
| 1 milyon kayıt ölçümü (`KUMAS_STRESS=1`) | 2 / 2 |

Google zincirindeki 2 kalan kontrol uygulama hatası değildi: çakışma testinin dayandığı T8 satırları önceki temizlikte
silinmişti. Tabloda bulunan T6 ile kurulunca 4/4 geçti.

## Testlerin bulduğu ve düzeltilen hatalar

| # | Hata | Etki | Çözüm |
|---|---|---|---|
| 1 | `=` ile başlayan lot Sheets'te formül olarak çalışıyordu (`=1+1` → `2`) | Veri bozuluyor; `=IMPORTXML` dışarıya istek attırabilirdi | Script v7: metin hücreleri başına görünmez tırnak yazılır |
| 2 | Tablodaki tek sayısal metin hücresi geri kurmanın tamamını durduruyordu | Felaket anında hiç veri gelmezdi | `export` metin sütunlarını metin döndürür |
| 3 | Geri kurma hatası yanlış sebep söylüyordu ve ekran dışında kalıyordu | Kullanıcı hatayı göremiyordu | Doğru mesaj, Ayarlar mesajı görünür alana kayar |
| 4 | Her gönderimde tablo baştan sona okunuyordu; süre satırla doğrusal artıyordu | ~450 bin satırda zaman aşımı | Script v6: üst 2000 satır + TextFinder + sayaç önbelleği + kısmi sıralama |
| 5 | Sayfa sonuna yazma varsayılan 1000 satırda hata verebilirdi (gerçek tabloda denenmedi, kod incelemesi) | Birkaç ay sonra gönderim dururdu | Satırlar `insertRowsAfter` ile eklenir, sayfa büyür |
| 6 | Baskı sayısı dolu, son baskı zamanı boş kayıt okutmada hata penceresi açıyordu | Okutma ekranı kullanılamıyordu | Boş değer gösterimi düzeltildi |
| 7 | Sıfır genişlik gibi görünmez karakterler lota girebiliyordu | Aynı görünen iki lot, arama bulamaz | Reddedilir |
| 8 | Uzun lot kırpılırken emoji ikiye bölünebiliyordu | Etikette bozuk karakter | Kırpma karakter sınırına uyar |
| 9 | Her kayıtta listeler gereksiz yere bir kez fazla yükleniyordu | Küçük yavaşlık | Fazla yükleme kaldırıldı |

İlk varsayım olan "13 sütun yerine 2 sütun okumak hızlandırır" gerçek ölçümde yanlış çıktı. Süre sütun sayısına değil,
okunan satır sayısına bağlı. Çözüm bu ölçüme göre yazıldı.

## Ölçümler

### Yerel veritabanı (1 milyon kayıt, 161 MB)

| İşlem | Orta süre |
|---|---|
| Kayıt | 2,5 ms |
| Baskı kaydı | 2,5 ms |
| Son lotlar / son kayıtlar | 5,1 ms / 0,3 ms |
| ID ile bulma | 0,1 ms |
| Geçmiş: son 7 gün | 4,8 ms |
| Geçmiş: tümü, ilk sayfa | 447 ms |
| Geçmiş: tümünde lot araması | 352 ms |
| Geçmiş: tümü, son sayfa | 1,9 sn |
| Gönderim paketi hazırlama | 0,3 ms |
| Yedek + bütünlük kontrolü | 19 sn (arka planda) |

"Tümü, son sayfa" bilinçli olarak bırakıldı: yaklaşık 5 yıllık veride nadir açılır, arayüzü dondurmaz.

### Google Sheets (gerçek ölçüm, ms)

| Satır sayısı | Boş gönderim | 1 yeni | 200 yeni | 200 güncelleme | Karışık 200 | Eski 200 güncelleme | Geç gelen, tüm sıralama |
|---|---:|---:|---:|---:|---:|---:|---:|
| 50.000 (eski kod) | 6.245 | 7.174 | 7.407 | 6.861 | 8.673 | — | 8.319 |
| 50.000 (yeni kod) | 1.069 | 2.247 | 3.344 | 1.913 | 3.809 | 4.819 | 3.529 |
| 100.000 (yeni kod) | 1.177 | 2.234 | 4.866 | 2.164 | 4.207 | 6.481 | 4.524 |
| 200.000 (yeni kod) | 1.140 | 3.273 | 8.512 | 1.664 | 5.828 | 9.671 | 9.118 |

Sınır 60 sn (uygulama zaman aşımı). 200.000 satırda en kötü işlem 9,7 sn.
Yeni scriptin tablodaki ilk çalışmasında sayaçlar için tablo bir kez okunur (50.000 satırda 8 sn).

### Arayüz (300 bin kayıt)

300 art arda F5: kayıp veya fazla kayıt yok; orta 158 ms, en kötü 839 ms (ölçüm aracının yaklaşık 100 ms'si dahil).
Bellek 134 → 173 MB, tutamaç 543 → 556. Geçmiş ekranı 0,8 sn'de açılıyor.

### Çökme

İşlem ortasında 6 kez zorla kapatma: veritabanı her seferinde sağlam, sayaç doğru, kuyruksuz kayıt yok.
Tam baskı anında kesilen kayıtlar `print_count = 0` kalır, listede uyarı simgesiyle görünür ve Geçmiş'ten basılabilir.

## Denenmeyenler ve bilinen sınırlar

- **Gerçek etiket yazıcısı ve USB okuyucu:** baskı test yazıcısıyla (dosyaya ZPL), okutma hızlı klavye benzetimiyle denendi. Türkçe karakterin etikette basılması yazıcıyla doğrulanmalı.
- **Çok bilgisayarlı gerçek kullanım:** iki kopya tek veritabanında ve iki istasyon kodu aynı tabloda denendi; aynı anda çalışan ayrı bilgisayarlar denenmedi.
- **Google kotaları:** ölçümler kısa süreli. Aylar süren kullanımda hesap kotası gözlenmedi.
- **Üst sınırlar:** Google tablosu en fazla 10 milyon hücre alır (13 sütunla yaklaşık 770 bin satır). İstasyon başına en fazla 999.999 ID.
- **Eski tablo satırları:** v7 öncesinde `=` ile başlayan lotla yazılmış bir satır varsa o hücre bozuk kalır. Mevcut 24 satırda böyle bir lot yok.
