# Kumaş Kalite Kontrol ve Barkod Takip Sistemi

Bu dosya projenin hafızasıdır. Yeni oturumda önce bunu oku. **Her iş bitince "İlerleme günlüğü" ve "Şu anki durum" bölümlerini güncelle.**

- **Plan:** `C:\Users\User\Desktop\plan.txt` (Aşama 1–7; geliştirme adımları tablosu Aşama 7'de). Kaynak plan: `Desktop\plan.md`.
- **Depo stok + sevkiyat projesi (2026-11-14 →):** istek `Desktop\geliştirme.txt`, onaylı plan `docs/DEPO-SEVKIYAT-PLANI.md` (faz 0–7). Durum: aşağıda "Depo stok ve sevkiyat" bölümü.
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
  Domain/      Cut, CutStatus, CutIds (ID/barkod biçimi, TryParseScan), Stations, Length, Timestamps, VoidReasons, LotKeys
  Services/    CutService (oluşturma, iptal), PrintService (baskı + baskı kaydı + günlük)
  Printing/    LabelData, ILabelTemplate, ILabelPrinter, ZplLabelTemplate, FileLabelPrinter, Code128 (ZXing)
  Scanning/    ScanBurstDetector (okuyucu hız + biçim tespiti)
  Sync/        AppsScriptTransport, OutboxRepository (tablo: sync_outbox | server_outbox), SyncService, SyncWorker (ISyncRunner), RestoreService, modeller
  ServerApi/   istasyon↔sunucu sözleşmesi (ServerApiModels, DepotModels), ServerAddress, HttpServerTransport, ServerHttp, ServerSyncService, DepotApiClient
  Diagnostics/ ILog, FileLog, NullLog
src/KumasBarkod.Server   (net10.0, ASP.NET Core; çıktı KumasBarkodSunucu.exe, Windows hizmeti) — depo sunucusu
  Data/        ServerMigrations (sunucu şeması), RollStates, MovementKinds, ServerInvariants (bütünlük kontrolü), ServerBackupService
  Ingest/      CutIngestService (kesim alımı), ApiException
  Lots/        LotService, DataVersion · Stock/ StockQueryService
  Program.cs, ServerEndpoints (uç noktalar + ApiErrorMiddleware), ServerHosting (ServerPaths, FileLogProvider), MaintenanceWorker (+ AdminCommands)
src/KumasBarkod.App      (net10.0-windows, WPF; çıktı adı KumasBarkod.exe)
  App.xaml(.cs)  DI, kültür tr-TR, günlük + beklenmeyen hata yakalama, göç, gönderici, günlük yedek zamanlayıcı
  AppPaths.cs    veri klasörü (KUMASBARKOD_DATA_DIR ile değişir), db, labels, backups, logs
  Messages.cs    CutChanged, OpenCut, StartNewCut, BackupChanged, DatabaseRestored, SettingsChanged
  Scanning/      ScanCapture (pencere düzeyinde okutma yakalama)
  ServerSync/    ServerConnection (sunucu kuyruğu + gönderici), StationIdentityProvider (MachineGuid)
  (depo ekranları) ViewModels/StockViewModel, LotsViewModel, DataVersionPoller; Views/Pages/StockPage, LotsPage
  Printing/      ScreenOnlyPrinter, PagePrinter (Windows yazıcı/PDF), SelectablePrinter (ayara göre)
  Controls/      LabelPreview (50×30 mm etiket çizimi, gerçek Code128)
  Assets/        app.ico
  Themes/Styles.xaml, Converters.cs
  ViewModels/    MainWindow, CutEntry, Scan, History, Settings
  Views/         MainWindow (FluentWindow + NavigationView + durum çubuğu), Pages/ (CutEntry, Scan, History, Settings)
tests/KumasBarkod.Tests  (xUnit, 322 test) Data/, Domain/, Services/, Printing/, Scanning/, Sync/ (FakeAppsScript, ServerOutboxTests), Stress/ (rastgele girdi, eşzamanlılık; KUMAS_STRESS=1 ile 1 milyon kayıt)
tests/KumasBarkod.Server.Tests (xUnit, 66 test) WebApplicationFactory + geçici SQLite: CutIngestTests, StationToServerTests, ServerBackupTests, LotAndStockTests (gerçek istasyon veritabanı → gerçek HTTP → sunucu)
tools/sheets/            Code.gs (Apps Script v7), Code.test.js (node --test, 25 test), KURULUM.md
tools/test/              regresyon: uitest.ps1 (arayüz A/B, gerçek Google C/D), uidb.py, stres.gs, README.md
tools/installer/         KumasBarkod.iss, build-installer.ps1, generate-icon.ps1, KumasBarkodSunucu.iss, build-server-installer.ps1
docs/                    SUNUM.md (donanımsız sunum akışı), PILOT.md (fabrika pilotu), TEST-RAPORU.md,
                         DEPO-SEVKIYAT-PLANI.md (onaylı plan), SUNUCU-KURULUM.md (depo sunucusu kurulumu), FABRIKA-KURALLARI.md
```

**Paketler:** WPF-UI 4.3.0 (Fluent), CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 10.0.14, Microsoft.Data.Sqlite 10.0.14, Dapper 2.1.89, ZXing.Net 0.16.11 (yalnızca önizleme barkodu).

### Önemli tasarım kararları
- **Veritabanı:** SQLite, WAL + `synchronous=FULL` + `foreign_keys` + `busy_timeout=5000`. Şema sürümü `PRAGMA user_version`; her göç kendi işleminde. **Yayınlanmış göç asla değiştirilmez, yeni sürüm eklenir.** Uygulamadan yeni şemalı veritabanı açılmaz.
- **Şema v1:** `cuts` (STRICT, CHECK'ler: ID = istasyon + '-' + 6 hane, lot kırpılmış/boş değil, length_cm > 0, durum active/voided, iptal bilgisi tutarlılığı), `sync_outbox` (FK → cuts), `station_counter`, `settings` (anahtar/değer).
- **Şema v3 (2026-11-15):** `lots_cache` (lot_id, name, lot_key, active) — depo sunucusundaki lot tanımlarının kopyası; yalnızca öneri/uyarı, kayıt buna bağlı değil.
- **Şema v2 (2026-11-15):** `server_outbox` (sync_outbox ile aynı yapı, depo sunucusu kuyruğu). Göç, mevcut bütün kesimleri kayıt sırasıyla bu kuyruğa ekler. Kesimdeki her değişiklik `OutboxWriter.EnqueueAll` ile AYNI işlemde iki kuyruğa yazılır. Ayar `server_url` (normalleştirilmiş tam adres).
- **ID:** `K1-000123` saklanır/görünür; barkodda tiresiz `K1000123` (Türkçe klavye tire sorunu). İstasyon kodu `^[A-Z][A-Z0-9]$`, varsayılan `K1`. Sıra 1–999999.
- **Kayıt:** `CutService.CreateCut` tek işlemde (BEGIN IMMEDIATE) sayaç + cuts + sync_outbox. Hatalı giriş sıra numarası harcamaz.
- **Uzunluk:** personel tam sayı **cm** girer (`Length.TryParseCentimeters`), virgül/nokta reddedilir; veritabanı zaten cm tutar. Canlı ipucu "= 25,50 m" bilerek kalır (metre alışkanlığıyla yazılan "25", 25 cm'dir). Etiket, listeler, Geçmiş toplamları ve tablodaki Metre sütunu **metre gösterir**. Üst sınır Ayarlar'dan cm, varsayılan 30000 (`max_length_cm`).
- **Lot:** kırpılır, en fazla 50 karakter, büyük/küçük harf korunur (Türkçe i/İ).
- **Zaman:** UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` (sabit uzunluk, metin sıralaması = zaman sırası). `TimeProvider` enjekte edilir (testte sabit saat).
- **Giriş ekranı:** açılır liste (ComboBox) KULLANILMAZ — odak sorunları çıkardı. Lot = TextBox; ↑↓ son lotlarda gezer; son lotlar altta düğme (chip) olarak görünür.
- **Kısayollar:** sayfa kısayolları code-behind'da `AddHandler(PreviewKeyDownEvent, ..., handledEventsToo: true)` ile; KeyBinding kullanma (F5 üst denetimde yutuluyor). Giriş ekranı: Enter, F2 (lot), ↑↓ (son lotlar), F5 (sıradaki parça), F6 (bozulan etiket), Esc (uzunluğu temizle / onaydan vazgeç). Okutma: F8 (iptal), Esc. Geçmiş: F6 (bozulan etiket, onaylı).
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
| Sürüm 1.0.2 | ✅ yayında | `6908af3` (etiket `v1.0.2`) | Sabit iptal sebepleri. 2026-11-02 |

### Sürüm yayınlama (GitHub Releases)
- **İndirme linki (her zaman en yeni sürüm):** <https://github.com/bugraakaratas/Barkod-Uygulamasi/releases/latest/download/KumasBarkod-Kurulum.exe>. Dosya her sürümde **aynı adla** (`KumasBarkod-Kurulum.exe`) yüklenmeli, yoksa link kırılır.
- **Depo HERKESE AÇIK** — kullanıcıya soruldu (2026-11-02), "herkese açık kalsın" dedi: link herkes için, GitHub hesabı olmadan çalışır.
- `gh` CLI kurulu DEĞİL. Release, GitHub REST API ile yapıldı; anahtar `git credential fill` (Git Credential Manager) ile alındı, ekrana/dosyaya yazılmadı. Adımlar: `<Version>` artır → `build-installer.ps1` → commit + push → `POST /repos/.../releases` (`tag_name` vX.Y.Z, `target_commitish` HEAD, `make_latest`) → `uploads.github.com/...?name=KumasBarkod-Kurulum.exe` → linkten indirip SHA-256 karşılaştır.
- 1.0.1 doğrulandı: anonim indirme, dosya özgün ile birebir aynı (46,5 MB).
- 1.0.2 (2026-11-02) doğrulandı: sabit link 1.0.2'yi veriyor, SHA-256 özgünle aynı (`57C4FEA3…DFD1`, 48.720.174 bayt). Release notu Türkçe (değişiklikler + kurulum). İlk `git push` bu kez Claude'un kendi komutuyla sorunsuz çalıştı.
- csproj'u PowerShell ile düzenlerken `-Encoding utf8BOM` kullan (dosya BOM'lu; `utf8` BOM'u siliyor).
- Hız testi (`CutSearchTests`) artık paralel olmayan koleksiyonda (`TimingCollection`): paralel koşuda 1 sn sınırını aşıp kurulum derlemesini durdurmuştu.

| İptal sebepleri | ✅ | `eb9d025` | Okutma ekranında serbest metin yerine sabit 4 sebep (tek seçim, 1–4 / ↑↓ / Enter). 247 test |
| Revize: cm girişi | ✅ | `e5e3ae8` | Personel tam sayı cm girer. 2026-11-05 |
| Revize: fareyle lot | ✅ | `578f9f7` | Son lot önerileri tıklanarak seçilir |
| Revize: Okutma sadeleşti | ✅ | `75da294`, `b69e608` | "Yerine yeni kayıt" (F9) ve "Etiketi tekrar bas" kalktı; yalnızca İptal et |
| Revize: F5/F6 | ✅ | `067bdcc` | Sıradaki parça ve bozulan etiket ayrıldı |
| Sheets v5–v7 + test çalışması | ✅ | `5d69247`, `50b2874` | En yeni üstte, Türkçe başlık, büyük tablo hızı, formül koruması. 262 + 25 test |
| Geçmiş F6 + sürüm 1.0.5 | ✅ yayında (en yeni) | etiket `v1.0.5` | Bozulan etiket onaylı. Regresyon temiz |

### İptal sebepleri notları (kullanıcı isteği, 2026-11-02)
- **Karar:** tek seçim, liste sabit (Ayarlar'dan düzenlenmez), "Diğer"/ek not YOK. Sebepler `Core/Domain/VoidReasons.cs`: 1 Yanlış metre girildi · 2 Yanlış lot girildi · 3 Çift kayıt · 4 Kesim yapılmadı / vazgeçildi. "Etiket bozuk/okunmuyor" bilerek yok (çözüm Kesim Girişi'nde veya Geçmiş'te F6, iptal değil). **Metinleri değiştirme** — raporlardaki sayım bölünür; yeni sebep eklenecekse sona ekle.
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

### Sheets v5 notları (2026-11-06)
- **Başlıklar:** `COLUMNS` iç anahtar olarak İngilizce kaldı, `HEADERS` görünen Türkçe adlar. `upgradeHeader_` her POST'ta eski başlığı çevirir. `export_` iki adı da tanır; hücre değerleri değişmedi.
- **Sıra:** yeni satırlar `insertRowsAfter(1, n)` ile başlığın altına girer. Güncellemeler eklemeden ÖNCE yapılır (satır numaraları kayar). Eklenen satır biçimi kalın başlıktan gelmesin diye `setFontWeight('normal')`. Sırasızlık kontrolü ters döndü; eski artan tablolar açılıştaki boş pakette yeniden eskiye döner.
- **`export` eskiden yeniye verir:** offset tablonun altından sayılır. Yoksa geri kurma sırasında üste eklenen satır sayfaları kaydırırdı. Bu yüzden `RestoreService` satır numarası yerine ID bildirir.
- **1000 satır:** eski kod sayfa sonuna `getRange` ile yazıyordu; varsayılan 1000 satırlık sayfa dolunca hata vermesi beklenir (gerçek tabloda denenmedi). `insertRowsAfter` sayfayı büyütür.
- **Dağıtım yolu:** Chrome eklentisiyle editöre yapıştır + SHA-256 karşılaştır + Dağıtımları yönet → kalem → Yeni sürüm. Sürüm 5 = 2026-11-06. Script projesi "Kumaş Barkod", tablo "Kumaş Kesimleri - TEST". Google Sheets bağlayıcısı bu tabloya erişemiyor; okuma Chrome'dan yapılır.
- Yerel veritabanından anahtar okuyup tabloya istek atan betik otomatik güvenlik denetimine takıldı: doğrulamayı uygulamanın kendisi tetiklemeli.
- `RequiredScriptVersion` 3 kaldı: sıra ve başlık yalnızca görünümdür, eski script veri bozmaz. `FakeAppsScript` varsayılanı v5.

### Geçmiş ekranı baskısı (kullanıcı isteği, 2026-11-06)
- **Karar:** düğme kaldırılmadı, çünkü eski bir etiket bozulursa aynı ID ile basmanın tek yolu burası. Kesim Girişi'yle aynı dile getirildi: **F6 "Bozulan etiketi yeniden bas"**, satır içi onay (Enter bas, Esc vazgeç). Hiç basılmamış kayıtta (`print_count = 0`) onaysız basar, iptal kaydında pasif. **F5 Geçmiş'te çalışmaz:** F5 uygulama genelinde "yeni parça" demektir, Geçmiş'te sıradaki parça yoktur.

### Test çalışması ve Sheets v6/v7 (2026-11-06)
- Sonuçlar, bulunan hatalar ve ölçümler: `docs/TEST-RAPORU.md`. Tekrar çalıştırma: `tools/test/README.md`.
- **v6:** ölçümle bulundu: 50 bin satırda uzun sütunu okumak 7 sn (1 sütun da 13 sütun da aynı), üste eklemek 0,2 sn, TextFinder 0,2 sn. Bu yüzden üstteki `SCAN_WINDOW` (2000) satır okunur, altı TextFinder ile aranır, `max_sequences` `MAX_SEQUENCES` script özelliğinde tutulur, geç gelen kayıt üst bölüme düşüyorsa yalnızca o bölüm sıralanır. Sıralı tablo varsayımı: en alttaki satır üst bölümden yeniyse tablo tamamen yeniden sıralanır.
- **v7:** düz metin biçimi `=` ile başlayan metni formül olmaktan KORUMUYOR. `toValues_` metin sütunlarında `= + - @ '` ile başlayanların başına `'` yazar (hücrede görünmez, okurken gelmez). `export` metin sütunlarını `String` döndürür.
- `MAX_SEQUENCES` özelliği elle silinirse sonraki gönderimde tablo bir kez baştan sona okunur. Tabloya elle satır eklemek önbelleğe yansımaz.
- Stres testinde `sheet_` geçersiz kılınarak ayrı `STRES` sekmesi kullanılır; `Kesimler` ve yayındaki sürüm etkilenmez.
- Apps Script editörü: Monaco'ya `monaco.editor.getModels()[0].setValue` ile kod yazılabilir, panodan yapıştırma ilk denemede bazen tutmaz (ikinci deneme tuttu). İşlev açılır listesi ilk tıkta geç açılabilir; seçimi çalıştırmadan önce sayfa metninden doğrula, yoksa `doPost` çalışır. Listede en fazla ilk 9 işlev görünür.

## Depo stok ve sevkiyat (2026-11-14 →)

Plan: `docs/DEPO-SEVKIYAT-PLANI.md`. Kullanıcı planı 2026-11-15'te onayladı. **Her faz sonunda: test → commit → bu dosya → özet → sonraki faz için onay.** Her faz `feature/fazN-…` dalında; bitince `main`'e birleştirilip gönderilir.

### Kullanıcı kararları (değiştirme)
- QC ve depo cihazları aynı fabrika yerel ağında. Kurulum: **3 ekranlı bilgisayar** (Kalite kontrol · Sevkiyat/rıhtım · Patron) + **1 ekransız sunucu**. Aynı WPF uygulaması üçünde; Ayarlar'da "bilgisayarın görevi" (qc / dock / office) görünen sayfaları seçer.
- **Lot = firma** (lot adı firmayı gösterir). Ayrı firma tablosu yok, sipariş/parti ayrımı yok.
- **Etiketi basılan top doğrudan stokta** (`in_stock`); depo kabul okutması yok.
- Depo/Sevkiyat ekranları **WPF'te** (rıhtım PC'si + kablosuz okuyucu + büyük ekran + belirgin sesler). **El terminali ileride kesin:** bütün kurallar sunucuda, API istemciden bağımsız (`/api/v1`, hata kodu + Türkçe mesaj sunucudan, okutma kimliği, sürüm kontrolü); WPF yalnızca istemci (kural yok, sunucu veritabanına doğrudan erişim yok). El terminali için sade web okutma sayfası Faz 4'te yazılacak.
- Sunucuda SQLite (tek süreç, yerel disk). PostgreSQL'e geçiş ölçütleri planda. Bulut reddedildi (internet kesilince tır yüklenemez).
- Sevkiyat okutması internetsiz çalışmaz (tek kazanan kuralı); QC internetsiz çalışmaya devam eder.
- Hareketsiz sevkiyat kendiliğinden iptal edilmez (yalnızca uyarı). Lot başına tek oturum kilidi yok; kilit top düzeyinde.
- Kullanıcı sahaya uzak: teknik konuları sahadaki karşılığıyla, sade anlat (bkz. 2026-11-15 açıklamaları: sunucu = ortak hakem, "kontrol" = karar sunucuda, görüntüleme uygulamada).

### Faz durumu
| Faz | Durum | Commit | Özet |
|---|---|---|---|
| 0 Saha | ⏳ kullanıcıdan bilgi | — | Yükleme nasıl (elle/forklift, kim okutuyor), Wi-Fi kapsaması, sunucu PC + UPS |
| 1 Sunucu + veri alımı | ✅ GitHub'da | `cdde8f8` `4638e97` `5269f72` `8fef74e` `997a4e6` `8ad7071` `46f92c1` | Aşağıda. QC 1.1.0, sunucu 0.1.0 |
| 2 Lot tanımları + Depo Stoğu | ✅ | `2c40993` `e81be0c` + belge | Aşağıda. QC 1.2.0, sunucu 0.2.0 |
| 3 QC'de lot önerisi | ✅ | `3fc2e26` + belge | Aşağıda. QC 1.3.0 |
| 4 Sevkiyat | ✅ | `cbfdbdd` `40ee078` + el terminali + belge | Aşağıda. QC 1.4.0, sunucu 0.3.0 |
| 5 Geri alma + istisnalar + raporlar | ✅ | `ffc82e5` + WPF + belge | Aşağıda. QC 1.5.0, sunucu 0.4.0 |
| 6 Sheets sunucudan | ✅ | `0b49b0a` | Aşağıda. QC 1.6.0, sunucu 0.5.0, Apps Script v8 |
| 7 | ⏳ | — | İsteğe bağlılar (sayım, iade, raf, PostgreSQL, HTTPS) |

### Faz 6 notları (2026-11-15) — Google Sheets kopyası sunucudan (kullanıcı seçimi "a")
- **Code.gs v8:** `action: "snapshot"` → yalnızca **Stok** ve **Sevkiyatlar** sayfaları (izin listesi; Kesimler ezilemez) baştan yazılır: `clearContents`, başlık + boş + "Son güncelleme: …" (kalın), satır sayısı yetmezse `insertRowsAfter`, en fazla 50.000 satır, başlık genişliği ≠ satır genişliği → ret, `= + - @ '` ile başlayan metin `'` önekiyle (formül çalışmaz). v7 davranışı aynen. Node testleri 29 (`Code.test.js`: sahte e-tablo artık çok sayfalı).
- **Core:** `AppsScriptTransport.GetScriptVersionAsync` (boş paket → sürüm; tabloya yazmaz), `SnapshotAsync` (v8 ister). `SheetsModels.cs`: `SheetsPaths.Settings = api/v1/settings/sheets`, `SheetsSettingsRequest(Url, Key)`, `SheetsStatusResponse` (anahtar yok, yalnızca `KeySet`).
- **Sunucu şema v5:** `sheets_pending(cut_id PK, seq)`. **Kuyruğu tetikleyiciler doldurur** (`AFTER INSERT ON rolls`, `AFTER UPDATE OF <QC alanları>`; `INSERT OR REPLACE`, seq = max+1) → hiçbir kod yolu kuyruğu unutamaz. Göç mevcut bütün rolls'u kuyruğa koyar. Satır içeriği gönderim anında `rolls`'tan (her zaman güncel); silme `cut_id AND seq` ile → gönderim sırasında değişen top kuyrukta kalır.
- **`SheetsSyncService` + `SheetsWorker`** (BackgroundService, 15 sn; hata → 1 dk; ayar değişince hemen `Wake`). Kesimler: 200'lük paketler, `lot_code` = QC'nin yazdığı ham metin (istasyonlarla aynı satır). Stok/Sevkiyatlar: `data_version` değiştiyse ve son denemeden ≥ `SnapshotInterval` (2 dk) geçtiyse. Durum bellekte (son başarı, son tablo yazımı, son hata — aynı hata günlüğe bir kez). Beklenmeyen hata hizmeti durdurmaz (`LogError` + durum).
- **Ayar uçları (yalnızca yetkili):** `GET/PUT api/v1/settings/sheets`. PUT kaydetmeden önce Google'ı dener (sürüm ≥ 8, anahtar, erişim); başarısızsa 400 `SHEETS_FAILED` + sebep, **kaydedilmez**. Adres boş = kapat. Anahtar boş = kayıtlı anahtar korunur. **Adres değişirse bütün toplar yeniden kuyruğa** (yeni tabloya da gitsin). Ayar `server_settings` (`sheets_url`, `sheets_key`) — veritabanı yedeğiyle korunur; anahtar hiçbir yanıtta yok.
- **WPF:** Ayarlar'da "Google Sheets kopyası (depo sunucusundan)" kartı — **yalnızca yönetim görevi** (`ServerSheetsViewModel`, `SettingsViewModel.ServerSheets`). Kart içinde yetkili girişi (kart → Enter → PIN → Enter; oturum `DepotApiClient` ile ortak), durum satırı (hata turuncu), "Dene ve kaydet" / "Durumu yenile" / "Kopyayı kapat". İstasyonun kendi Sheets kartına "sunucu gönderiyorsa boş bırakılabilir; geri kurmada yeniden yazılır" notu.
- **`DepotApiClient` zaman aşımı istek başına:** varsayılan 10 sn (`DefaultTimeout`), Sheets kaydı 75 sn (Apps Script ilk açılışta yavaş); `HttpClient.Timeout` 90 sn'ye çıktı. İptal → `SyncException(Offline, "zaman aşımı")`.
- **Testler:** sunucu 114 (`ShipmentTests.Phase6.cs` + `FakeAppsScript` — test düzeninde `AppsScriptTransport` sahte işleyiciyle; yetki 401/403, http/anahtarsız/yanlış anahtar/v7/internetsiz → kaydedilmez, bütün toplar Kesimler'e + Stok/Sevkiyatlar içeriği, 2 dk sınırı, veri değişmezse yazılmaz, QC basım/iptali kuyruğa, internetsiz bekler, seq yarışı, anahtar korunur/adres boş kapatır/başka tablo hepsini alır). Arayüz **J 22** (QC'de kart yok, yönetimde var, kart içi giriş, http ve ulaşılamayan adres kaydedilmez, sunucuda ayar varken Google'a ulaşılamıyor → "Gönderilemiyor" + sağlık bozulmaz, kapat, sunucu günlüğü). **Gerçek Google'a gönderim kullanıcının hesabıyla ayrıca denenmeli** (Code.gs v8 dağıtımı gerekiyor).

### Faz 5 notları (2026-11-15)
- **Şema v4:** `shipments.reversed_by/reversed_at/reversal_reason/reversal_request_id`, `cut_conflicts.resolved_by`, indeks `(status, closed_at)`. Kapanış bilgisi (closed_*) geri almada korunur.
- **Onayı geri alma** (`POST shipments/{id}/reverse {reason ≥3, requestId}`, yalnızca yetkili, 409 `SHIPMENT_NOT_CONFIRMED`): kalemler `reversed`, toplar shipped → in_stock (`ship_reversed`); **QC'de iptal edilmiş top** ardından in_stock → voided (`qc_voided`, iki hareket — tetikleyici shipped → voided'e izin vermez). Aynı `requestId` tekrar → başarı. `ShipmentDetail`'e `ReversedBy/At/Reason` (isteğe bağlı alanlar, geriye uyumlu).
- **İstisnalar** (`GET exceptions`, yetkili): çözülmemiş çakışmalar (sunucudaki kayıt + `payload_json`'dan gelen kayıt), sevk sonrası QC iptali (`shipped` + `qc_status = voided`), 24 sa işlem görmeyen açık sevkiyatlar, son 30 onaylı, eşleşmemiş lot sayısı.
- **Karantina çözümü** (`POST exceptions/conflicts/{id}/resolve {note ≥3}`): çakışma `resolved_*`; topta başka çözülmemiş çakışma yoksa son `quarantined` hareketinin `from_state`'ine döner (**reserved ise in_stock** — sevkiyattan çıkarılmıştı), `released` hareketi. **Kesim alımı:** gelen satırın (cut_id, created_at) çözülmüş bir çakışmayla eşleşirse sessizce kabul edilir (QC'deki kuyruk uyarısı kalkar, top etkilenmez).
- **Raporlar** (giriş istemez, gerçek kayıtlardan; `from/to` = yyyy-MM-dd yerel gün, ikisi dahil, ≤366 gün, sunucunun saat dilimi): `reports/stock-by-lot`, `reports/daily-shipments` (yalnızca onaylı; geri alınan/iptal hariç; gün C#'ta yerel saate çevrilerek gruplanır), `reports/shipped-rolls`, `reports/shipments` (kapanmış hepsi + not: iptal sebebi / "Geri alındı (yetkili): sebep").
- **Bütünlük:** geri alınan sevkiyatta yalnızca reversed/removed kalem; başka sevkiyatta reversed kalem yok.
- **WPF (yönetim görevi):** menü = **Sevkiyat, Depo Stoğu, Lotlar, Personel, İstisnalar, Raporlar** (görünen sıra = Ctrl+1..3; açılış ekranı yine Depo Stoğu — `_startPage`). `ExceptionsPage/ExceptionsViewModel` (yetkili girişi; karantina listesi + not + "Çözüldü"; sevk sonrası iptal; 24 sa açık; son onaylananlar + sebep + "Onayı geri al…" + onay paneli; 3 sn yenileme). `ReportsPage/ReportsViewModel` (rapor türü açılır listesi, tarih aralığı kısa biçim, `DataGrid` sütunları `[DisplayName]`'den, toplam satırı, **CSV: `;` + UTF-8 BOM** — Türkçe Excel doğrudan açar; `= + - @` ile başlayan hücreye `'`).
  - Bulunan ve düzeltilenler: geri alma başarı mesajı "0 kalem" diyordu (sayı işlemden önce alınır); yönetim menüsünde görünen sıra ile Ctrl kısayol sırası farklıydı; rapor tarih kutularında tarih kesiliyordu.
- **Testler:** sunucu 108 (`ShipmentTests.Phase5.cs`: geri alma yetki/sebep/tekrar/yeniden sevk, açık/iptal geri alınamaz, sevk sonrası QC iptali → geri almada voided, karantina listesi/çözüm/tekrar gönderimde sessiz kabul/yeniden sevk, ayrılmış topun karantinası → stoğa, 24 sa açık + son onaylılar, raporlar (stok, günlük, sevk edilen, geçmiş, hatalı aralık)). Arayüz **I 29** (yetkili girişi, karantina çözümü, sebepsiz geri alma yok, geri alma paneli, raporlar, **Windows "Farklı kaydet" penceresine yol yapıştırılarak CSV kaydı** + BOM/başlık/veri kontrolü; pencere başlığı kontrol edilmeden tuş gönderilmez).

### Faz 4 notları (2026-11-15) — varsayılan rıhtım düzeniyle (kullanıcı onayı; saha bilgisi gelmedi)
- **Sunucu şeması v3:** `users` (rol warehouse|supervisor, `badge_code` benzersiz, PIN = PBKDF2-SHA256 + tuz, kayıtta "turSayısı:özet" — `AuthService.Iterations` testte 1000, gerçek `DefaultIterations` 100.000; 5 yanlışta 5 dk kilit; silinmez), `auth_sessions` (belirteç SHA-256 özeti, 12 sa), `shipment_counter` (yıl → sıra; no `SV-YYYY-NNNNN`, yerel yıl), `shipments` (open → confirmed|cancelled, confirmed → reversed; tetikleyici; silinmez; `version`, `close_request_id`), `shipment_lots`, `scan_events` (kabul + ret, `client_scan_id` benzersiz, yanıt JSON bir kez yazılır — tetikleyici yalnızca boş yanıtın doldurulmasına izin verir; silinmez), `shipment_items` (**`ux_items_one_active_per_roll`: bir top aynı anda tek aktif kalem**; kapanmış kalem değişmez; silinmez).
- **Giriş (`AuthService`):** `Authorization: Bearer`. Kart kodu büyük harfe (ı/i/İ → I); top barkoduna benzeyen kart reddedilir. Hata mesajı kartın varlığını söylemez. Hiç personel yokken ilk kayıt girişsiz ve yalnızca yetkili. Son etkin yetkili pasif/rol düşürme yapılamaz; pasif yapılanın oturumları kapanır. Rol yalnızca personel yönetimini ayırır; sevkiyatın her işlemini depo personeli yapar (onayı geri alma Faz 5'te yetkili).
- **Okutma (`ShipmentService.Scan`, tek BEGIN IMMEDIATE):** tekrar gelen `clientScanId` → kayıtlı yanıt aynen. Sıra: sevkiyat açık değil → barkod geçersiz → sistemde yok → karantina → bu sevkiyatta zaten (**sarı**, "reddedilen"e sayılmaz) → sevk edilmiş → QC iptal → başka sevkiyatta → lot atanmamış → yanlış lot ("Bu barkod seçili lota ait değil. Barkodun lotu: X. Seçili lot: Y.") → stokta değil → kabul (koşullu `UPDATE … WHERE state='in_stock'`, kalem, `reserved` hareketi, sürüm+1). Diğer retler kırmızı (sistemde yok da kırmızı: top yüklenmemeli). Ret de `scan_events`'e yazılır. Çıkarma (`/removals`) kalem `removed`, top `in_stock`, `unreserved`.
- **Onay:** `expectedVersion` ≠ → 409 `VERSION_MISMATCH`; boş → 400; aynı `requestId` tekrar → başarı. **İptal:** sebep zorunlu (`CancelReasons` 3 sabit), kalemler `cancelled`, toplar stoğa. Kapanmış sevkiyata okutma → `SHIPMENT_NOT_OPEN` (son işlem zamanı değişmez).
- **QC iptali / karantina ayrılmış topa gelirse** (`CutIngestService` → `ShipmentService.DetachReservedRoll`): kalem `removed`, sevkiyat sürümü artar (eski sürümle onay reddedilir), top `voided`/`quarantined`.
- **Bütünlük kontrolü** genişledi: ayrılmış ⇔ açık sevkiyatta aktif kalem; sevk edilmiş ⇔ onaylı sevkiyatta aktif kalem; aktif kalem yalnızca açık/onaylı sevkiyatta; kabul edilen her okutmanın kalemi; boş yanıt yok.
- **Uç noktalar:** `POST auth/login`, `POST auth/logout`, `GET auth/me`, `GET users/bootstrap-needed`, `GET|POST users`, `PUT users/{id}`, `GET shipments?status=open|closed`, `POST shipments {lotIds}`, `GET shipments/{id}`, `POST shipments/{id}/scans|removals {clientScanId, rawInput}`, `POST shipments/{id}/confirm {expectedVersion, requestId}`, `POST shipments/{id}/cancel {reason, requestId}`, `GET /el-terminali` (gömülü sayfa, `no-store`), `GET /` → `/el-terminali`. Hata kodları `ShipmentErrorCodes`; okutma sonuçları `ScanCodes` + `ScanSeverity`.
- **WPF:** `DepotApiClient` oturumu tutar (bilgisayar başına tek giriş; 401 → oturum düşer, `SessionChanged`). `SyncException.StatusCode/Code` eklendi. Görevler: **sevkiyat = Sevkiyat, Depo Stoğu**; **yönetim = Depo Stoğu, Sevkiyat, Lotlar, Personel** (Ctrl+1..3 ilk üç, Personel kısayolsuz). `ShipmentPage/ShipmentViewModel`: giriş (kart → Enter → PIN → Enter), liste (açık sevkiyatlar + lot seçip başlat, Enter/çift tık), okutma ekranı (44 pt lot, renkli sonuç bandı, 40 pt sayaçlar, okutulanlar + son okutmalar), F7 çıkarma, F9 onay paneli, F8 iptal paneli (1–3 + Enter; rakamlar `Handled` değil), Esc; okutmalar kuyrukla sırayla, ağ yoksa 2 sn'de bir aynı kimlikle tekrar, "BAĞLANTI YOK" bandı; çift okuma 1,5 sn — **mod değişince sıfırlanır** (H testinde bulunan hata: okut → çıkarma modu → aynı top yok sayılıyordu); 30 dk işlem yoksa çıkış; okuyucu okutması sevkiyat ekranı açıksa oraya (`MainWindow` → `ShipmentPage.TryHandleScan`). `FeedbackSounds`: bellek içi WAV (kabul ince bip, uyarı çift, ret kalın 3 darbe, çıkarma alçalan). `PersonnelPage/PersonnelViewModel`: ilk yetkili, yetkili girişi, ekle, PIN değiştir, rol, pasif.
  - Bulunan ve düzeltilenler: sonuç bandı metinlerine sabit `AutomationProperties.Name` verilmişti → ekran okuyucu/test metni göremiyordu (kaldırıldı); "Reddedilen" sayısına zararsız tekrar okutma giriyordu; kapanmış sevkiyata gelen ret son işlem zamanını değiştiriyordu; sevkiyat listesi sırası eşit zamanda belirsizdi (`shipment_id` ikinci ölçüt).
- **El terminali sayfası** (`src/KumasBarkod.Server/Web/handheld.html`, gömülü kaynak): düz HTML/JS, telefon boyutu; giriş → liste → okutma; WebAudio sesleri (ilk dokunuşta açılır) + titreşim; kuyruk + aynı kimlikle tekrar; `crypto.getRandomValues` (http'de `randomUUID` yok); okutma kutusu odağı korunur (DataWedge "klavye + Enter"); 3 sn sürüm yoklaması. Belirteç `sessionStorage`'da.
- **Testler:** sunucu 101 (`ShipmentTests`: kullanıcının 12 senaryosu S1–S12 + giriş güvenliği + eş zamanlı 20 okutma/tek kazanan + tekrar gönderim + QC iptali/karantina + veritabanı korumaları + sayfa). Arayüz **G 41** (rıhtım WPF, sunucu öldürülüp yeniden açılır) ve **H 26** (`tools/test/handheld-e2e.mjs`: görünmez Edge `--headless=new --remote-debugging-port=9333` + Node 26 yerleşik WebSocket ile CDP; `Network.emulateNetworkConditions` ile ağ kesintisi; yalnızca testin kendi profiliyle açılan Edge kapatılır). Aynı turda A 60, B 21, E 31, F 24 (yönetim menüsü değiştiği için E'de Lotlar artık Ctrl+3).

### Faz 3 notları (2026-11-15)
- **Önbellek:** `LotCatalog` (Core/Data) `lots_cache`'i tek işlemde yeniler. `ServerSyncService` her BAŞARILI turun sonunda `GET /api/v1/lots` ile yeniler (yalnızca QC görevinde — QC dışında tur zaten erken döner); alınamazsa eski liste kalır, kesim gönderimi etkilenmez; `LotCatalogRefreshed` olayı (arka plan iş parçacığı). `IServerTransport.GetLotsAsync` eklendi.
- **Eşleştirme (`LotMatcher`, sunucuyla aynı `LotKeys`):** liste boş → `NoCatalog` (kontrol yok, ekran eskisi gibi; sunucusuz kurulumlar ve A/B testleri bu yolda). `Key` eşit → `Defined` (etikete/kayda TANIMLI AD yazılır, sonuç mesajında "yazılan … → tanımlı ad"). Değilse `Undefined`; öneri YALNIZCA `LooseKey` eşitliği (sıfır farkı) — "içinde geçiyor" önerilmez (LOT-31 → LOT-3 yanlış firma riski). `Suggest` (yazarken): başı uyanlar, sonra içinde geçenler, en fazla 8.
- **Kesim Girişi:** lot kutusunun altındaki seçenekler artık başlıklı: yazılan metne uyan tanımlı lot varsa "Tanımlı lotlar", yoksa "Son lotlar" (aynı `RecentLots` koleksiyonu, ↑↓ ve tıklama aynı). Seçeneklerden biri seçilince liste değişmez; yazınca yeniden süzülür.
  - **Tanımsız lot onayı** (`LotCheckPanel`, Kaydet düğmesinin üstünde, Focusable Border — `ReprintPanel` deseni): öneri yoksa seçim "2" hazır, Enter yazıldığı gibi kaydeder; öneri varsa HİÇBİR ŞEY seçili değil, seçimsiz Enter kaydetmez ("Önce seçin"); 1 = önerilen, 2 = yazıldığı gibi, Esc = düzelt (odak lota), tıklama/başka yere odak = kapanır (kayıt yok). Rakam tuşları `Handled` işaretlenmez (okutma karakterleri ScanCapture'a ulaşsın); okutmanın Enter'ı ScanCapture'da işlendiği için panel kaydetmez (testte doğrulandı). F5/F6 paneli kapatır.
  - Oturumda "yazıldığı gibi" onaylanan tanımsız lot (anahtar → İLK yazım) tekrar sorulmaz ve hep ilk yazımla basılır ("YENI LOT"/"yeni lot" karışmasın — testte görülüp düzeltildi).
  - F5 (sıradaki parça) kontrol etmez: son kaydın lotu zaten kontrolden geçti.
- **Arayüz testi F** (`uitest.ps1 -Part F`, geçici sunucu `localhost:5087`, 24/24): önbellek dolar, yazarken "Tanımlı lotlar", ↓ ilk öneri, Türkçe harfsiz yazımla öneri (akin → Akın Tekstil), "lot 3" → LOT-3 basıldı, tanımsız + Enter, aynı tanımsız tekrar sorulmaz + ilk yazım, LOT-03'te seçimsiz Enter kaydetmez / 1 → LOT-3 / 2 → LOT-03, Esc + düzeltme, panel açıkken okutma kayıt açmaz ve Okutma ekranına geçer, F5, sunucuda LOT-3 bağlı / tanımsızlar yönetime düştü. Aynı turda A 60/60, B 21/21 (300 F5 orta 161 ms, en kötü 706 ms — tek ölçüm), E 31/31; birim 322 + 66.

### Faz 2 notları (2026-11-15)
- **Bilgisayar görevi (`settings.station_role`, `StationRoles`):** `qc` (varsayılan; Kesim Girişi, Okutma, Geçmiş), `dock` (Depo Stoğu; Faz 4'te Sevkiyat), `office` (Depo Stoğu, Lotlar). Ayarlar'ın EN ÜSTÜNDE seçilir (radyo düğmeleri), `SettingsChangedMessage` ile menü hemen değişir; gizlenen ekrandaysa görevin ilk ekranına gider. Ctrl+1..3 = görünen menü sırası, Ctrl+4 = Ayarlar. Görev yetki DEĞİLDİR (kullanıcı girişi Faz 4).
  - QC dışı görevde durum çubuğunda yalnızca "Bu bilgisayar: <görev>" (istasyon, Sheets, depo kuyruğu, yazıcı gizli).
  - **QC dışı bilgisayar sunucuya istasyon olarak kaydolmaz** (`ServerSyncService`: görev ≠ qc ve kuyruk boşsa el sıkışma yok). Yoksa patron PC'si varsayılan K1 kodunu gerçek QC bilgisayarından önce kapabilirdi. Kuyruğunda kesim olan (görevi sonradan değişmiş) bilgisayar yine gönderir.
  - Okutulan barkod (`OpenCutMessage`): QC'de Okutma ekranı (yerel kayıt), diğerlerinde Depo Stoğu → sunucudan topun geçmişi.
- **Lot (= firma) kuralları (sunucu şeması v2):** `lots` (ad yazıldığı gibi, `lot_key` = `LotKeys.Key` benzersiz, `active`, silinemez — tetikleyici), `lot_aliases` (QC metni → lot, elle), `rolls.lot_id`. Bağlama sırası: elle eşleştirme → yazım farkı eşitliği (`Key`: harf büyüklüğü, Türkçe harf İ/I/ı/Ş/Ğ/Ü/Ö/Ç, boşluk ve `- _ / . , \` yok sayılır). **Sayı sıfırı farkı (LOT-03 / LOT-3) kendiliğinden bağlanmaz**, `LooseKey` ile yalnızca öneri. Lot eklenince/adı değişince eşleşen bağlanmamış toplar bağlanır; elle eşleştirilmiş metin başka lottaysa dokunulmaz. Bağlama değişikliği `lot_assigned` hareketi (from=to=durum, not: eski → yeni lot + cihaz adı). Sevkiyatta/sevk edilmiş topun lotu değiştirilemez (409). Gelen top lot varsa hemen bağlanır (ayrı hareket yok).
- **Veri sürümü:** `server_settings.data_version`; top/hareket/çakışma/lot değişen her işlemde aynı işlem içinde artar (yalnızca istasyon "son görülme" değişince artmaz — `total_changes()` karşılaştırması). Ekranlar `GET /api/v1/version`'ı 3 sn'de bir sorar (`DataVersionPoller`, yalnızca ekran görünürken), değiştiyse yeniden okur.
- **Uç noktalar (yeni):** `GET /api/v1/version`, `GET /api/v1/stock?state=&lotId=&unmatched=&search=&offset=&limit=` (≤500; durum: in_stock|reserved|shipped|voided|quarantined|all; arama barkod biçimindeyse tam ID, değilse lot adı/metni LIKE, `% _ \` kaçışlı), `GET /api/v1/rolls/{barkod|ID}` (top + bütün hareketler), `GET /api/v1/lots`, `GET /api/v1/lots/unmatched`, `POST /api/v1/lots {name}`, `PUT /api/v1/lots/{id} {name?, active?}`, `POST /api/v1/lots/{id}/aliases {rawText}`. İşlemi yapan cihaz `X-Kumas-Device` başlığıyla (WPF: `Environment.MachineName`). Hata kodları: `LOT_EXISTS` 409, `LOT_NOT_FOUND` 404, `LOT_INVALID_NAME` 400, `LOT_RAW_NOT_FOUND` 404, `ROLL_NOT_FOUND` 404.
- **Stok sayıları** her zaman gerçek kayıtlardan (`count`, `sum(length_cm)` where `state='in_stock'`); özet ve liste aynı okuma işleminde.
- **WPF:** `DepotApiClient` (Core; adres her çağrıda ayarlardan, 10 sn zaman aşımı), `ServerHttp` (ortak hata çevirisi). `StockPage/StockViewModel` (özet kartları, sol lot listesi + "Eşleşmemiş", arama Ctrl+F, durum açılır listesi, 200'lük sayfa, sağda 300 px geçmiş paneli, Esc kapat) ve `LotsPage/LotsViewModel` (lot listesi + ekle (Enter) + ad kaydet + pasif/etkin; eşleşmemiş yazımlar + öneri düğmeleri (her düğme KENDİ yazımını taşır — seçili satırdan bağımsız) + "Soldaki seçili lota bağla" + "Bu yazımla yeni lot"). `InvertBool` dönüştürücüsü eklendi. `KUMASBARKOD_START_PAGE` artık `stock`, `lots` da alır.
- **Gerçek uygulama denemesi (2026-11-15, gerçek verinin kopyası, klavyesiz):** sunucu `--urls http://localhost:5080` (çift yığın), kopya QC veritabanı `e2e\sync.cs` ile gönderildi (43 top), API ile 3 lot eklendi → "LOT-9" tanımlanınca "Lot 9" yazımlı 6 top kendiliğinden bağlandı, sıfır farkı olmadığı için yanlış öneri yok; 10 eşleşmemiş yazım. Yönetim görevinde Depo Stoğu (38 top / 256,75 m), LOT-9 filtresi + top geçmişi (UI Automation `SelectionItemPattern` ile satır seçimi çalışıyor; menü öğesi seçilemiyor), Lotlar ekranında "hyk" → LOT-9 bağlama (2 top, harekete cihaz adıyla yazıldı). **Canlı senkron:** Depo Stoğu açıkken QC tarafından 2 yeni top gönderildi → ekran kendiliğinden 38 → 40 top. QC görevi eskisi gibi. Durum çubuğunda "K1 istasyon kodu başka bilgisayara kayıtlı" görüldü: test betiği K1'i sahte kimlikle kaydetmişti — koruma doğru çalıştı.
  - Bulunan ve düzeltilenler: geçmiş paneli açıkken "Lot" sütunu sıfıra iniyordu (sütun genişlikleri + `MinWidth`); QC dışı görevde durum çubuğunda anlamsız yazıcı/kuyruk bilgisi; öneri düğmesi seçili satırın metnini bağlayabilirdi (düğme kendi metnini taşıyor).
- Betikler (scratchpad `e2e\`): `sync.cs` (QC veritabanından sunucuya bir kez gönder), `role.cs` (görevli boş uygulama veritabanı), `select.ps1` (UI Automation ile satır seç), `nav.ps1` (düğmeye bas).
- **Klavyeli arayüz testleri (2026-11-15, kullanıcı tam izin verdi — artık sormadan çalıştırılır, bkz. hafıza `keyboard-ui-tests`):** A 60/60, B 21/21, yeni **E 31/31** (`uitest.ps1 -Part E`: geçici sunucu `localhost:5087`, QC kuyruğu sunucuya, görev Yönetim ↔ QC (UI Automation ile radyo + Kaydet), Depo Stoğu'nda klavyeyle arama/Esc/Ctrl+F, okuyucu gibi hızlı `K1000005{ENTER}` → topun geçmişi (arama kutusu bozulmadı), bilinmeyen barkod, Lotlar'da Enter ile ekleme + yazım farklı aynı lot reddi, Lotlar ekranında okutma → Depo Stoğu, QC'ye dönünce yeni kesim sunucuya gidip lota bağlandı, sunucu `--kontrol`). C/D (gerçek Google tablosuna yazar) çalıştırılmadı — Sheets tarafı değişmedi.
  - B'de "İki kopya farklı ID üretti" bir kez KALDI: kayıtlar doğruydu (K1-300326/300327), betik sayımı 700 ms sonra okuyordu → 5 sn'ye kadar bekleme eklendi, tekrar 21/21.
  - E ilk koşu 25/29: uygulama doğruydu; InfoBar mesaj metni UI Automation'da görünmüyor ve PowerShell `Invoke-RestMethod` JSON dizisini tek nesne döndürüyor (`@(...)` sayısı 1) → kontroller davranışa çevrildi (sunucudaki lot adları, kutudaki metin, geçmiş paneli açılmadı).

### Faz 1 notları (2026-11-15)
- **İstasyon:** şema v2 `server_outbox`; `ServerSyncService` Sheets göndericisinin desenini izler: oturumda bir adrese/istasyon koduna ilk gidişte BOŞ paket (el sıkışma: istasyon kaydı + `max_sequences` → `StationCounters.RaiseTo`), sonra 200'lük paketler, turda en fazla 25, hata olunca tur durur. Çakışan kesim kuyrukta kalır (uyarı görünür kalsın). Reddedilince (4xx) el sıkışma tekrarlanır. `ServerConnection` (App): 30 sn aralık, kayıttan 1 sn sonra dürtme, hata beklemesi en fazla 1 dk; HTTP zaman aşımı 15 sn.
- **Bilgisayar kimliği:** Windows `MachineGuid` (`mg-…`), veritabanında SAKLANMAZ (veritabanı başka PC'ye kopyalanırsa kimlik taşınmasın). Okunamazsa `%LOCALAPPDATA%\KumasBarkod\install-id`. Ayarlar'da görünür.
- **Sözleşme (`Core/ServerApi/ServerApiModels.cs`):** `ApiVersion = 1`, camelCase JSON. `POST /api/v1/cuts/batch` `{installId, stationCode, appVersion, rows[CutSyncRow]}` → `{ok, apiVersion, accepted, conflicts[], maxSequences}`; hata `{ok:false, code, message}` (`STATION_OWNED_BY_OTHER_DEVICE` 409, `INVALID_REQUEST` 400, `INTERNAL` 500). `GET /health` → `{ok, service:"KumasBarkod.Server", apiVersion, version, schemaVersion, serverTime}`. Taşıyıcı: ağ/zaman aşımı/5xx → Offline, 4xx → Rejected.
- **Sunucu şeması v1:** `stations` (kod → install_id), `rolls` (stok satırı = kesimin yansısı; `state` in_stock|reserved|shipped|voided|quarantined; QC alanları değişmez), `stock_movements` (yalnızca ekleme; kind listesi tam: qc_created, qc_voided, lot_assigned, received, reserved, unreserved, shipped, ship_reversed, quarantined, released, adjusted), `cut_conflicts`, `server_settings`. **Tetikleyiciler:** durum geçişi listesi, QC alanları değişmez, QC iptali geri alınmaz, top silinmez, hareket değiştirilmez/silinmez. SQLite CHECK sonradan değişmediği için durum/hareket listeleri ilk sürümde tam yazıldı. `lot_id` ve sevkiyat FK'leri sonraki fazların göçlerinde.
- **Alım kuralları (`CutIngestService`, tek BEGIN IMMEDIATE işlem):** yeni kesim → `in_stock` + `qc_created` (occurred_at = QC zamanı, recorded_at = sunucu zamanı); zaten iptal geldiyse ardından `qc_voided`. Bilinen kesim + aynı kimlik (istasyon, sıra, lot, uzunluk, operatör, kayıt zamanı) → yalnızca ileri yönde güncelleme (baskı sayısı büyüğü, iptal kalıcı; geç gelen eski hâl iptali geri almaz). Farklı kimlik → `cut_conflicts` + top `quarantined` (sevk edilmişse durum değişmez); aynı çakışma tekrar gelirse yeni kayıt yok. İptal yalnızca `in_stock` topu `voided` yapar; başka durumdaki topun durumu değişmez, iptal harekete not olarak yazılır (tıra yüklenmiş top sessizce stoktan düşmesin).
- **İstasyon sahipliği (tasarım düzeltmesi, testte bulundu):** yalnızca bilgisayarın ŞU ANKİ kodu başka bilgisayara aitse paket reddedilir. Paketteki eski kodlu kesimler (kodu sonradan değişmiş PC) yine işlenir; numara çakışırsa karantina. İlk tasarımda bütün paket reddediliyordu → kodunu değiştiren PC'nin yeni kesimleri de sonsuza dek bekliyordu.
- **Bütünlük kontrolü (`ServerInvariants`):** durum = son hareketin vardığı durum; her topta tam bir `qc_created` ve ilk hareket o; hareket zinciri kesintisiz; karantinadaki topun çözülmemiş çakışması var. Her sunucu testinin sonunda, günlük yedekle birlikte ve `--kontrol` komutuyla çalışır.
- **Sunucu barındırma:** `UseWindowsService`, varsayılan **`http://*:5080` (IPv4+IPv6)**. **`0.0.0.0` KULLANMA:** yalnızca IPv4 dinlenince Windows ad çözümünde önce IPv6'yı deniyor, her YENİ bağlantı ~2 sn gecikiyor (ölçüldü: 5 istek 10,2 sn → `*` ile 20 ms; kurulum testinde bulundu). Adres sırası: `--urls` / `ASPNETCORE_URLS` / `ayarlar.json "Urls"`. Veri `%ProgramData%\KumasBarkodSunucu` (`KumasBarkod:DataDirectory` ayarı / `KUMASBARKOD_SERVER_DATA_DIR`). Veri klasöründeki `ayarlar.json` okunur (program klasörü güncellemede silinir, bu kalır). Bütün yollar DI'daki `ServerPaths`'ten (testler yalnızca onu değiştirir). Günlük: ASP.NET uyarıları + kendi bilgilerimiz `logs\kumas-YYYY-MM-DD.log`.
- **Sunucu yedeği (`ServerBackupService`, kullanıcı sordu 2026-11-15):** ilk sürüm günde birdi (QC'nin `BackupService`'i yeniden kullanılmıştı) — sevkiyat kayıtları yalnızca sunucuda olacağı için yetersiz. Şimdi **saatlik** `backups\saatlik\sunucu-YYYY-MM-DD-HH.db` (48), **günlük** `backups\gunluk\sunucu-YYYY-MM-DD.db` (30, o günün ilk saatlik yedeğinin kopyası), **ikinci klasör** `KumasBarkod:BackupMirrorDirectory` (USB/ağ diski; ulaşılamazsa yerel yedek yine alınır, `[HATA]` günlüğü, disk dönünce eksikler tamamlanır). Silme ada göre, klasördeki başka dosyalara dokunmaz. `MaintenanceWorker` 5 dk'da bir kontrol eder; bütünlük kontrolü günlük yedekle birlikte. Ortak kopya kodu `BackupService.WriteVerifiedCopy`.
- **Sunucu kurulum paketi:** `tools/installer/KumasBarkodSunucu.iss` + `build-server-installer.ps1 [-SkipTests]` → `artifacts\installer\KumasBarkodSunucu-Kurulum-<sürüm>.exe` (~35 MB). AppId `{6E0B8E52-6C1D-4B8F-9D3B-2F6A3C0B7A41}` (DEĞİŞTİRME). Yönetici kurulumu; `sc create` (yoksa) + `sc config` (güncellemede yol) + `sc failure` (çökünce yeniden başlat) + `net start`; güvenlik duvarı `netsh … remoteip=localsubnet profile=any program=…` (fabrika ağı "Ortak" görünse de çalışsın, internete kapalı); `PrepareToInstall` hizmeti durdurur; ikinci yedek klasörü sayfası → `ayarlar.json` (varsa dokunmaz); kaldırma: hizmet + kural silinir, `dirifempty` ile boş klasör, veri kalır. Bitiş sayfasında `http://<bilgisayar adı>:5080/health`.
  - **Kurulum testi (2026-11-15, kullanıcı onayıyla, bu PC'de yönetici olarak, 3 tur; betik scratchpad `e2e\installtest.ps1` tek UAC ile kur → kontrol → üzerine kur → kontrol → kaldır → test verisini sil):** 1. tur kurulum hiç başlamadı: `CreateInputDirPage` BOŞ değere izin vermiyor ("Sürücü adı ile tam yolu yazmalısınız") → `CreateInputQueryPage` + elle yol kontrolü. 2. tur: sağlık kontrolü 90 sn yanıt vermedi (IPv4/IPv6 sorunu, yukarıda) ve boş program klasörü kaldı. 3. tur temiz: hizmet Running/Automatic, kural 1 adet LocalSubnet/Any/5080, kayıt yazıldı, saatlik+günlük yedek oluştu, üzerine kurulumda veri korundu + `--kontrol` temiz, kaldırmada hizmet/kural/klasör silindi, veri korundu; test verisi (`C:\ProgramData\KumasBarkodSunucu`) silindi — bu PC'de kalıntı yok.
- **QC 1.1.0 kurulum paketi** üretildi (`KumasBarkod-Kurulum-1.1.0.exe`, 46,5 MB), bu PC'deki 1.0.5 kurulumuna KURULMADI (gerçek veriyi v2'ye yükseltirdi; geri dönüş yok). **GitHub Release yapılmadı** — sabit indirme linki hâlâ 1.0.5.
- **Gerçek veri kopyasında yükseltme testi (2026-11-15):** gerçek `kumas.db` Backup API ile salt okunur kopyalandı (scratchpad `e2e\copydb.cs`; kopyada Sheets ayarları silindi ki gerçek tabloya yazmasın), yayın paketindeki 1.1.0 `KUMASBARKOD_START_PAGE=settings` ile açıldı: v1 → v2, 43 kesim → 43 sunucu kuyruğu → sunucuda 38 in_stock + 5 voided, gönderilmemiş 0, çift yok. Ayarlar → Depo sunucusu kartı ekran görüntüsünde doğrulandı.
- **`KUMASBARKOD_START_PAGE`** (scan | history | settings; yalnızca test/sunum): klavyesiz ekran görüntüsü için açılış sayfası. UI Automation menü öğesine ulaşamıyor, `PostMessage` tıklaması WPF'e ulaşmıyor — bu yüzden eklendi.
- **Durum çubuğu:** "Sheets bekleyen: N · <durum>" ve "Depo bekleyen: N · <durum>". Depo ayarlanmamışsa uyarı rengi YOK (sunucu kurulana kadar QC normal çalışır). 1366 genişlikte sığıyor (MaxWidth 300/280, tam metin ipucunda).
- **Testler:** istasyon 290 (yeni: `ServerOutboxTests` — v1→v2 göçü, iki kuyruk, atomiklik, adres, gönderici), sunucu 37 (`CutIngestTests`, `StationToServerTests`: 20 paralel aynı paket → tek top; iki PC aynı kod için yarış → tek kazanan; kapalı sunucu → kuyrukta bekler → bir kez gider; yeniden kurulum sayacı sunucudan öğrenir; bağlanmadan önce ID tekrar üreten veritabanı → karantina; iki istasyon paralel 60 kesim).
- **Gerçek uygulama denemesi (2026-11-15, geçici veri, klavyesiz):** sunucu `--urls http://127.0.0.1:5080` (0.0.0.0 güvenlik duvarı penceresi açmasın diye) + geçici QC veritabanına `seed.cs` ile 4 kesim (1 iptal) → uygulama açıldı, durum çubuğu "Depo güncel"; sunucuda 3 in_stock + 1 voided, hareketler doğru, istasyon = MachineGuid. Sunucu kapatıldı + 2 kesim → "Depoya ulaşılamıyor", bekleyen 2; sunucu açıldı → ≤1 dk içinde gitti, 6 top, çift yok. Yayın paketi (`dotnet publish -c Release`, ~109 MB) `--kontrol` ile çalıştı.
  - **Bulunan hata:** Sheets `SyncWorker` DI kaydı `ISyncRunner` yüzünden çözülemiyordu → uygulama açılışta uyarı verdi. Birim testleri App'in DI kaydını kapsamıyor: **App'te servis kaydı değişince uygulamayı gerçekten açarak dene.**
- Betikler scratchpad'de (oturuma özel): `e2e\seed.cs` (Core'u `#:project` ile kullanır, geçici QC veritabanına kesim yazar), `e2e\srvcheck.cs` (sunucu veritabanını salt okunur gösterir), `e2e\appcheck.cs`, `e2e\copydb.cs`, `e2e\shot.ps1` (PrintWindow), `e2e\installtest.ps1`.
- **Testler (son):** istasyon 290 + sunucu 42 (yedek: 5) = 332.
- **Gizlilik (2026-11-15):** depo HERKESE AÇIK. Ticari çalışma (donanım fiyatları, rakipler, yazılım satış fiyatı) depoya GİRMEZ; `docs/DEPO-SEVKIYAT-PLANI.md`'de yalnızca bu not var. İlk hâlde ticari bölüm bir commit'teydi; gönderilmeden önce kullanıcı onayıyla o dal yeniden kuruldu (yalnızca yerel, gönderilmemiş commit'ler; force push yok).
- **Fabrika kuralları:** `docs/FABRIKA-KURALLARI.md` — kullanıcı kararı (2026-11-15): sistemi en sade tutup kullanım kurallarını fabrikaya biz veriyoruz (ayrı sunucu PC + UPS, sabit IP, USB yedek diski, depo/rıhtım Wi-Fi, her QC'ye ayrı istasyon kodu…). Her fazda güncellenir; yalnızca uygulamada olan özelliklere dayanır.

## Şu anki durum

- **2026-11-15 — Faz 6 tamam:** Google Sheets kopyası depo sunucusundan (Kesimler + Stok + Sevkiyatlar), yönetim Ayarlar kartı, Apps Script v8. QC 1.6.0, sunucu 0.5.0. Gerçek Google dağıtımı (Code.gs v8) kullanıcının hesabıyla yapılmadı. Kurulum paketleri bu sürümlerle henüz üretilmedi; GitHub Release yok. Sıradaki: kullanıcı onayıyla kurulum paketleri + fabrika pilotu ya da Faz 7 isteğe bağlıları.

- **2026-11-15 — Faz 5 tamam:** onayı geri alma (yetkili), İstisnalar ekranı (karantina çözümü, sevk sonrası QC iptali, uzun süredir açık sevkiyatlar), Raporlar + CSV. QC 1.5.0, sunucu 0.4.0. Kurulum paketleri bu sürümlerle henüz üretilmedi; GitHub Release yok. Sıradaki: kullanıcı onayıyla Faz 6 (Google Sheets'e stok/sevkiyat kopyası sunucudan) ya da kurulum paketleri + fabrika pilotu.
- **2026-11-15 — Faz 4 tamam (sevkiyat):** sunucuda personel girişi + sevkiyat (tek kazanan kilidi, tekrar gönderime dayanıklı okutma, sürüm kontrollü onay, iptal), rıhtım WPF ekranı, Personel ekranı, el terminali web sayfası (gerçek tarayıcıyla test edildi). QC 1.4.0, sunucu 0.3.0. Kurulum paketleri bu sürümlerle henüz üretilmedi; GitHub Release yok. Sıradaki: kullanıcı onayıyla Faz 5 (onayı geri alma, karantina/istisna ekranı, raporlar).
- **2026-11-15 — Faz 3 tamam:** QC'de tanımlı lot önerisi + tanımsız lot onayı (internetsiz de çalışır), klavyeli arayüz testleri A/B/E/F hepsi geçti. QC 1.3.0. Sıradaki: Faz 4 (sevkiyat) — kullanıcı onayı; saha soruları (elle/forklift, kim okutuyor, etiket yeri, rıhtım ağı, aynı anda kaç tır) açık, varsayılan düzen: kablolu rıhtım PC + büyük ekran + kablosuz okuyucu, kapıda tek görevli, tam ekran renk + ses.
- **2026-11-15 — Faz 2 tamam (GitHub'da):** bilgisayar görevi (QC / sevkiyat / yönetim), lot (= firma) tanımları + yazım eşleştirme, Depo Stoğu ve Lotlar ekranları, ekranlar arası 3 sn senkron. QC 1.2.0, sunucu 0.2.0 (kurulum paketleri bu sürümle henüz üretilmedi; GitHub Release yok). Sıradaki: kullanıcı onayıyla Faz 3 (QC'de lot önerisi: lot listesi önbelleği + tanımsız lot uyarısı).
- **2026-11-15 — Depo stok + sevkiyat projesi, Faz 1 tamam:** depo sunucusu + kesim alımı, saatlik/günlük/ikinci klasör yedeği, sunucu kurulum paketi (bu PC'de kur/güncelle/kaldır testi geçti), QC 1.1.0 (Ayarlar kartı + durum çubuğu), gerçek veri kopyasında yükseltme testi, fabrika kuralları belgesi. Kullanıcı isteği: Faz 1 bitince GitHub'a gönder, sonra Faz 2 (lot/firma tanımları + Depo Stoğu ekranı). GitHub Release (1.1.0) yapılmadı. Faz 0 saha soruları açık.
- **Sürüm 1.0.5 (2026-11-06) yayında, tüm revizeler tamam:** cm girişi, fareyle lot, Okutma sadeleştirme, F5/F6, Geçmiş'te F6, Sheets Türkçe başlık + en yeni en üstte, script v7. Kapsamlı test ve regresyon geçti (`docs/TEST-RAPORU.md`). Bu bilgisayarda yalnızca 1.0.5 kurulu, kurulum dosyası masaüstünde.

- **Adım 11 kurulum paketi tamamlandı** (2026-11-01): `KumasBarkod-Kurulum-1.0.0.exe` üretildi ve kur/çalıştır/kaldır testi geçti. Adım 9 ve Adım 10'un donanımsız kısmı (gösterim modu) da tamam. Sunum: `docs/SUNUM.md`, pilot: `docs/PILOT.md`.
- **Karar (2026-11-01): ID 6 hane kalır** (seçenek a). Bir istasyon en fazla 999.999 ID üretir (günde 500 kesimle ~6,7 yıl); dolunca uygulama uyarır, Ayarlar'dan yeni istasyon kodu verilir. 7 haneye çıkarma önerilmişti, kullanıcı istemedi — tekrar önerme.
- **Kalan (donanım/fabrika gerektiren):** Adım 10'un kalanı (etiket yazıcısı seçimi/satın alma, etiket yazıcısına ham ZPL — `RawWindowsPrinter`/winspool, şablonu gerçek etikete uydurma, Türkçe karakter ve kumaş denemesi); fabrika pilotu (temiz PC + bir vardiya kağıtla paralel). Fabrika için yeni Google tablosu + anahtar.
- **2026-11-02:** ID benzersizliği düzeltmesi tamamlandı ve gerçek Google ile doğrulandı (test tablosunda script v4). Fabrika tablosu kurulurken v4 Code.gs kullanılmalı.
- **Code.gs v7 (2026-11-06):** test tablosunda yayında (Apps Script dağıtım sürümü 7), gerçek ekleme, güncelleme, iptal ve geri kurma doğrulandı, tablo özgün 24 kayıtta. Fabrika tablosu için de v7 kullanılır.
- **Sıradaki:** Kullanıcıya sorulacak. Seçenek: yazıcı modeli belli olmadan da yazılabilecek ham ZPL gönderimi (winspool) — gerçek yazıcıda denenene kadar "denenmedi" olarak işaretlenir.
- Gerçek USB okuyucu testi hâlâ açık (Adım 6) — gösterim modunda elle giriş/"Okut (gösterim)" ile ikame edildi.
- **GitHub:** 2026-11-06 itibarıyla her şey gönderildi; **v1.0.5 yayında** (Releases, sabit indirme linki).
- **2026-11-02:** İptal sebebi sabit 4 seçenekli liste oldu (bkz. İptal sebepleri notları), 1.0.2 olarak yayınlandı.
- **Donanım araştırması (2026-11-02, yalnızca öneri, satın alma yok):** Kullanıcının fikri = mevcut ofis PC + Cat6 KVM uzatıcı (HDMI+USB) ile makine başına monitör + yazıcı. Önerilen = **makine başına ayrı mini PC (N100, 16/512, Win11 Pro) + 22" monitör + Zebra ZD220t (USB, yerel ZPL) + Zebra DS2208 + 850 VA UPS**, ~35 bin TL. Uzatıcı yolu ~34 bin TL ama ofis PC'si yalnızca bu işe ayrılmalı (aynı masaüstü iki ekranda), ucuz KVM uzatıcılar USB yazıcıyı genelde taşımaz → Ethernet'li yazıcı (ZD421t) gerekir. Endüstriyel paket (IP65 fansız dokunmatik panel PC + ZD421t) ~73–81 bin TL. Notlar: ZD220t 74 m ribon alır (TSC TE200 300 m alır ama ZPL emülasyonu denenmeli); kuşe etiket kumaşta tutmayabilir → sentetik etiket denenmeli; yedekler aynı diskte → USB belleğe kopyalama ileride eklenebilir.

## Rafa kalkan: Yönetici (izleme) modu — 2026-11-02
- **2026-11-15 güncellemesi:** Depo projesinde "Patron bilgisayarı" rolü planlandı (sunucudan okur: tüm makinelerin Geçmiş'i, Depo Stoğu, Sevkiyatlar). Aşağıdaki Sheets tabanlı fikir bunun yerine geçti; yapıldıkça burası güncellenecek.
- **Son karar (kullanıcı):** şimdilik takip **yalnızca Google Sheets'ten** yapılacak, KOD YAZILMAYACAK. Ayrı yönetici paneli istenmedi. Kullanıcı tekrar istemedikçe önerme.
- **Güncelleme (2026-11-06):** Kullanıcı patronun ayrı PC'den Geçmiş'i izlemesini sordu; "yapılabilir, salt okunur izleme bilgisayarı önerilir" yanıtı verildi, konu yine **park edildi**. Tekrar gündeme gelirse koda başlamadan önce sorularla kapsam netleştirilecek. Ağ klasöründe ortak SQLite yapılmaz (WAL ağda güvenli değil).
- Önkoşul (zaten geçerli): her makineye ayrı istasyon kodu; hepsi aynı Sheets adresi + anahtarı.
- İleride istenirse önerilen yol: Geçmiş ekranına "Tüm makineler" seçeneği (yalnızca Ayarlar'da açılan bilgisayarda; makineler birbirini görmez), ~1 dk gecikme, salt okunur. Gereken: Code.gs v5 (`rev` sütunu + kilit altında `changes` eylemi), uygulamada ayrı yerel kopya tablosu. Makine durumu (heartbeat) / pano isteğe bağlı. Bir taslak yazılmaya başlanmıştı, hiçbir değişiklik kaydedilmedi.

## Rafa kalkan: Stok entegrasyonu — 2026-11-06
- **2026-11-14 güncellemesi:** Kullanıcı kendi depo stoğu + sevkiyat modülünü istedi; bu konu "Depo stok ve sevkiyat" bölümüne taşındı. Aşağıdaki notlar dış stok sistemine (Excel/ERP) bağlanma fikri içindi.
- **Karar:** Kod yazılmadı, park edildi. Fabrika demosunda sorulması bekleniyor; yanıt: yapılabilir, kapsam stoğun tutulduğu yere bağlı (Excel/Sheets kolay, ERP karşı tarafa bağlı). Başlamadan önce sorularla kapsam netleştirilecek.
- **Yaklaşım:** Stok tarafı Google tablosunu okur, kesim ekranı stoğa bağımlı olmaz. Riskler: elle yazılan lotun stokla eşleşmemesi (en büyüğü), birim farkı (cm/kg/top), fire, iptalde iade, aynı ürünün iki kez geçmesi, stoktan düşme mi durum taşıma mı.
- **Demoda sorulacak:** Stok nerede tutuluyor, birim ne, lot kodunu kim üretiyor, toplarda barkod var mı, geçişte stokta ne değişmeli, fire ve iptal nasıl kaydediliyor.

## Açık konular / riskler

- **Türkçe karakterler etikette:** Zebra yerleşik font 0 (`^A0`) ğ/ş/ı/İ'yi her firmware'de basmayabilir. Adım 10'da gerçek yazıcıda denenmeli; gerekirse yazıcıya Türkçe destekli font yüklenir.
- **Etiket boyutu** (50×30 mm, 203 dpi) varsayım; yazıcı/etiket seçilince şablon güncellenecek (Adım 10).
- **Code128 otomatik mod (A)** eski Zebra firmware'de yoksa `^BCN,80,N,N,N,N` + elle alt küme kullanılır.
- **Veritabanı konumu** kullanıcı klasöründe; fabrika bilgisayarında birden çok Windows kullanıcısı olacaksa ortak klasöre (ProgramData) taşınmalı.
- Etiket önizlemesi var (`LabelPreview`, Adım 10a); ZPL'nin gerçek yazıcıdaki görünümü ancak yazıcıyla doğrulanır (labelary.com ZPL'yi görüntüler ama veriyi dışarı gönderir — kullanılmadı).
- Kurulum paketi kod imzasız → SmartScreen uyarısı. İmza için ücretli kod imzalama sertifikası gerekir.
