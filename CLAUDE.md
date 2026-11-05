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
- Uygulama: `src\KumasBarkod.App\bin\Debug\net10.0-windows\KumasBarkod.exe` (Adım 11'den beri `AssemblyName=KumasBarkod`; süreç adı `KumasBarkod`, eskiden `KumasBarkod.App`).
- Yayın: `dotnet publish src\KumasBarkod.App -c Release -o artifacts\publish` (self-contained win-x64, ReadyToRun, ~144 MB). Kurulum paketi: `pwsh tools\installer\build-installer.ps1` → `artifacts\installer\KumasBarkod-Kurulum-<sürüm>.exe` (Inno Setup 6 gerekir). `artifacts/` git'e girmez.
- Bu ortamda `Remove-Item -Recurse` bazen "system path '/'" diye engelleniyor → silmeden üzerine yaz.
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
src/KumasBarkod.Core     (net10.0)  iş mantığı, veri, yazdırma, senkron — arayüzden bağımsız, test edilir
  Data/        SqliteConnectionFactory, Migrations (şema), DatabaseMigrator, SettingsRepository, StationCounters,
               CutRepository (+ Search/CutQuery), BackupService
  Domain/      Cut, CutStatus, CutIds (ID/barkod biçimi, TryParseScan), Stations, Length, Timestamps, VoidReasons
  Services/    CutService (oluşturma, iptal), PrintService (baskı + baskı kaydı + günlük)
  Printing/    LabelData, ILabelTemplate, ILabelPrinter, ZplLabelTemplate, FileLabelPrinter, Code128 (ZXing)
  Scanning/    ScanBurstDetector (okuyucu hız + biçim tespiti)
  Sync/        AppsScriptTransport, OutboxRepository, SyncService, SyncWorker, RestoreService, modeller
  Diagnostics/ ILog, FileLog, NullLog
src/KumasBarkod.App      (net10.0-windows, WPF; çıktı adı KumasBarkod.exe)
  App.xaml(.cs)  DI, kültür tr-TR, günlük + beklenmeyen hata yakalama, göç, gönderici, günlük yedek zamanlayıcı
  AppPaths.cs    veri klasörü (KUMASBARKOD_DATA_DIR ile değişir), db, labels, backups, logs
  Messages.cs    CutChanged, OpenCut, StartNewCut, BackupChanged, DatabaseRestored, SettingsChanged
  Scanning/      ScanCapture (pencere düzeyinde okutma yakalama)
  Printing/      ScreenOnlyPrinter, PagePrinter (Windows yazıcı/PDF), SelectablePrinter (ayara göre)
  Controls/      LabelPreview (50×30 mm etiket çizimi, gerçek Code128)
  Assets/        app.ico
  Themes/Styles.xaml, Converters.cs
  ViewModels/    MainWindow, CutEntry, Scan, History, Settings
  Views/         MainWindow (FluentWindow + NavigationView + durum çubuğu), Pages/ (CutEntry, Scan, History, Settings)
tests/KumasBarkod.Tests  (xUnit, 250 test) Data/, Domain/, Services/, Printing/, Scanning/, Sync/ (FakeAppsScript)
tools/sheets/            Code.gs (Apps Script v3), Code.test.js (node --test), KURULUM.md
tools/installer/         KumasBarkod.iss, build-installer.ps1, generate-icon.ps1
docs/                    SUNUM.md (donanımsız sunum akışı), PILOT.md (fabrika pilotu)
```

**Paketler:** WPF-UI 4.3.0 (Fluent), CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 10.0.14, Microsoft.Data.Sqlite 10.0.14, Dapper 2.1.89, ZXing.Net 0.16.11 (yalnızca önizleme barkodu).

### Önemli tasarım kararları
- **Veritabanı:** SQLite, WAL + `synchronous=FULL` + `foreign_keys` + `busy_timeout=5000`. Şema sürümü `PRAGMA user_version`; her göç kendi işleminde. **Yayınlanmış göç asla değiştirilmez, yeni sürüm eklenir.** Uygulamadan yeni şemalı veritabanı açılmaz.
- **Şema v1:** `cuts` (STRICT, CHECK'ler: ID = istasyon + '-' + 6 hane, lot kırpılmış/boş değil, length_cm > 0, durum active/voided, iptal bilgisi tutarlılığı), `sync_outbox` (FK → cuts), `station_counter`, `settings` (anahtar/değer).
- **ID:** `K1-000123` saklanır/görünür; barkodda tiresiz `K1000123` (Türkçe klavye tire sorunu). İstasyon kodu `^[A-Z][A-Z0-9]$`, varsayılan `K1`. Sıra 1–999999.
- **Kayıt:** `CutService.CreateCut` tek işlemde (BEGIN IMMEDIATE) sayaç + cuts + sync_outbox. Hatalı giriş sıra numarası harcamaz.
- **Uzunluk:** personel tam sayı **cm** girer (`Length.TryParseCentimeters`), virgül/nokta reddedilir; veritabanı zaten cm tutar. Canlı ipucu "= 25,50 m" bilerek kalır (metre alışkanlığıyla yazılan "25", 25 cm'dir). Etiket, listeler, Geçmiş toplamları ve tablodaki Metre sütunu **metre gösterir**. Üst sınır Ayarlar'dan cm, varsayılan 30000 (`max_length_cm`).
- **Lot:** kırpılır, en fazla 50 karakter, büyük/küçük harf korunur (Türkçe i/İ).
- **Zaman:** UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` (sabit uzunluk, metin sıralaması = zaman sırası). `TimeProvider` enjekte edilir (testte sabit saat).
- **Giriş ekranı:** açılır liste (ComboBox) KULLANILMAZ — odak sorunları çıkardı. Lot = TextBox; ↑↓ son lotlarda gezer; son lotlar altta düğme (chip) olarak görünür.
- **Kısayollar:** sayfa kısayolları code-behind'da `AddHandler(PreviewKeyDownEvent, ..., handledEventsToo: true)` ile; KeyBinding kullanma (F5 üst denetimde yutuluyor). Giriş ekranı: Enter, F2 (lot), ↑↓ (son lotlar), F5 (sıradaki parça), F6 (bozulan etiket), Esc (uzunluğu temizle / onaydan vazgeç). Okutma: F8 (iptal), Esc. Geçmiş: F5 (aynı ID ile baskı).
- **Odak:** Tuşa bağlı odak değişimi anında yapılır (`FocusNow`); ertelenirse hızlı yazılan karakter yanlış kutuya düşer.
- **Ekran kısayolları (MainWindow):** Ctrl+1 Kesim Girişi, Ctrl+2 Okutma, Ctrl+3 Geçmiş, Ctrl+4 Ayarlar. Okutma her ekranda yakalanır (bkz. Adım 6 notları).
- **Sayfalar:** her `Page`'de `ScrollViewer.CanContentScroll="False"` (WPF-UI'nin dış kaydırma alanını kapatır; yoksa sanallaştırma bozulur). Kaydırma sayfanın içinde yönetilir.
- **Arayüz testi düğmeleri:** odaklanamayan düğmeler (Focusable=False) testte UI Automation `InvokePattern` ile, ComboBox `ExpandCollapsePattern`/`SelectionItemPattern` ile kullanılır.
- **Baskı:** Kayıt önce yazılır, sonra basılır. Başarılı baskı `print_count+1`, `last_printed_at` yazar ve kuyruğa `upsert` ekler. Baskı hatası kaydı bozmaz; F6 aynı ID ile basar. İptal edilmiş kesim basılmaz.
- **Etiket (ZPL):** 50×30 mm, 203 dpi, `^CI28` (UTF-8), Code128 `^BCN,80,N,N,N,A` (otomatik mod), lot `^FH_` ile kaçışlı (`^ ~ _`). Yazıcı Ayarlar'dan seçilir (`SelectablePrinter`): **Gösterim (yalnızca ekran) varsayılan**, Kağıt (Windows yazıcısı/PDF), Test (`FileLabelPrinter` → `<veri>\labels\*.zpl`). Fiziksel olmayan yazıcı durum çubuğunda turuncu görünür. Etiket yazıcısına ham ZPL henüz yok (Adım 10 kalanı).
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
- `CutService.VoidCut`: sebep zorunlu (≤ 200, kontrol karakteri yok), zaten iptal → hata, kuyruğa upsert. İptal edilmiş kayıt basılmaz. (2026-11-02'den beri arayüzde serbest metin yok, sabit liste — bkz. İptal sebepleri notları.)
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
- Kullanıcı test sırasında gerçek uygulamada "Lot 31, 19 m" kaydı açtı (K1-000001); aynı ID test tablosundaki test satırının üzerine yazıldı. **Bu bir hataydı** — 2026-11-02'de düzeltildi (bkz. ID benzersizliği notları).

| 9 | ✅ | `4463ea7` | Günlük yerel yedek (Backup API, 30 gün), Sheets'ten geri kurma (Apps Script v2 `export`), hata günlüğü (`FileLog`, beklenmeyen hata yakalama), Ayarlar'da Yerel yedekler / Sheets'ten geri kur / Hata günlüğü. 228 test |

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

| 10a | ✅ | `099f048` | **Gösterim modu (donanımsız sunum):** Code128 üretici (ZXing.Net), ekranda etiket önizlemesi (`LabelPreview`), "Okut (gösterim)", yazıcı seçimi (Gösterim / Kağıt-PDF / Test dosyası), sunum akışı `docs/SUNUM.md`. 236 test |

### Adım 10a notları (gösterim modu — kullanıcı isteği 2026-11-01)
- Amaç: barkod yazıcı/okuyucu almadan fabrikada uçtan uca sunum. Okuma yolu: Okutma ekranına elle ID (zaten vardı) + "Okut (gösterim)" düğmesi (`OpenCutMessage`, gerçek okuyucunun sonucu ile aynı yol). İsteğe bağlı: telefonu klavye gibi okuyucu yapan uygulama.
- **`Code128` (Core/Printing):** ZXing.Net 0.16.11 `Code128Writer` (margin 0) → modül dizisi. Yalnızca ekran/kağıt önizlemesi için; etiket yazıcısı barkodu kendi ZPL `^BC` komutuyla basar. Testler: üretilen barkod bağımsız `Code128Reader` ile geri okunuyor (K1000123 vb.).
- **`LabelPreview` (App/Controls):** 50×30 mm etiket, `ZplLabelTemplate` sabitleriyle aynı yerleşim (`BarcodeX/Y`, `IdY`, `LotLine()`, `DateText()` ZPL şablonundan herkese açık yapıldı; ZPL referans testi değişmedi). **Ekranda barkod modülleri tam piksele (≥2 px) oturtulur** (`SnapBarcodeToPixels`): orantılı çizimde ~1,6 px/modül → genişlikler bozuluyor ve barkod OKUNMUYORDU (ekran görüntüsünden doğrulandı). Yazdırırken `SnapBarcodeToPixels=false` (gerçek boyut, yazıcı çözünürlüğü).
- **Yazıcılar (App/Printing/Printers.cs):** `ScreenOnlyPrinter` (Gösterim, varsayılan), `PagePrinter` (normal Windows yazıcısı/PDF; `PrintDialog.PrintVisual`, arayüz iş parçacığında `Dispatcher.Invoke`; etiket gerçek boyutta, 15 mm kenar), `FileLabelPrinter` (test). `SelectablePrinter` ayara göre yönlendirir (`settings.printer`: `screen` | `file` | `page:<ad>`), değişince `Changed` → durum çubuğu. Etiket yazıcısına ham ZPL (Zebra) Adım 10'un kalanında eklenecek.
- Kesim Girişi sağ üst: "Son etiket" kartı (önizleme + Okut (gösterim)); Okutma ekranı ayrıntı kartında etiket önizlemesi (`CutDetails.Label`). Ayarlar: Yazıcı kartı (ComboBox öğelerinde `AutomationProperties.Name` = başlık).
- **Doğrulama:** ekran görüntülerindeki barkod bağımsız okuyucuyla okundu (K1000002, K1000001); "Microsoft Print to PDF" çıktısı (80×60 mm sayfa, 50×30 etiket) Windows PDF motoruyla 300 dpi'ye çevrilip okundu: K1000004.
- Test betikleri: `decode.cs` (PNG'den Code128 okur), `pdfdecode.cs` (`#:property TargetFramework=net10.0-windows10.0.19041.0`, `Windows.Data.Pdf`). PDF "Farklı kaydet" penceresi uygulama penceresinin ALT öğesi (`#32770`), UI Automation'a dosya adı kutusunu vermiyor → pencere öne alınıp (her adımda ön planda mı kontrolü) dosya adı kutusuna tıklanıp yazıldı. Bir denemede terminal öne geçti; betik tuş göndermeden durdu.

### Claude kullanım ayarı (2026-11-01, kullanıcı isteği)
- `/usage`: claude-in-chrome %18 + beceri %12. `~/.claude.json` → `claudeInChromeDefaultEnabled: false` (Chrome yalnızca `/chrome` ile açılır). Hafıza notu `browser-usage`: tarayıcıyı az kullan, ekran görüntüsü yerine `find`/`get_page_text`, ölçek ≤0,5, 2 başarısız denemede dur.

| 11 | ✅ paket (pilot fabrikada) | `2900e52` + `1e144bd` | Uygulama adı/sürümü (1.0.0) + simge, istasyon kodu Ayarlar'dan değiştirilebilir, self-contained yayın, Inno Setup betiği + derleme betiği, pilot kontrol listesi |

### Adım 11 notları
- **Simge:** `tools/installer/generate-icon.ps1` → `src/KumasBarkod.App/Assets/app.ico` (`tools/icon/` klasörü KULLANILMAZ: `.gitignore`'daki macOS `Icon` satırı Windows'ta "icon" klasörünü de yok sayıyor) (turkuaz kare + beyaz barkod, 16–256 px PNG'li ICO). Exe simgesi (`ApplicationIcon`) + pencere `Icon` (pack URI).
- **csproj (App):** `AssemblyName=KumasBarkod`, `Version=1.0.0`, `Product`, `AssemblyTitle`; Release'te `RuntimeIdentifier=win-x64`, `SelfContained`, `PublishReadyToRun`, `SatelliteResourceLanguages=tr;en`. Yayınlanan uygulama geçici veriyle denendi: açılış ~5,3 sn (ilk açılış), yedek + günlük, temiz kapanış.
- **İstasyon kodu** artık Ayarlar → Kesim'den değiştirilebilir (`SaveStationCode`, `SettingsChangedMessage` → durum çubuğu). Eski kayıtlar aynen kalır; yeni kod kendi sayacıyla başlar.
- **Inno Setup (`tools/installer/KumasBarkod.iss`):** AppId `{B963423C-EC31-47F5-AD11-B7CF6D89F797}` (DEĞİŞTİRME: güncellemeler bununla eşleşir), kullanıcı başına kurulum (`PrivilegesRequired=lowest`, yönetici isterse tüm kullanıcılar), x64, Türkçe sihirbaz, masaüstü kısayolu seçeneği, güncellemede program klasörü temizlenir (`InstallDelete`), çalışan uygulama kapatılır, kaldırma VERİYİ SİLMEZ (mesajda söylenir). `build-installer.ps1`: testler → yayın → ISCC (`/DAppVersion` csproj'dan).
- **Pilot:** `docs/PILOT.md` (kurulum, ayarlar, deneme kaydı, bir vardiya kağıtla paralel, vardiya sonu karşılaştırma tablosu).
- **Inno Setup 6.7.3** kullanıcı onayıyla winget ile kuruldu: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` (kullanıcı başına kurulum).
- **Paket testi (2026-11-01):** `build-installer.ps1` → 236 test + yayın + ISCC → `KumasBarkod-Kurulum-1.0.0.exe` **46,4 MB**. Sessiz kurulum (`/VERYSILENT /CURRENTUSER /DIR=… /MERGETASKS="!desktopicon"`) 12 sn, çıkış 0, 271 dosya, Programlar listesinde "Kumaş Barkod 1.0.0", Başlat menüsü kısayolu. Kurulu uygulama 3,2 sn'de açıldı, temiz kapandı. Sessiz kaldırma: program klasörü, kayıt defteri girişi ve kısayol silindi; **veri (veritabanı, yedek, günlük) korundu**; gerçek veri klasörüne dokunulmadı.
- **AÇIK (fabrikada):** temiz bilgisayara kurulum + bir vardiya kağıtla paralel pilot (`docs/PILOT.md`). Kod imzası yok: Windows SmartScreen "tanınmayan uygulama" uyarısı verebilir → "Ek bilgi → Yine de çalıştır".

| ID düzeltmesi | ✅ (gerçek Google testi geçti) | `919d72f` `02791a3` `fe9726d` | Sheets'te aynı ID'li farklı kesimin üzerine yazılması engellendi; satırlar kayıt zamanına göre sıralı; yeni kurulum sayaçları tablodan öğrenir. Apps Script **v4**. 245 test + 8 Code.gs testi |

### ID benzersizliği notları (kullanıcı hatası bildirimi, 2026-11-02)
- **Sorun:** ID = istasyon + YEREL sayaç. Yeni/boş veritabanı (yeniden kurulum, test veritabanı, aynı kodlu 2. PC) yine `K1-000001`'den başlıyordu; Code.gs yalnızca `cut_id`'ye bakıp upsert yaptığı için eski, farklı kesimin satırını eziyordu ve yeni kesim tablonun ortasında kalıyordu (Adım 8 notundaki "Lot 31" olayı buydu — o zaman "beklenen" denmişti, YANLIŞTI).
- **Code.gs v3:** kesim kimliği = `cut_id` + `created_at`. Aynı ID farklı `created_at` ile gelirse satır yazılmaz, yanıtta `conflicts` döner (aynı paket içindeki çift de). Yeni satırlar `created_at`'e göre sıralı eklenir; eklenen satır tablodaki son kayıttan eskiyse veri alanı `created_at`, `cut_id`'ye göre yeniden sıralanır. Yanıtta `max_sequences` (istasyon → en büyük sıra). `created_at` boşsa paket reddedilir.
- **Uygulama:** `AppsScriptTransport.SendAsync` → `SendResult(Conflicts, MaxSequences)`; script sürümü < 3 ise `Rejected` ("Apps Script eski sürüm") → gönderim durur, kayıtlar kuyrukta bekler. `StationCounters.RaiseTo` (Core/Data) sayacı yalnızca YÜKSELTİR (999999 tavan, geçersiz kod yok sayılır). `SyncService`: oturumda bir adrese ilk gidişte (ve hata sonrası tekrar) önce BOŞ paket gönderir → script sürümü satır yazılmadan doğrulanır (eski v2 script'e hiç satır gitmez; ilk sürümde sürüm kontrolü yanıttan sonraydı ve v2 satırı yine ezebiliyordu — düzeltildi) ve sayaçlar öğrenilir; her gönderim yanıtında da sayaçlar yükseltilir. Sonuç: her açılışta 1 ek boş istek. Çakışan kesimler kuyrukta kalır (`MarkFailed`), çakışmayanlar `MarkSent`; tur durur, hata durum çubuğunda (tam metin ipucunda).
- **Sınır (çevrimdışı tasarımın doğası):** Sheets ayarlanmadan önce yapılan kesimler veya aynı istasyon kodlu iki PC hâlâ aynı numarayı üretebilir; artık tabloda EZİLMEZ ve görünür hata verir. Çözüm her PC'ye ayrı istasyon kodu (KURULUM.md'ye yazıldı).
- **Testler:** `IdUniquenessTests` (9 test, iki/üç veritabanı tek sahte tabloya yazar: bildirilen senaryo, çakışma ezilmez + kuyrukta kalır + sonraki ID çakışmaz, kendi satırını güncelleme, istasyonlar arası zaman sırası, 450 kesim sırası, eski script reddi, sayaç öğrenme boşta tek istek, `RaiseTo` kuralları). `FakeAppsScript` v3 gibi davranır (`Version` ayarlanabilir, `AddForeignRow`). **Gerçek Code.gs testi:** `node --test tools/sheets/Code.test.js` (sahte SpreadsheetApp ile; 6 test).
- - Gerçek Google testi betiği hazır: scratchpad `gtest3\gtest3.cs` (oturuma özel; adres/anahtar gerçek db'den salt okunur, yalnızca geçici db'ye yazar, T9 istasyonlu 3 test satırı ekler, v3 değilse hiçbir şey yazmadan durur).
- `Search_100k_rows_under_one_second` tam koşuda bir kez 1 sn sınırını aştı (CPU yükü), tek başına ve sonraki 4 tam koşuda geçti — kararsız olabilir.
- **Code.gs v4:** tablo sırasızsa (eski hatadan kalan: üzerine yazılmış K1-000001..3 üstte, eski test satırları K1-000004..5 altta) her POST'ta — uygulamanın açılıştaki boş paketi dahil — `created_at`, `cut_id`'ye göre yeniden sıralar. Uygulama v3+ ister.
- **Gerçek Google testi (2026-11-02) ✅:** Chrome araçları bu oturumda gelmedi (`/chrome` + yeniden başlatma denendi); kullanıcı Code.gs'yi panodan yapıştırıp dağıttı (v3, sonra v4). `gtest3.cs` 19/19: sağlık v4, yeni kurulum sayacı tablodan öğrendi (T9 3 → T9-000004), çakışma reddedildi + tablo korundu + kuyrukta kaldı, ikinci kurulumun sonraki ID'si çakışmadı, iptal aynı satır, ID'ler benzersiz, tablo kayıt zamanına göre sıralı, en yeni en altta, önceki satırlar birebir aynı. Test tablosunda T9-000001..6 test satırları var.
- Gerçek uygulama v4'e bağlandı (hata yok), sayaç K1 = 5 → sonraki gerçek kesim **K1-000006**; bekleyen 0. Tablodaki K1-000004/5 (TEST-LOT-B) gerçek veritabanında yok — test satırı, fabrika tablosu yeni olacak.
- Panoya kopyalama: Windows PowerShell 5 konsolu Türkçe karakteri bozuk GÖSTERİR; pwsh ile `Get-Clipboard` dosyayla karşılaştırıldı, içerik doğruydu.

| Sürüm 1.0.1 | ✅ yayında | `31d815d` (etiket `v1.0.1`) | GitHub Release + sabit indirme linki |
| Sürüm 1.0.2 | ✅ yayında (en yeni) | `6908af3` (etiket `v1.0.2`) | Sabit iptal sebepleri. 2026-11-02 |

### Sürüm yayınlama (GitHub Releases)
- **İndirme linki (her zaman en yeni sürüm):** <https://github.com/bugraakaratas/Barkod-Uygulamasi/releases/latest/download/KumasBarkod-Kurulum.exe>. Dosya her sürümde **aynı adla** (`KumasBarkod-Kurulum.exe`) yüklenmeli, yoksa link kırılır.
- **Depo HERKESE AÇIK** — kullanıcıya soruldu (2026-11-02), "herkese açık kalsın" dedi: link herkes için, GitHub hesabı olmadan çalışır.
- `gh` CLI kurulu DEĞİL. Release, GitHub REST API ile yapıldı; anahtar `git credential fill` (Git Credential Manager) ile alındı, ekrana/dosyaya yazılmadı. Adımlar: `<Version>` artır → `build-installer.ps1` → commit + push → `POST /repos/.../releases` (`tag_name` vX.Y.Z, `target_commitish` HEAD, `make_latest`) → `uploads.github.com/...?name=KumasBarkod-Kurulum.exe` → linkten indirip SHA-256 karşılaştır.
- 1.0.1 doğrulandı: anonim indirme, dosya özgün ile birebir aynı (46,5 MB).
- 1.0.2 (2026-11-02) doğrulandı: sabit link 1.0.2'yi veriyor, SHA-256 özgünle aynı (`57C4FEA3…DFD1`, 48.720.174 bayt). Release notu Türkçe (değişiklikler + kurulum). İlk `git push` bu kez Claude'un kendi komutuyla sorunsuz çalıştı.
- csproj'u PowerShell ile düzenlerken `-Encoding utf8BOM` kullan (dosya BOM'lu; `utf8` BOM'u siliyor).
- Hız testi (`CutSearchTests`) artık paralel olmayan koleksiyonda (`TimingCollection`): paralel koşuda 1 sn sınırını aşıp kurulum derlemesini durdurmuştu.

| İptal sebepleri | ✅ | `eb9d025` | Okutma ekranında serbest metin yerine sabit 4 sebep (tek seçim, 1–4 / ↑↓ / Enter). 247 test |
| Revize: cm girişi | ✅ (henüz yayında değil) | `e5e3ae8` | Personel tam sayı cm girer. 2026-11-05 |
| Revize: fareyle lot | ✅ (henüz yayında değil) | `578f9f7` | Son lot önerileri tıklanarak seçilir |
| Revize: Okutma sadeleşti | ✅ (henüz yayında değil) | `75da294`, `b69e608` | "Yerine yeni kayıt" (F9) ve "Etiketi tekrar bas" kalktı; yalnızca İptal et |
| Revize: F5/F6 | ✅ (henüz yayında değil) | `067bdcc` | Sıradaki parça ve bozulan etiket ayrıldı. 250 test |

### İptal sebepleri notları (kullanıcı isteği, 2026-11-02)
- **Karar:** tek seçim, liste sabit (Ayarlar'dan düzenlenmez), "Diğer"/ek not YOK. Sebepler `Core/Domain/VoidReasons.cs`: 1 Yanlış metre girildi · 2 Yanlış lot girildi · 3 Çift kayıt · 4 Kesim yapılmadı / vazgeçildi. "Etiket bozuk/okunmuyor" bilerek yok (çözüm Kesim Girişi'nde F6 veya Geçmiş'ten aynı ID ile baskı, iptal değil). **Metinleri değiştirme** — raporlardaki sayım bölünür; yeni sebep eklenecekse sona ekle.
- Seçilen metin `void_reason`'a aynen yazılır → şema, Sheets, geri kurma değişmedi; eski serbest metinli iptaller geçerli. `VoidCut` hâlâ serbest metin kabul eder (liste kuralı arayüzde).
- Arayüz: F8 → panel seçeneklerle açılır, seçim boş; odak `VoidPanel` kartında (seçenekler `Focusable=False`), panel `BringIntoView` ile ekrana kaydırılır (1366×768'de onay düğmeleri altta kalıyordu). Tuşlar sayfa işleyicisinde, yalnızca panel açık + okutma kutusunda odak yok + değiştirici tuş yok + olay işlenmemişse: 1–4 (NumPad dahil) seçer, ↑↓ kaydırır, Enter onaylar, Esc vazgeçer. Seçimsiz Enter → panel içinde kırmızı uyarı (üstteki InfoBar ekran dışında kalıyordu).
- **Rakam tuşları `Handled` işaretlenmez:** işaretlenirse TextInput üretilmez ve panel açıkken yapılan okutma `ScanCapture`'a ulaşmaz. Okutmanın Enter'ı `ScanCapture`'da işlenir ve `Open` eşzamanlı paneli kapatır → iptal tetiklenmez. Ctrl+1–4 ekran kısayolları sebep seçmez.
- Arayüz testi (geçici veri, SendKeys, 2 koşu): seçimsiz Enter iptal etmiyor + uyarı görünüyor; 2 + ↓ → "Çift kayıt" iptali; seç + Esc → iptal yok; tekrar F8 seçim boş; panel açıkken hızlı okutma K1-000003'ü açtı, iptal etmedi; 1 + Enter → "Yanlış metre girildi". Veritabanı doğrulandı.
- Test betiği notu: Windows PowerShell 5 BOM'suz UTF-8 betiği bozuk okuyor → `pwsh -STA` kullan; betikte `Type` adlı fonksiyon yazma (`Get-Content` takma adı).

### F5/F6 notları (kullanıcı isteği, 2026-11-05)
- **Sorun:** "Son etiketi tekrar bas" lot ve uzunluk girmeden etiket verdiği için personel onu sıradaki parça için de kullanıyordu; aynı ID iki parçaya gidiyordu, veri eksik kalıyordu. **Tasarım ilkesi: doğru yol yanlış yoldan kolay olmalı.** "Tekrar bas" sözü Kesim Girişi'nde F5 için kullanılmaz.
- **F5 Sıradaki parça** (yeşil düğme, giriş kartı): son kaydın lotu ve uzunluğuyla YENİ kesim + etiket (`CutService.CreateNextPiece`). Kutulardaki yazılar kullanılmaz, değişmez.
- **F6 Bozulan etiket** ("Son etiket" kartı): aynı ID, satır içi onay ister (Enter bas, Esc vazgeç; panelde F5 sıradaki parçaya geçer). Başka yere tıklanınca veya ekran değişince panel kapanır. Daha eski etiketler Geçmiş'ten basılır.
- **Kurallar:** kaynak bu oturumun son kaydıdır, yeni açılışta ikisi de pasif. Son kayıt iptal edildiyse ikisi de pasif, gerekçe düğme altında (Okutma'daki iptal `CutChanged` ile yansır). `print_count = 0` ise F5 yeni kayıt açmaz ("Önce <ID> etiketini basın (F6)"), F6 onaysız basar: etiketsiz ama metresi toplama giren kayıt kalmasın.
- **Kilitler:** F5'te `e.IsRepeat` yok sayılır (basılı tutmak çok kayıt açmasın). `RunExclusiveAsync`: Enter ve F5 farklı komut olduğu için ortak kilit gerekir.
- **Fareyle lot:** `ListBoxItem` `Focusable=False` olduğu için ListBox fare seçimi yapmıyordu (öğe önce odak ister); tıklanan öğe `ContainerFromElement` ile bulunur.
- **Arayüz testi (2026-11-05, geçici veri, SendKeys + gerçek fare):** 24/24: cm ve virgül reddi, F5 sonraki ID, F5 basılı tek kayıt, F6 Esc/Enter, panelde F5, fareyle lot, baskı hatası (labels klasörü dosyayla değiştirilerek) F5 engeli + F6 onaysız, Okutma düğmeleri, iptal sonrası F5/F6 pasif. Son test betiği scratchpad'de `uitest.ps1` (oturuma özel).
- 1366×768'de "Son kayıtlar" yaklaşık 3 satıra düştü; F6 paneli açıkken liste geçici olarak kapanır.

## ⚠️ EN SON YAPILACAK — Sheets (2026-11-05, kullanıcı isteği)

Revizeler bu iki iş yapılmadan **tamamlandı sayılmaz**. Diğer revizeler bitince kullanıcıya hatırlat, onay alıp başla.

- [ ] **Sütun adları Türkçe:** görünen başlıklar Türkçe; iç anahtarlar ve JSON sözleşmesi İngilizce kalır. Geri kurma başlık adıyla okuduğu için hem eski hem yeni başlıkları tanımalı.
- [ ] **En yeni kayıt en üstte:** yeni satır başlığın hemen altına girer, sıralama yeniden eskiye. Amaç: 4 ay sonra binlerce satırda tabloya bakan kişi en alta inmesin. Dikkat: Code.gs bugün eskiden yeniye sıralıyor (yeni satır en altta, araya girme, eski tablo düzeltme); `exportRows`/geri kurma ve `Code.test.js`, `FakeAppsScript` bu sıraya göre gözden geçirilmeli.

Code.gs değişince kullanıcı script'i yeniden dağıtmalı.

## Şu anki durum

- **Revizeler (2026-11-05):** cm girişi, fareyle lot, Okutma sadeleştirme, F5/F6 tamam ve test edildi, **GitHub'a gönderildi** (2026-11-06). **Sürüm 1.0.3 bu bilgisayara kuruldu, kurulum dosyası masaüstünde; GitHub Release YOK:** sabit indirme linki hâlâ 1.0.2 verir, Release kullanıcı isteyince yapılır. **Revizeler tamamlanmış sayılmaz:** yukarıdaki iki Sheets işi açık.

- **Adım 11 kurulum paketi tamamlandı** (2026-11-01): `KumasBarkod-Kurulum-1.0.0.exe` üretildi ve kur/çalıştır/kaldır testi geçti. Adım 9 ve Adım 10'un donanımsız kısmı (gösterim modu) da tamam. Sunum: `docs/SUNUM.md`, pilot: `docs/PILOT.md`.
- **Karar (2026-11-01): ID 6 hane kalır** (seçenek a). Bir istasyon en fazla 999.999 ID üretir (günde 500 kesimle ~6,7 yıl); dolunca uygulama uyarır, Ayarlar'dan yeni istasyon kodu verilir. 7 haneye çıkarma önerilmişti, kullanıcı istemedi — tekrar önerme.
- **Kalan (donanım/fabrika gerektiren):** Adım 10'un kalanı (etiket yazıcısı seçimi/satın alma, etiket yazıcısına ham ZPL — `RawWindowsPrinter`/winspool, şablonu gerçek etikete uydurma, Türkçe karakter ve kumaş denemesi); fabrika pilotu (temiz PC + bir vardiya kağıtla paralel). Fabrika için yeni Google tablosu + anahtar.
- **2026-11-02:** ID benzersizliği düzeltmesi tamamlandı ve gerçek Google ile doğrulandı (test tablosunda script v4). Fabrika tablosu kurulurken v4 Code.gs kullanılmalı.
- **Sıradaki:** Kullanıcıya sorulacak. Seçenek: yazıcı modeli belli olmadan da yazılabilecek ham ZPL gönderimi (winspool) — gerçek yazıcıda denenene kadar "denenmedi" olarak işaretlenir.
- Gerçek USB okuyucu testi hâlâ açık (Adım 6) — gösterim modunda elle giriş/"Okut (gösterim)" ile ikame edildi.
- **GitHub:** 2026-11-02 itibarıyla her şey gönderildi; **v1.0.2 yayında** (Releases, sabit indirme linki).
- **2026-11-02:** İptal sebebi sabit 4 seçenekli liste oldu (bkz. İptal sebepleri notları), 1.0.2 olarak yayınlandı.
- **Donanım araştırması (2026-11-02, yalnızca öneri, satın alma yok):** Kullanıcının fikri = mevcut ofis PC + Cat6 KVM uzatıcı (HDMI+USB) ile makine başına monitör + yazıcı. Önerilen = **makine başına ayrı mini PC (N100, 16/512, Win11 Pro) + 22" monitör + Zebra ZD220t (USB, yerel ZPL) + Zebra DS2208 + 850 VA UPS**, ~35 bin TL. Uzatıcı yolu ~34 bin TL ama ofis PC'si yalnızca bu işe ayrılmalı (aynı masaüstü iki ekranda), ucuz KVM uzatıcılar USB yazıcıyı genelde taşımaz → Ethernet'li yazıcı (ZD421t) gerekir. Endüstriyel paket (IP65 fansız dokunmatik panel PC + ZD421t) ~73–81 bin TL. Notlar: ZD220t 74 m ribon alır (TSC TE200 300 m alır ama ZPL emülasyonu denenmeli); kuşe etiket kumaşta tutmayabilir → sentetik etiket denenmeli; yedekler aynı diskte → USB belleğe kopyalama ileride eklenebilir.

## Rafa kalkan: Yönetici (izleme) modu — 2026-11-02
- **Son karar (kullanıcı):** şimdilik takip **yalnızca Google Sheets'ten** yapılacak, KOD YAZILMAYACAK. Ayrı yönetici paneli istenmedi. Kullanıcı tekrar istemedikçe önerme.
- Önkoşul (zaten geçerli): her makineye ayrı istasyon kodu; hepsi aynı Sheets adresi + anahtarı.
- İleride istenirse önerilen yol: Geçmiş ekranına "Tüm makineler" seçeneği (yalnızca Ayarlar'da açılan bilgisayarda; makineler birbirini görmez), ~1 dk gecikme, salt okunur. Gereken: Code.gs v5 (`rev` sütunu + kilit altında `changes` eylemi), uygulamada ayrı yerel kopya tablosu. Makine durumu (heartbeat) / pano isteğe bağlı. Bir taslak yazılmaya başlanmıştı, hiçbir değişiklik kaydedilmedi.

## Açık konular / riskler

- **Türkçe karakterler etikette:** Zebra yerleşik font 0 (`^A0`) ğ/ş/ı/İ'yi her firmware'de basmayabilir. Adım 10'da gerçek yazıcıda denenmeli; gerekirse yazıcıya Türkçe destekli font yüklenir.
- **Etiket boyutu** (50×30 mm, 203 dpi) varsayım; yazıcı/etiket seçilince şablon güncellenecek (Adım 10).
- **Code128 otomatik mod (A)** eski Zebra firmware'de yoksa `^BCN,80,N,N,N,N` + elle alt küme kullanılır.
- **Veritabanı konumu** kullanıcı klasöründe; fabrika bilgisayarında birden çok Windows kullanıcısı olacaksa ortak klasöre (ProgramData) taşınmalı.
- Etiket önizlemesi var (`LabelPreview`, Adım 10a); ZPL'nin gerçek yazıcıdaki görünümü ancak yazıcıyla doğrulanır (labelary.com ZPL'yi görüntüler ama veriyi dışarı gönderir — kullanılmadı).
- Kurulum paketi kod imzasız → SmartScreen uyarısı. İmza için ücretli kod imzalama sertifikası gerekir.
