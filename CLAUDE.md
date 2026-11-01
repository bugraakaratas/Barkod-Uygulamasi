# Kumaş Kalite Kontrol ve Barkod Takip Sistemi

Bu dosya projenin hafızasıdır. Yeni oturumda önce bunu oku. **Her iş bitince "İlerleme günlüğü" ve "Şu anki durum" bölümlerini güncelle.**

- **Plan:** `C:\Users\User\Desktop\plan.txt` (Aşama 1–7; geliştirme adımları tablosu Aşama 7'de). Kaynak plan: `Desktop\plan.md`.
- **Proje:** `D:\dev\kumas-barkod` — git, `main` dalı.
- **GitHub:** <https://github.com/bugraakaratas/Barkod-Uygulamasi> (`origin`). İlk gönderim 2026-11-01 (adım 8 dahil). **Push yalnızca kullanıcı isteyince.**
  - Kimlik doğrulama Git Credential Manager'da kayıtlı. Claude'un kendi komutu ilk seferde giriş penceresi açamadı; kullanıcı `! git -C D:/dev/kumas-barkod push -u origin main` ile gönderdi (`!` Git Bash'te çalışır: yolda `/` kullan, `\` bozulur).
  - Commit yazarı e-postası: depo herkese açıksa görünür — kullanıcıya bildirildi.
- **Dil:** Kullanıcıyla Türkçe konuşulur. Arayüz metinleri, hata mesajları, kod yorumları Türkçe; kod tanımlayıcıları İngilizce.

## Çalışma kuralları (kullanıcı tercihleri)

- Plan adım adım ilerler. Her adım bitince: test → commit → bu dosyayı güncelle → kullanıcıya özet ver → **sonraki adıma geçmek için onay iste.**
- Kalıcı/sisteme dokunan işlerde (program kurma vb.) önce onay al.
- Her adımda: birim testleri + gerçek uygulamada doğrulama (ekran görüntüsü, veritabanı kontrolü).
- Kullanıcı "son derece profesyonel" bir arayüz istiyor (Fluent tasarım, büyük okunur yazı, tam klavye kullanımı).
- Kullanıcı SQLite'ı ilk kez kullanıyor; teknik konuları sade anlat.

## Ortam

- Windows 10, ekran 1366×768 (pencere tam ekran açılır).
- .NET SDK 10.0.403, Git 2.56 (winget ile kuruldu). Yeni PowerShell'de PATH yenilemek gerekebilir:
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- Derleme: `dotnet build D:\dev\kumas-barkod` · Test: `dotnet test` (proje klasöründe)
- Uygulama: `src\KumasBarkod.App\bin\Debug\net10.0-windows\KumasBarkod.App.exe`
- Uygulama açıkken derleme exe kilidi yüzünden başarısız olur → önce uygulamayı kapat.
- Gerçek veritabanı: `%LOCALAPPDATA%\KumasBarkod\kumas.db` (+ `-wal`, `-shm`).
- **Test/geliştirme:** `KUMASBARKOD_DATA_DIR` ortam değişkeni veri klasörünü değiştirir; arayüz testleri her zaman geçici klasörle çalıştırılır, gerçek veritabanına yazılmaz.
- Veritabanını incelemek için kullanıcıya DB Browser for SQLite önerildi (salt okunur açılmalı).

### Arayüz testi (SendKeys) uyarıları
- Klavye ile test betiği uygulamayı öne getirip tuş gönderir. Pencere önde değilse tuşlar **Claude Code terminaline** gider ve kullanıcı mesajı olarak gönderilir (bir kez yaşandı). Betik her tuştan önce `GetForegroundWindow` kontrolü yapmalı, değilse durmalı.
- Test sırasında kullanıcı klavyeye dokunursa yazdıkları uygulamaya girer (bir kez "efe" lota karıştı). **Testten önce kullanıcıyı uyar.**
- Betikler oturumun scratchpad klasöründe (oturuma özel); gerekirse yeniden yazılır. Desen: Alt tuşu bas/bırak + `SetForegroundWindow`, `SendKeys.SendWait`, ekran görüntüsü `CopyFromScreen`.
- Veritabanı kontrolü için .NET 10 dosya tabanlı betik: `dotnet run dbcheck.cs -- <db>` (`#:package Microsoft.Data.Sqlite`, `#:package Dapper`; salt okunur bağlantı).

## Mimari

```
src/KumasBarkod.Core     (net10.0)  iş mantığı, veri, yazdırma — arayüzden bağımsız, test edilir
  Data/       SqliteConnectionFactory, Migrations (şema), DatabaseMigrator, SettingsRepository, CutRepository
  Domain/     Cut, CutStatus, CutIds (ID/barkod biçimi), Stations, Length (metre ayrıştırma), Timestamps
  Services/   CutService (kesim oluşturma), PrintService (baskı + baskı kaydı)
  Printing/   LabelData, ILabelTemplate, ILabelPrinter, ZplLabelTemplate, FileLabelPrinter, PrinterException
src/KumasBarkod.App      (net10.0-windows, WPF)
  App.xaml(.cs)  DI kurulumu, açılışta veritabanı göçü
  AppPaths.cs    veri klasörü, db yolu, etiket klasörü
  Messages.cs    CutCreatedMessage (WeakReferenceMessenger)
  Themes/Styles.xaml, Converters.cs
  ViewModels/    MainWindowViewModel, CutEntryViewModel, (Scan/History/Settings boş)
  Views/         MainWindow (FluentWindow + NavigationView + durum çubuğu), Pages/*
tests/KumasBarkod.Tests  (xUnit) Data/, Domain/, Services/, Printing/
```

**Paketler:** WPF-UI 4.3.0 (Fluent), CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 10.0.14, Microsoft.Data.Sqlite 10.0.14, Dapper 2.1.89.

### Önemli tasarım kararları
- **Veritabanı:** SQLite, WAL + `synchronous=FULL` + `foreign_keys` + `busy_timeout=5000`. Şema sürümü `PRAGMA user_version`; her göç kendi işleminde. **Yayınlanmış göç asla değiştirilmez, yeni sürüm eklenir.** Uygulamadan yeni şemalı veritabanı açılmaz.
- **Şema v1:** `cuts` (STRICT, CHECK'ler: ID = istasyon + '-' + 6 hane, lot kırpılmış/boş değil, length_cm > 0, durum active/voided, iptal bilgisi tutarlılığı), `sync_outbox` (FK → cuts), `station_counter`, `settings` (anahtar/değer).
- **ID:** `K1-000123` saklanır/görünür; barkodda tiresiz `K1000123` (Türkçe klavye tire sorunu). İstasyon kodu `^[A-Z][A-Z0-9]$`, varsayılan `K1`. Sıra 1–999999.
- **Kayıt:** `CutService.CreateCut` tek işlemde (BEGIN IMMEDIATE) sayaç + cuts + sync_outbox. Hatalı giriş sıra numarası harcamaz.
- **Metre:** santim tam sayı. Virgül/nokta kabul, en fazla 2 ondalık (fazlası reddedilir, yuvarlanmaz), binlik ayırıcı reddedilir. Üst sınır ayardan, varsayılan 300 m (`max_length_cm`).
- **Lot:** kırpılır, en fazla 50 karakter, büyük/küçük harf korunur (Türkçe i/İ).
- **Zaman:** UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` (sabit uzunluk, metin sıralaması = zaman sırası). `TimeProvider` enjekte edilir (testte sabit saat).
- **Giriş ekranı:** açılır liste (ComboBox) KULLANILMAZ — odak sorunları çıkardı. Lot = TextBox; ↑↓ son lotlarda gezer; son lotlar altta düğme (chip) olarak görünür.
- **Kısayollar:** sayfa kısayolları code-behind'da `AddHandler(PreviewKeyDownEvent, ..., handledEventsToo: true)` ile; KeyBinding kullanma (F5 üst denetimde yutuluyor). Giriş ekranı: Enter, F2 (lot), ↑↓ (son lotlar), F5 (son etiketi tekrar bas), Esc (metreyi temizle).
- **Odak:** Tuşa bağlı odak değişimi anında yapılır (`FocusNow`); ertelenirse hızlı yazılan karakter yanlış kutuya düşer.
- **Ekran kısayolları (MainWindow):** Ctrl+1 Kesim Girişi, Ctrl+2 Okutma, Ctrl+3 Geçmiş, Ctrl+4 Ayarlar. Okutma her ekranda yakalanır (bkz. Adım 6 notları).
- **Sayfalar:** her `Page`'de `ScrollViewer.CanContentScroll="False"` (WPF-UI'nin dış kaydırma alanını kapatır; yoksa sanallaştırma bozulur). Kaydırma sayfanın içinde yönetilir.
- **Arayüz testi düğmeleri:** odaklanamayan düğmeler (Focusable=False) testte UI Automation `InvokePattern` ile, ComboBox `ExpandCollapsePattern`/`SelectionItemPattern` ile kullanılır.
- **Baskı:** Kayıt önce yazılır, sonra basılır. Başarılı baskı `print_count+1`, `last_printed_at` yazar ve kuyruğa `upsert` ekler. Baskı hatası kaydı bozmaz; F5 aynı ID ile tekrar basar. İptal edilmiş kesim basılmaz.
- **Etiket (ZPL):** 50×30 mm, 203 dpi, `^CI28` (UTF-8), Code128 `^BCN,80,N,N,N,A` (otomatik mod), lot `^FH_` ile kaçışlı (`^ ~ _`). Şimdilik `FileLabelPrinter` → `<veri klasörü>\labels\*.zpl`. Durum çubuğunda "Test yazıcısı" turuncu görünür.
- **Bekleyen kayıt sayısı:** `count(DISTINCT cut_id)` gönderilmemiş kuyruk satırları.

## İlerleme günlüğü

| Adım | Durum | Commit | Özet |
|---|---|---|---|
| 1 | ✅ | `0941847` | Çözüm iskeleti: WPF App + Core + xUnit, git |
| Arayüz | ✅ | `ea7e440` | WPF-UI Fluent, MVVM Toolkit, DI, sol menü (Kesim Girişi/Okutma/Geçmiş/Ayarlar), durum çubuğu, tam ekran açılış |
| 2 | ✅ | `083ca0e` | SQLite şema v1, göç sistemi, WAL+FULL, 24 şema testi; açılışta db otomatik oluşur |
| 3 | ✅ | `34d3c28` | CutService: sayaç, ID, metre ayrıştırma, lot kuralları, kuyruk; paralel kayıt ve ani kapanış (dosya anlık görüntüsü) testleri |
| 4 | ✅ | `7c5df5c` | Giriş ekranı: lot + son lotlar, canlı metre kontrolü, Enter ile kayıt, son 12 kayıt, bekleyen kayıt sayısı. Klavye testiyle doğrulandı |
| 5 | ✅ | `739b2ca` | Etiket: ILabelTemplate/ILabelPrinter, ZPL şablonu, FileLabelPrinter, PrintService, kayıttan sonra otomatik baskı, F5 tekrar baskı, "Etiket" sütunu, durum çubuğunda yazıcı. 113 test |

### Adım 5 notları
- Birim testleri: ZPL sabit referans çıktısı, kaçış (`^ ~ _`), Türkçe UTF-8, uzun lot, baskı sayacı + kuyruk, yazıcı hatası kaydı bozmaz, iptal kaydı basılmaz.
- Arayüz testinde doğrulandı: otomatik baskı, F5 tekrar baskı (aynı ID), yazıcı hatasında sarı uyarı + "basılmadı" simgesi, hata giderilince F5 ile kurtarma, Esc, hızlı ardışık iki kayıt.
- **Bulunan ve düzeltilen hatalar:**
  1. F5 çalışmıyordu: üst denetim (pencere/NavigationView) F5'i önizleme aşamasında "handled" işaretliyor. Çözüm: sayfa kısayol işleyicisi `AddHandler(PreviewKeyDownEvent, ..., handledEventsToo: true)` ile kayıtlı.
  2. Hızlı yazımda karakter yanlış kutuya düşüyordu ("LOT-A"+Enter+"25,5" → lot "LOT-A2", 5,50 m): Enter sonrası odak `Dispatcher.BeginInvoke` ile ertelenmişti. Çözüm: tuşla odak değişimi `FocusNow` (anında); yalnızca ViewModel isteği (kayıt sonrası) ertelenir. **Okutma ekranında (Adım 6) da aynı kurala uy.**

| 6 | ✅ (cihaz testi bekliyor) | `7876a02` | Okutma: her ekrandan okutma (hız + biçim tespiti), okutma ekranı, elle ID, iptal (sebepli), yerine yeni kayıt, Ctrl+1/2/3 ekran kısayolları. 157 test |

### Adım 6 notları
- **Okutma tespiti (plandan sapma):** Okuyucuda ön ek ayarı GEREKMİYOR. `ScanBurstDetector` (Core): karakter arası ≤ 100 ms, ≥ 8 karakter, Enter ve geçerli ID → okutma. `ScanCapture` (App) pencere düzeyinde `PreviewTextInput`/`PreviewKeyDown` (handledEventsToo) dinler; dizinin ilk karakterinde odaktaki TextBox'ın metnini/seçimini saklar, okutma anlaşılınca geri yükler ve Enter'ı yutar → lot bozulmaz, metre kutusunda kayıt tetiklenmez. Sorun çıkarsa ön ek desteği eklenebilir.
- **Eşik 100 ms neden:** 60 ms ile bir testte (SendKeys ~31–47 ms aralık, meşgul PC) okutma kaçtı ve ID lot kutusuna düştü. Okutmayı kaçırmak pahalı, yazımı okutma sanmak zararsız (yalnızca kayıt açılır).
- `CutIds.TryParseScan`: `K1000123`, `K1-000123`, küçük harf, boşluk, `*` (ABD düzenli okuyucu + Türkçe Q'da tire), `ı/i/İ → I`.
- `CutService.VoidCut`: sebep zorunlu (≤ 200, kontrol karakteri yok), zaten iptal → hata, kuyruğa upsert. İptal edilmiş kayıt basılmaz.
- Okutma ekranı kısayolları: F5 tekrar bas, F8 iptal, F9 yerine yeni kayıt (giriş ekranı aynı lotla açılır), Esc vazgeç/temizle. Elle yazılan ID için Enter (ScanBox_PreviewKeyDown).
- Mesajlar: `CutChangedMessage` (oluşturma/baskı/iptal → listeler ve durum çubuğu), `ScanDetectedMessage`, `StartNewCutMessage`.
- Giriş ekranı "Son kayıtlar": iptal edilen satır üstü çizili + kırmızı simge.
- Arayüz testi (2 kez üst üste aynı sonuç): metre kutusunda ve lot kutusunda okutma (lot korunuyor), elle yavaş ID, iptal, F5 iptal edilmişte basmıyor, F9 yerine yeni kayıt, bulunamayan ve geçersiz barkod.
- **AÇIK:** Gerçek USB okuyucu ile Türkçe klavyede test (plan "Bitti" ölçütü). Kullanıcıya okuyucu olup olmadığı soruldu, yanıt gelmedi.

| 7 | ✅ | `337749c` | Geçmiş: tarih aralığı + hızlı aralıklar, lot/ID arama, durum filtresi, özet kartları, 200'lük sayfalama, kaydı aç, tekrar baskı. 100.000 kayıtla < 200 ms. 176 test |

### Adım 7 notları
- `CutRepository.Search(CutQuery, TimeZoneInfo)`: yerel gün → UTC aralık (bitiş dahil), arama ID biçimindeyse tam `cut_id`, değilse `lot_code LIKE %x% ESCAPE '\'` (% _ \ kaçışlı), durum filtresi, özet (toplam / aktif / iptal / aktif metre — iptaller metreye dahil değil), `ORDER BY created_at DESC, sequence_no DESC LIMIT/OFFSET`. Özet ve sayfa aynı okuma işleminde.
- **Hız (birim testi, 100.000 kayıt):** son 7 gün 2,5 ms · tümü ilk sayfa 44 ms · son sayfa 172 ms · lot içerir 46 ms · ID 0,6 ms. Testte sınır 1000 ms.
- **~1 milyon kayıt ölçümü** (`KUMASBARKOD_BENCH=1` ile çalışan test): son 7 gün 5 ms · ID 0,6 ms · son 30 gün+lot 14 ms · tümü ilk sayfa 465 ms · tümü lot 317 ms · tümü SON sayfa 1,86 sn (derin OFFSET; pratikte kullanılmaz). Dosya ~161 MB.
- Gerçek uygulama testi 100.000 kayıtla (geçici veri, `seed.cs` betiği: `#:project` ile Core'u kullanır): açılış 37 ms, sayfa 3/500 61 ms, LOT-0042 + İptal 85 ms.
- Arayüz: arama 300 ms bekleme ile otomatik; eski sorgunun geç sonucu yok sayılır (`_requestId`); ekran görünür değilken `CutChangedMessage` sorgu tetiklemez. Kısayollar: Ctrl+F arama, aramada ↓/Enter listeye, Enter/çift tık kaydı aç (okutma ekranında), F5 tekrar bas, Ctrl+PageUp/PageDown sayfa, aramada Esc temizle. Uygulama kültürü tr-TR (App.OnStartup).
- **Bulunan ve düzeltilen hatalar:**
  1. WPF-UI sayfayı kendi kaydırma alanına koyuyor → sonsuz yükseklik, DataGrid sanallaştırması bozuluyor, alt çubuk ekran dışına itiliyor, ilk açılış boş görünüyordu. Çözüm: sayfalarda `ScrollViewer.CanContentScroll="False"` (üç sayfaya da eklendi; sayfalar kendi kaydırmasını yönetir). **Yeni sayfalarda da kullan.**
  2. Aramadan ↓ ile listeye geçerken `MoveFocus(Next)` odağı sol menüye kaçırıyordu → `FocusGridRow`: ilk hücreye doğrudan odak.
  3. Seçili satır görünmüyordu (WPF-UI DataGrid stili) → `SelectedRowBrush` arka plan + sol kenar çizgisi.
  4. Liste yenilenince (tekrar baskı sonrası) odak kayboluyordu → `RowsReloading/RowsReloaded` olayları ile seçili satıra geri odak.
- `Length.FormatGrouped` (binlik ayırıcılı, özetlerde); etiket ve giriş `Length.Format` (ayırıcısız) kullanmaya devam eder.
- Mesaj adı değişti: `ScanDetectedMessage` → `OpenCutMessage` (okutma ve geçmişten açma).

| 8 | ✅ | `0aa90af` + `ae4330e` | Sheets kopyası: Apps Script (`tools/sheets/Code.gs`, kurulum `KURULUM.md`), kuyruk göndericisi, arka plan çalışanı + bekleme, durum çubuğu, Ayarlar ekranı (adres/anahtar/test/kaydet/şimdi gönder, metre üst sınırı). 203 test |

### Adım 8 notları
- **Apps Script (`Code.gs`):** `doPost` → anahtar (`Script Properties: API_KEY`) → `LockService` kilidi → `cut_id`'ye göre upsert (varsa satırı güncelle, yoksa sona ekle; aynı pakette iki kez gelen tek satır). Sayfa `Kesimler`, ilk gönderimde başlık satırıyla oluşur. Metin sütunları `@` biçimli (Sheets tarihe/sayıya çevirmesin). Son sütun `metre` = length_cm/100 (rapor için; geri kurmada kullanılmaz). `doGet` sağlık kontrolü, veri döndürmez. Yanıt `{ok, upserted, version}` / `{ok:false, error}`. Yayın: Web uygulaması, "Ben" olarak yürüt, erişim "Herkes" (yazmak için anahtar şart).
- **Uygulama (Core/Sync):** `AppsScriptTransport` (HTTPS POST JSON, snake_case; ağ hatası/zaman aşımı/5xx → `Offline`; `ok:false`, HTML giriş sayfası → `Rejected` ve anlaşılır Türkçe mesaj), `OutboxRepository` (bekleyen kesimler: `GROUP BY cut_id`, kesimin GÜNCEL hâli gönderilir; `MarkSent` yalnızca okunan en büyük kuyruk numarasına kadar kapatır → gönderim sırasında gelen değişiklik kaybolmaz ve aynı turun sonraki paketinde gider), `SyncService` (200'lük paket, turda en fazla 25 paket, hata olunca durur; gönderilmiş 30 günden eski kuyruk satırlarını siler), `SyncWorker` (açılışta hemen, sonra 2 dk; kayıttan 5 sn sonra dürtme; hata beklemesi 30 sn→1→2→5→10 dk, beklemede dürtme yok sayılır; "Şimdi gönder" beklemeyi atlar).
- Ayarlar: `sheets_url`, `sheets_key` (`settings` tablosu, düz metin; depoya girmez). Boş adres/anahtar = gönderim kapalı. Adres https olmalı.
- Durum çubuğu metinleri: "Sheets gönderimi ayarlanmadı (Ayarlar)" (turuncu), "Sheets'e gönderiliyor…", "Sheets güncel · son gönderim HH:mm", "İnternet yok · HH:mm'de tekrar denenecek" (turuncu), "Sheets hatası: …" (turuncu).
- **Testler (FakeAppsScript HTTP işleyicisi, Code.gs mantığını taklit eder):** yanıt kaybolup aynı paket tekrar gidince tek satır, kesinti sonrası kurtarma ve deneme sayacı, paket ortasında kopma + sıra korunması, baskı+iptal aynı satırı günceller, gönderim sırasında değişiklik, yanlış anahtar, HTML giriş sayfası, 5xx, 450 kayıt → 200/200/50, 30 gün temizliği, bekleme süreleri, çalışan durumu.
- Arayüz testi (gerçek uygulama, geçici veri): ayarlanmamış → kayıtlar birikiyor; ulaşılamayan adres (`https://kumas-test.invalid`) → bağlantı testi anlaşılır hata, durum çubuğu "İnternet yok · …'de tekrar denenecek", internetsiz kayıt ve etiket çalışıyor, deneme sayacı ve hata kuyruğa yazılıyor.
- Yeni kısayol: **Ctrl+4 Ayarlar**. Ayarlar açılınca odak adres kutusunda (yoksa içerik çerçevesinde siyah odak kenarı görünüyordu).
- Arayüz testi betiği: bulunamayan öğede DURMALI (bir kez bulunamayan öğe yüzünden metin yanlış kutuya yazıldı; zararsızdı, geçici veritabanıydı).
- **Gerçek Google testi (2026-11-01) ✅** — kullanıcıyla birlikte, Claude in Chrome ile:
  - Test tablosu: **"Kumaş Kesimleri - TEST"** (kullanıcının Google Drive'ı), Apps Script projesi **"Kumaş Barkod"**, dağıtım "Kumaş Barkod v1 (TEST)", erişim Herkes, "Ben" olarak yürütülür. Adres ve anahtar YALNIZCA uygulamanın gerçek veritabanı ayarlarında (`%LOCALAPPDATA%\KumasBarkod\kumas.db` → `settings`) ve Script Properties'te; depoya/CLAUDE.md'ye yazılmaz.
  - Uçtan uca test (geçici veritabanı, `dotnet run gtest.cs` — Core'u `#:project` ile kullanır; dosya tabanlı betiklerde **`#:property PublishAot=false`** gerekli, yoksa reflection JSON kapalı): sağlık kontrolü veri döndürmüyor; yanlış anahtar reddedildi; 3 kesim gitti; aynı paket tekrar gönderildi → tekrar satır YOK; iptal + tekrar baskı aynı satırları güncelledi; kesinti (ulaşılamaz adres) sırasında 2 kesim birikti, bağlantı gelince bir kez gitti. Tabloda 5 satır, Türkçe karakter doğru, `metre` doğru.
  - Gerçek uygulama (boş gerçek veritabanı) ayarlandı: durum çubuğu "Sheets güncel · son gönderim 14:53".
  - Tarayıcı notları: Google izin ekranı açılır pencerede (Chrome eklentisi göremez → kullanıcı onayladı). Apps Script "Kopyala" düğmeleri panoyu değiştirir (bir kez anahtar yerine proje kimliği kaydedildi). Ajan sızdırma kuralı: gizli anahtarı web formuna ajan yazmaz → kullanıcı yapıştırdı.
  - **Fabrika kurulumunda (Adım 11):** yeni, temiz tablo + yeni anahtar; `Kesimler` sayfası korunmalı (KURULUM.md 6. adım) — test tablosunda yapılmadı.
- Uygulama ekran görüntüsü, uygulama arka plandayken `PrintWindow` (PW_RENDERFULLCONTENT=2) ile alınabilir; odak değişmez, tuş gönderilmez.
- Kullanıcı test sırasında gerçek uygulamada "Lot 31, 19 m" kaydı açtı (K1-000001); aynı ID test tablosundaki test satırının üzerine yazıldı — beklenen davranış (test tablosu ile geliştirme makinesi aynı ID aralığında).

| 9 | ✅ | (bu commit) | Günlük yerel yedek (Backup API, 30 gün), Sheets'ten geri kurma (Apps Script v2 `export`), hata günlüğü (`FileLog`, beklenmeyen hata yakalama), Ayarlar'da Yerel yedekler / Sheets'ten geri kur / Hata günlüğü. 228 test |

### Adım 9 notları
- **Yedek (`BackupService`):** `SqliteConnection.BackupDatabase` → `<veri>\backups\kumas-YYYY-MM-DD.db` (yerel tarih). Önce `.tmp`'ye yazılır, `journal_mode=DELETE` yapılır (yedek tek dosya; WAL ayarını kopyalıyordu), `integrity_check` → sonra adlandırılır. Açılışta ve saatte bir `EnsureDailyBackup` (günde bir). 30 günden eskiler silinir; klasördeki başka dosyalara dokunulmaz. Ayarlar: "Şimdi yedekle", "Yedek klasörünü aç". Yerelden geri dönüş elle (KURULUM.md).
- **Geri kurma (`RestoreService`):** yalnızca BOŞ veritabanına (işlem içinde tekrar kontrol). Tablo 2000'lik sayfalarla okunur, TÜM satırlar doğrulanır (ID = istasyon+sıra, durum, metre, lot, zaman biçimleri, iptal tutarlılığı, tekrar ID) → tek hata bile varsa hiçbir şey yazılmaz ve satır numarasıyla Türkçe hata. Tek işlemde yazılır; kuyruğa yazılmaz (veri zaten tabloda); sayaç = istasyon başına en büyük sıra + 1000 (999999'da tavan); `settings.restored_at`.
- **Apps Script v2:** `doPost` içinde `action: "export"` (anahtarlı) → `{ok, total, rows, version: 2}`, sütunlar başlık adına göre, boş hücre → null. Test tablosuna "Dağıtımları yönet → düzenle → Yeni sürüm" ile dağıtıldı (sürüm 2, adres aynı). v1 script'e okuma isteği gelirse uygulama "Apps Script'i son sürümle güncelleyin" der.
- `AppsScriptTransport`: ortak `PostAsync<T>`; `JsonContent.Create(body, body.GetType(), Json)`; `NumberHandling = AllowReadingFromString`.
- **Hata günlüğü (`FileLog`, Core/Diagnostics):** `<veri>\logs\kumas-YYYY-MM-DD.log`, `[BİLGİ]/[UYARI]/[HATA]`, 30 gün, yazılamazsa sessizce geçer. App: açılış/kapanış, `DispatcherUnhandledException` (günlüğe yaz + Türkçe uyarı + uygulama açık kalır), `TaskScheduler.UnobservedTaskException`, `AppDomain.UnhandledException`, veritabanı açılamazsa, yedek sonuçları, Sheets durum DEĞİŞİMLERİ (her yeniden deneme değil), geri kurma, baskı hataları (`PrintService` `ILog` alır).
- **Hata günlüğü sayesinde bulunan hata:** kapanışta `ServiceProvider.Dispose()` → `SyncWorker` yalnızca `IAsyncDisposable` olduğu için hata atıyordu (Adım 8'den beri, kapanışta sessizce). Yeni yakalayıcı uyarı penceresi açınca uygulama kapanmada asılı kaldı. Çözüm: `OnExit`'te `_services.DisposeAsync()`.
- Testler: geri kurma (tüm alanlar birebir, iptal+baskı, iki istasyon, sayaç +1000 ve sonraki ID `K1-001006`, tavan, kuyruğa yazmama, 2500 satır → 2 sayfa, dolu veritabanını reddetme, 6 çeşit bozuk hücre + tekrar satır → hiçbir şey yazılmaz, boş tablo, eski script, yanlış anahtar), yedek (WAL kontrol noktasından önce de son veri, günde bir, 30 gün temizliği, ilgisiz dosyalar, yedekten açılabilirlik), günlük (dosya/seviye/istisna, 30 gün, yazılamazsa atmaz). `InternalsVisibleTo KumasBarkod.Tests` (Core).
- **Gerçek Google testi:** v2 sağlık `version:2`; boş geçici veritabanına 5 kayıt (1 iptal) kuruldu, sayaç 1005, sonraki ID K1-001006, kuyruk boş, ikinci geri kurma reddedildi. Gerçek uygulamada (geçici veri) Ayarlar → geri kur → Geçmiş'te 5 kayıt; yedek ve günlük dosyaları oluştu; uygulama temiz kapandı (çıkış kodu 0).
- Yeni mesajlar: `BackupChangedMessage`, `DatabaseRestoredMessage`. Yeni yollar: `AppPaths.BackupDirectory`, `AppPaths.LogDirectory`.

## Şu anki durum

- **Adım 9 tamamlandı** (2026-11-01).
- **Karar (2026-11-01): ID 6 hane kalır** (seçenek a). Bir istasyon en fazla 999.999 ID üretir (günde 500 kesimle ~6,7 yıl); dolunca uygulama uyarır, Ayarlar'dan yeni istasyon kodu verilir. 7 haneye çıkarma önerilmişti, kullanıcı istemedi — tekrar önerme.
- **Kalan adımlar (2):** 10 gerçek yazıcı (satın alma, şablon, etiket/kumaş denemesi — donanım gerekir), 11 kurulum paketi (self-contained + Inno Setup) + fabrika pilotu.
- **Sıradaki:** Adım 10 yazıcı gelmeden yapılabilecek kısımlar: Windows yazıcı kuyruğuna ham ZPL gönderen `RawWindowsPrinter` (winspool), Ayarlar'da yazıcı seçimi; ya da önce Adım 11 kurulum paketi. Kullanıcıya sorulacak.
- Gerçek USB okuyucu testi hâlâ açık (Adım 6).

## Açık konular / riskler

- **Türkçe karakterler etikette:** Zebra yerleşik font 0 (`^A0`) ğ/ş/ı/İ'yi her firmware'de basmayabilir. Adım 10'da gerçek yazıcıda denenmeli; gerekirse yazıcıya Türkçe destekli font yüklenir.
- **Etiket boyutu** (50×30 mm, 203 dpi) varsayım; yazıcı/etiket seçilince şablon güncellenecek (Adım 10).
- **Code128 otomatik mod (A)** eski Zebra firmware'de yoksa `^BCN,80,N,N,N,N` + elle alt küme kullanılır.
- **Veritabanı konumu** kullanıcı klasöründe; fabrika bilgisayarında birden çok Windows kullanıcısı olacaksa ortak klasöre (ProgramData) taşınmalı.
- Etiket önizlemesi (ekranda görüntü) yok; istenirse eklenebilir. labelary.com ZPL'yi görüntüler ama veriyi dışarı gönderir.
