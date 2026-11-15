# Depo Stok + Barkodlu Sevkiyat — Geliştirme Planı

Tarih: 2026-11-14 · **Kullanıcı onayı: 2026-11-15.** Uygulama durumu için `CLAUDE.md` → "Depo stok ve sevkiyat" bölümüne bakın; bu dosya PLANDIR, yazılan her şey yapılmış değildir.

## Bağlam
Kumaş Barkod 1.0.5 bugün yalnızca kalite kontrol (QC) kaydı + etiket basıyor. Her QC bilgisayarında ayrı SQLite var; ortak sunucu yok, kopya Google Sheets'e gidiyor. Kullanıcı, depo stoğu ve barkod okutarak sevkiyat istiyor. En önemli şart veri bütünlüğü: aynı top iki kez sevk edilmemeli, yanlış lot eklenmemeli, iki kişi aynı anda okuttuğunda çift kayıt oluşmamalı, geçmiş hiç silinmemeli.

**Kullanıcı kararları:**
- QC ve depo cihazları aynı yerel ağda.
- Firma lota ait.
- Sevkiyat–lot ilişkisi belli değil → şema çoklu lota uygun, arayüz tek lotla başlıyor.
- Ticari çalışma şimdilik ön fizibilite.

## Güncel kararlar (2026-11-15, kullanıcı) — aşağıdaki bölümlerle çelişirse bunlar geçerli
- **Lot = firma.** Ayrı `customers` tablosu yok; `lots` tablosu firma listesidir (ekranda "Lot / Firma"). Sipariş/parti ayrımı yok. Şema ileride `orders` eklenebilecek şekilde.
- **Etiketi basılan top doğrudan stokta** (`in_stock`); depo kabul okutması yok.
- **İstemci:** Depo Stoğu + Sevkiyat sayfaları **mevcut WPF uygulamasına** eklenir (rıhtım PC'si + kablosuz okuyucu + büyük ekran + belirgin sesler). Bilgisayar başına hangi sayfaların görüneceği Ayarlar'dan seçilir.
- **El terminali ileride kesin:** Bütün kurallar sunucuda; API istemciden bağımsız (`/api/v1`, JSON, hata kodu + önem + Türkçe mesaj sunucudan, okutma başına `clientScanId`, onayda `expectedVersion`, cihaz kaydı + cihaz türü, kullanıcı oturum belirteci Windows'a bağlı değil). WPF yalnızca istemci: doğrulama yok, veritabanına doğrudan erişim yok. El terminali ekranı (sunucudan sunulan sade web sayfası) Faz 4'te yazılır ve telefon tarayıcısıyla test edilir → ileride yalnızca cihaz + Wi-Fi bağlanır.

- **Kurulum düzeni:** 3 ekranlı bilgisayar (Kalite kontrol · Sevkiyat/rıhtım · Patron) + 1 ekransız sunucu kutusu. Aynı WPF uygulaması üçünde de kurulu; Ayarlar'da "bilgisayar rolü" görünen sayfaları belirler. Patron bilgisayarı sunucudan okur (Geçmiş — tüm makineler, Depo Stoğu, Sevkiyatlar, raporlar) → rafa kalkan "yönetici izleme" konusu bununla çözülür.
- **Senkron:** Ekranlar sunucudan birkaç saniyede bir yenilenir (hedef ≤ 3 sn; okutan ekranda anında). QC kaydı sunucuya ~1–2 sn içinde gönderilir (bugünkü 5 sn dürtme kısaltılır). Bağlantı yoksa ekranda "son güncelleme HH:mm:ss" uyarısı.

## 1. Mevcut durum (doğrulandı)
- .NET 10 WPF + Core (arayüzden bağımsız, 262 test), Dapper + SQLite (WAL, FULL, STRICT, CHECK'ler), göç sistemi `PRAGMA user_version`.
- `cuts` tablosu: `cut_id` (K1-000123), lot (serbest metin), `length_cm`, `operator` (boş), durum active/voided, baskı bilgisi. **Firma alanı yok, kullanıcı/oturum açma yok, lot listesi yok.**
- Kayıt ve iptal tek işlemde yapılıp `sync_outbox` → Sheets kuyruğuna yazılıyor (tekrar gönderime dayanıklı, çakışmayı bildiriyor).
- Okutma: `ScanBurstDetector` + `ScanCapture`, `CutIds.TryParseScan`.
- **Sonuç:** Çok kullanıcılı, eş zamanlı ve sunucuda doğrulanan sevkiyat için **merkezi bir veritabanı şart.** Ağ klasöründe paylaşılan SQLite daha önce reddedilmişti, yine reddediliyor.

## 2. Mimari (Advisor + benim değerlendirmem)
```
QC PC'leri (WPF, yerel SQLite, internetsiz çalışır)
   │  server_outbox kuyruğu (Sheets kuyruğunun aynı deseni)
   ▼
Depo sunucusu (fabrikada mini PC, UPS'li, Windows hizmeti)
   ASP.NET Core API + sunucunun yerel diskinde SQLite (tek süreç)
   + web ekranları (Stok, Sevkiyat, Lot/Firma, Raporlar)
   ▲
Depo cihazı: tarayıcı (rıhtım PC'si veya Android el terminali) + klavye gibi çalışan okuyucu
```

**Kararlar ve gerekçeleri:**
- **Sunucu ayrı bir mini PC.** Depocunun masaüstü kapatılır, güncelleme alır. Bütçe yoksa geçici olarak depo PC'sinde Windows hizmeti olarak çalışır.
- **Sunucuda SQLite, PostgreSQL değil (şimdilik).** Yük düşük (saniyede 1–2 okutma). Tek süreç olduğu için yazmalar sırayla işlenir ve "tek kazanan" kendiliğinden sağlanır. Bilgi işlem personeli olmayan fabrikada bakım yükü sıfır. Mevcut göç, yedek ve test altyapısı aynen kullanılır.
  - Alternatif PostgreSQL: satır kilitleri ve dış araç bağlantısı daha iyi; ama kurulum, parola ve yedek yönetimi ister.
  - **Geçiş ölçütü:** yedek sunucu, Power BI/ERP'nin veritabanına doğrudan bağlanması veya raporların okutmayı yavaşlatması. SQL taşınabilir yazılır.
- **Bulut reddedildi:** İnternet kesilince tır yüklenemez.
- **Sevkiyat okutması internetsiz çalışmaz.** İnternetsiz ayırma "tek kazanan" kuralını garanti edemez. Bağlantı koparsa ekran kırmızıya döner, okutma durur, bekleyen okutmalar bağlantı gelince aynı kimlikle gönderilir.
- **QC internetsiz çalışmaya devam eder,** kayıtlar kuyrukta birikir.
- **Depo arayüzü web (Razor Pages + az JavaScript), QC ekranı WPF'te kalır.**
  - Neden: el terminalinde WPF çalışmaz; raporları patron kurulum yapmadan açar (rafa kalkan "yönetici izleme" konusu da çözülür); güncelleme tek yerden yapılır.
  - Blazor Server reddedildi: sürekli bağlantı Wi-Fi'da kopar.
  - Alternatif: rıhtımda sabit PC kullanılacaksa WPF "Depo modu" daha hızlı yazılır. Karar Faz 0'daki saha gözlemine bağlı.
- **Google Sheets kalıyor.** Önce istasyonlardan göndermeye devam edilir; Faz 6'da sunucuya taşınır (Kesimler, Stok, Sevkiyatlar sekmeleri), istasyonlarda kapatılır.

**Advisor'a katıldığım ve kullanıcının önerdiği akışı değiştiren noktalar:**
1. **Hareketsiz sevkiyat kendiliğinden iptal edilmez.** Tıra yüklenmiş toplar stokta görünürdü. Sevkiyat 2 saat hareketsiz kalırsa listede turuncu, 24 saatte yetkili ekranında kırmızı görünür.
2. **"Bir lota tek oturum" kilidi konmaz.** İki personel aynı sevkiyatı birlikte okutabilir. Asıl kilit **top düzeyinde** (kısmi benzersiz indeks).
3. **Onay, sevkiyatın tamamı içindir.** Kısmi sevkiyat = lotun bir kısmı stokta kalır. Tırdan inen top önce "çıkarma modu" ile listeden çıkarılır.
4. **Etiket basılınca otomatik "depoya giriş" yazılmaz.** Kayıt sunucuya ulaşınca "Sisteme giriş" olur. İsteğe bağlı "Depo kabul" okutması ayarla açılır, ilk aşamada kapalı (işçiliği iki katına çıkarmasın).
5. **Lot ataması kaybolmaz.** Lotlar sunucuda tanımlanır (firma lotta), ama sevkiyatta da firma tutulur.

**Kendi eklemem / Advisor'dan ayrıldığım nokta:**
- `lot_key` sadeleştirmesi (büyük harf, ayırıcıları atma, baştaki sıfırları silme) yalnızca **öneri ve uyarı** için kullanılır. Otomatik birleştirme yapılmaz: "A01" ile "A1" gerçekte farklı lot olabilir. Eşleştirmeyi yetkili onaylar.

## 3. Ekranlar
**QC (WPF), az değişiklik:**
- durum çubuğuna "Sunucu güncel · bekleyen N",
- lot kutusunda tanımlı lotlardan otomatik tamamlama (TextBox kalır, ComboBox yok),
- tanımsız lotta sarı onay.

**Web (sunucu):**
1. Giriş (personel kartı barkodu + PIN)
2. **Depo Stoğu** (filtreler: lot, firma, durum, tarih; özet: adet, metre)
3. **Top geçmişi** (QC'den itibaren bütün hareketler + okutma denemeleri)
4. **Lot ve Firma** tanımları + **Eşleşmemiş lotlar**
5. **Sevkiyatlar** listesi (açık, onaylı, iptal; sahipsiz uyarısı)
6. **Sevkiyat okutma:**
   - üst bilgi: no, lot (büyük ve sürekli görünür), firma, tarih, kullanıcı, durum,
   - büyük giriş kutusu, otomatik odak, yeşil/sarı/kırmızı + ses,
   - canlı sayaçlar: adet, metre, ret sayısı, lotta kalan uygun stok,
   - okutulanlar tablosu, çıkarma modu, Onayla, İptal.
7. Yetkili: istisnalar (karantina, sevk edildikten sonra iptal), onayı geri alma.

## 4. Backend
- Yeni projeler: `KumasBarkod.Server` (ASP.NET Core, `UseWindowsService`) ve `KumasBarkod.Server.Tests`. `CutIds` ve göç altyapısı Core'dan ortak kullanılır.
- **Uç noktalar:**

  | Uç nokta | Amaç |
  |---|---|
  | `POST /api/stations/{code}/cuts:batch` | QC'den kesim alımı (tekrar gönderime dayanıklı) |
  | `GET /api/lots` | QC lot önbelleği |
  | `POST /api/shipments` | Sevkiyat açma |
  | `POST /api/shipments/{id}/scans` | Okutma |
  | `POST /api/shipments/{id}/items/{cut}/remove` | Kalemi çıkarma |
  | `POST /api/shipments/{id}/confirm` | Onay (`expectedVersion` ile) |
  | `POST /api/shipments/{id}/cancel` | İptal |
  | `POST /api/shipments/{id}/reverse` | Onayı geri alma (yetkili) |
  | `GET /api/stock`, `GET /api/rolls/{id}/history` | Stok ve top geçmişi |
  | `GET /health` | Sağlık kontrolü |

- Türkçe hata mesajları sunucuda tek yerde üretilir; istemci yalnızca renk ve sesi seçer.
- **İstasyon kaydı:** Aynı istasyon kodu ikinci bir makineden bağlanırsa reddedilir. Bilinen "aynı ID iki PC'de" riski kaynağında kapanır.

## 5. Veritabanı
**QC tarafı (göç v2, v3; v1'e dokunulmaz):**
- `server_outbox`: `CreateCut`, `VoidCut` ve baskı aynı işlemde buraya da yazar.
- `lots_cache` (salt okunur önbellek).

**Sunucu:**
- `customers`, `lots` (lot_code, lot_key, customer_id), `lot_aliases`, `stations`, `users` (rol + PIN özeti + kart kodu).
- `rolls`: kesimin sunucudaki yansısı ve **stok satırının kendisi**; ayrı stok tablosu yok, veri tekrarı yok. QC alanları değiştirilmez. `state` ∈ in_stock, reserved, shipped, voided, quarantined.
- `shipments` (SV-2026-000123, durum open/confirmed/cancelled/reversed, `version`), `shipment_lots`, `shipment_items`.
- `scan_events`: kabul ve ret dahil her okutma; `client_scan_id UNIQUE`.
- `stock_movements`: yalnızca ekleme yapılır; tetikleyicilerle UPDATE ve DELETE yasak.

**Garantiler:**
- `CREATE UNIQUE INDEX … ON shipment_items(cut_id) WHERE status='active'` → bir top aynı anda tek aktif sevkiyatta olur ve **bir kez sevk edilir**.
- Durum geçişlerini bir tetikleyici denetler; koşullu `UPDATE … WHERE state='in_stock'` ile etkilenen satır sayısı kontrol edilir.
- Değişmez kural (testte + gece kontrolünde): durum = son hareket; ayrılmış top ⇔ bir aktif kalem.
- **Model seçimi:** Anlık durum + hareket defteri, aynı işlem içinde. Olay kaynaklı model fazla karmaşık bulundu ve tek kazanan garantisini zorlaştırıyor.

## 6. Okutma ve doğrulama
1. Aynı `client_scan_id` daha önce geldiyse kayıtlı yanıt aynen döner (ağ kopup tekrar denenince çift kayıt olmaz).
2. Barkod ayrıştırılır.
3. `BEGIN IMMEDIATE` ile sırayla kontrol edilir; hata kodları:

   | Kod | Renk | Anlamı |
   |---|---|---|
   | `INVALID_BARCODE` | kırmızı | Barkod ayrıştırılamadı |
   | `NOT_FOUND` | sarı | Sistemde yok ("QC kaydı henüz gelmemiş olabilir") |
   | `QUARANTINED` | kırmızı | Aynı barkod iki topta var |
   | `VOIDED` | kırmızı | Kesim iptal edilmiş |
   | `LOT_UNASSIGNED` | kırmızı | Topun lotu tanımlı değil |
   | `WRONG_LOT` | kırmızı | "Bu barkod seçili lota ait değil. Barkodun lotu: LOT-002. Seçili lot: LOT-003." |
   | `ALREADY_IN_THIS_SHIPMENT` | sarı | Bu sevkiyatta zaten var, zararsız |
   | `RESERVED_IN_OTHER_SHIPMENT` | kırmızı | Başka sevkiyatta (numarasıyla) |
   | `ALREADY_SHIPPED` | kırmızı | Sevk edilmiş (sevkiyat no + tarih) |

4. **Kabul:** ayırma + kalem + hareket + okutma kaydı tek işlemde yazılır.
5. **Ret:** ayrı kısa bir işlemde okutma kaydı (sebep koduyla) yazılır. Böylece "neden reddedildi?" sorusu her zaman yanıtlanır.
6. **Okuyucunun çift okuması:** aynı barkod 1,5 sn içinde tekrar gelirse istemci yok sayar. Asıl koruma sunucuda.

## 7. Stok ve sevkiyat akışı
- **Top:** QC kaydı → (kuyruk) → sunucu: `in_stock` → okutma: `reserved` → onay: `shipped`.
- **Geri dönüşler:** çıkarma veya iptal → `in_stock`; QC'de iptal → `voided`; onayı geri alma → `in_stock` (`ship_reversed` hareketi).
- **Sevkiyat:** `open` → `confirmed` | `cancelled`; `confirmed` → `reversed` (yalnızca yetkili, sebepli).
- **Onay:** `expectedVersion` farklıysa ret ("liste değişti, kontrol edip tekrar onaylayın"). Aynı istek tekrar gelirse başarı döner.

## 8. Hata senaryoları
| Senaryo | Davranış |
|---|---|
| Bağlantı kopması | Kırmızı şerit, okutma durur; 3 deneme, aynı kimlikle sonra gönderim |
| Onay başarısız | Tekrar denenir; tekrar gönderime dayanıklı |
| Yanlışlıkla onay | Yetkili geri alır; hiçbir şey silinmez |
| Yanlış top eklendi | Çıkarma modu |
| Yanlış lot seçildi | Okutmalar `WRONG_LOT` ile reddedilir; sevkiyat iptal edilip doğru lotla açılır |
| İki personel aynı lotta | Serbest, top kilidi korur |
| Terk edilen sevkiyat | Turuncu/kırmızı uyarı; listeden devam veya iptal |
| Top QC'de iptal edildi ama sevkiyatta | Kalem otomatik çıkarılır + uyarı |
| Top QC'de iptal edildi ama sevk edilmiş | Yetkili istisnası |
| Aynı ID'li iki fiziksel top | Karantina + alarm |
| QC kaydı sunucuya gelmemiş | Sarı `NOT_FOUND` + istasyonda "10 dk'dır gönderilmedi" alarmı |
| Sunucu arızası | Sevkiyat durur, QC devam eder; saatlik yedek + yedek PC'ye 30 dk geri kurma prosedürü |
| Elektrik kesintisi | UPS + düzgün kapanma |

## 9. Testler
- **Birim:** durum makinesi, okutma sınıflandırması, lot sadeleştirme.
- **Entegrasyon:** `WebApplicationFactory` + geçici SQLite dosyası.
- **Eşzamanlılık:**
  - 20 paralel istek aynı barkodu okutur → tam 1 kabul;
  - aynı `client_scan_id` 10 kez → tek kalem;
  - onay sırasında okutma;
  - QC iptali ile okutma yarışı.
- **Kesinti:** işlem ortasında süreç öldürülür, değişmez kural kontrol edilir.
- **İstasyon–sunucu:** tekrar paket, sırası karışmış iptal ve baskı, çakışma.
- **Uçtan uca:** kullanıcının listelediği 12 senaryo, gerçek uygulama + tarayıcı + veritabanı kontrolüyle. Her fazın sonunda mevcut 262 testlik regresyon.

## 10. Aşamalar
0. **Saha:** yükleme nasıl yapılıyor, Wi-Fi kapsaması, sabit PC mi el terminali mi, sunucu + UPS. Kod gerekmez, kullanıcıdan bilgi alınır.
1. **Sunucu iskeleti:** göçler, hizmet, yedek, `/health`, istasyon kaydı, kesim alımı + karantina. QC 1.1: `server_outbox` + durum göstergesi.
2. Lot/firma tanımları, eşleşmemiş lotlar, eski lot metinlerini eşleme, **salt okunur Stok + Top geçmişi**.
3. QC lot önbelleği + tanımsız lot uyarısı.
4. **Sevkiyat:** kullanıcılar ve giriş, açma, okutma, çıkarma, iptal, onay, canlı sayaçlar.
5. Onayı geri alma, sahipsiz sevkiyatlar, yetkili istisnaları, ilk raporlar (güncel stok, lot, firma, günlük sevkiyat).
6. Sheets'in sunucuya taşınması (Stok + Sevkiyat sekmeleri).
7. **İleriye bırakılanlar:** depo kabul okutması, sayım, tek top iadesi, depo içi yer/raf, el terminali uygulaması, PostgreSQL, ERP, HTTPS, QC'de operatör girişi.

## 11. Git / GitHub
- Her faz `feature/<faz>` dalında; mantıksal commit'ler + ilgili CLAUDE.md güncellemesi aynı commit'te.
- Testler geçince `main`'e birleştirme ve push.
- Force push yok, geçmiş yeniden yazılmaz. Push öncesi dal ve diff kontrolü.
- Sürüm: QC 1.1.0 (Faz 1), sunucu ayrı kurulum paketi.
- Başlangıç durumu: `main`, `origin/main` ile eşit, çalışma alanı temiz (2026-11-14).

## Ticari çalışma
Donanım ihtiyacı, piyasa ve fiyatlandırma araştırması bu depoda TUTULMAZ (depo herkese açık); kullanıcının bilgisayarında ayrı tutulur.
