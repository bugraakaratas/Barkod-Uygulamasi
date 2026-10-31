# Kumaş Kalite Kontrol ve Barkod Takip Sistemi

Bu dosya projenin hafızasıdır. Yeni oturumda önce bunu oku. **Her iş bitince "İlerleme günlüğü" ve "Şu anki durum" bölümlerini güncelle.**

- **Plan:** `C:\Users\User\Desktop\plan.txt` (Aşama 1–7; geliştirme adımları tablosu Aşama 7'de). Kaynak plan: `Desktop\plan.md`.
- **Proje:** `D:\dev\kumas-barkod` — yerel git, `main` dalı. **Push yok** (açıkça istenmedikçe).
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

| 8 | 🔄 kod ✅, gerçek Google testi bekliyor | `0aa90af` | Sheets kopyası: Apps Script (`tools/sheets/Code.gs`, kurulum `KURULUM.md`), kuyruk göndericisi, arka plan çalışanı + bekleme, durum çubuğu, Ayarlar ekranı (adres/anahtar/test/kaydet/şimdi gönder, metre üst sınırı). 203 test |

### Adım 8 notları
- **Apps Script (`Code.gs`):** `doPost` → anahtar (`Script Properties: API_KEY`) → `LockService` kilidi → `cut_id`'ye göre upsert (varsa satırı güncelle, yoksa sona ekle; aynı pakette iki kez gelen tek satır). Sayfa `Kesimler`, ilk gönderimde başlık satırıyla oluşur. Metin sütunları `@` biçimli (Sheets tarihe/sayıya çevirmesin). Son sütun `metre` = length_cm/100 (rapor için; geri kurmada kullanılmaz). `doGet` sağlık kontrolü, veri döndürmez. Yanıt `{ok, upserted, version}` / `{ok:false, error}`. Yayın: Web uygulaması, "Ben" olarak yürüt, erişim "Herkes" (yazmak için anahtar şart).
- **Uygulama (Core/Sync):** `AppsScriptTransport` (HTTPS POST JSON, snake_case; ağ hatası/zaman aşımı/5xx → `Offline`; `ok:false`, HTML giriş sayfası → `Rejected` ve anlaşılır Türkçe mesaj), `OutboxRepository` (bekleyen kesimler: `GROUP BY cut_id`, kesimin GÜNCEL hâli gönderilir; `MarkSent` yalnızca okunan en büyük kuyruk numarasına kadar kapatır → gönderim sırasında gelen değişiklik kaybolmaz ve aynı turun sonraki paketinde gider), `SyncService` (200'lük paket, turda en fazla 25 paket, hata olunca durur; gönderilmiş 30 günden eski kuyruk satırlarını siler), `SyncWorker` (açılışta hemen, sonra 2 dk; kayıttan 5 sn sonra dürtme; hata beklemesi 30 sn→1→2→5→10 dk, beklemede dürtme yok sayılır; "Şimdi gönder" beklemeyi atlar).
- Ayarlar: `sheets_url`, `sheets_key` (`settings` tablosu, düz metin; depoya girmez). Boş adres/anahtar = gönderim kapalı. Adres https olmalı.
- Durum çubuğu metinleri: "Sheets gönderimi ayarlanmadı (Ayarlar)" (turuncu), "Sheets'e gönderiliyor…", "Sheets güncel · son gönderim HH:mm", "İnternet yok · HH:mm'de tekrar denenecek" (turuncu), "Sheets hatası: …" (turuncu).
- **Testler (FakeAppsScript HTTP işleyicisi, Code.gs mantığını taklit eder):** yanıt kaybolup aynı paket tekrar gidince tek satır, kesinti sonrası kurtarma ve deneme sayacı, paket ortasında kopma + sıra korunması, baskı+iptal aynı satırı günceller, gönderim sırasında değişiklik, yanlış anahtar, HTML giriş sayfası, 5xx, 450 kayıt → 200/200/50, 30 gün temizliği, bekleme süreleri, çalışan durumu.
- Arayüz testi (gerçek uygulama, geçici veri): ayarlanmamış → kayıtlar birikiyor; ulaşılamayan adres (`https://kumas-test.invalid`) → bağlantı testi anlaşılır hata, durum çubuğu "İnternet yok · …'de tekrar denenecek", internetsiz kayıt ve etiket çalışıyor, deneme sayacı ve hata kuyruğa yazılıyor.
- Yeni kısayol: **Ctrl+4 Ayarlar**. Ayarlar açılınca odak adres kutusunda (yoksa içerik çerçevesinde siyah odak kenarı görünüyordu).
- Arayüz testi betiği: bulunamayan öğede DURMALI (bir kez bulunamayan öğe yüzünden metin yanlış kutuya yazıldı; zararsızdı, geçici veritabanıydı).
- **AÇIK:** Gerçek Google hesabı ile test: KURULUM.md adımları → Ayarlar'a gir → bağlantı testi → kayıt aç → tabloda tek satır; interneti kesip kayıt aç, bağlayınca satırların bir kez geldiğini doğrula.

## Şu anki durum

- **Adım 8 kodu tamamlandı;** gerçek Google bağlantısı testi kullanıcının kurulumunu bekliyor (Google hesabı soruldu, yanıt bekleniyor). Kullanıcı isterse Claude in Chrome ile kuruluma yardım önerildi.
- **Karar (2026-11-01): ID 6 hane kalır** (seçenek a). Bir istasyon en fazla 999.999 ID üretir (günde 500 kesimle ~6,7 yıl); dolunca uygulama uyarır, Ayarlar'dan yeni istasyon kodu verilir. 7 haneye çıkarma önerilmişti, kullanıcı istemedi — tekrar önerme.
- **Kalan adımlar (2026-11-01 itibarıyla 4):** 8 (gerçek Google testi), 9 günlük yedek + Sheets'ten geri kurma + hata günlüğü, 10 gerçek yazıcı, 11 kurulum paketi + pilot.
- **Sıradaki:** Adım 9 (Google testi beklenirken yapılabilir; geri kurma testi Sheets verisini sahte işleyiciyle simüle eder).
- Gerçek USB okuyucu testi hâlâ açık (Adım 6).

## Açık konular / riskler

- **Türkçe karakterler etikette:** Zebra yerleşik font 0 (`^A0`) ğ/ş/ı/İ'yi her firmware'de basmayabilir. Adım 10'da gerçek yazıcıda denenmeli; gerekirse yazıcıya Türkçe destekli font yüklenir.
- **Etiket boyutu** (50×30 mm, 203 dpi) varsayım; yazıcı/etiket seçilince şablon güncellenecek (Adım 10).
- **Code128 otomatik mod (A)** eski Zebra firmware'de yoksa `^BCN,80,N,N,N,N` + elle alt küme kullanılır.
- **Veritabanı konumu** kullanıcı klasöründe; fabrika bilgisayarında birden çok Windows kullanıcısı olacaksa ortak klasöre (ProgramData) taşınmalı.
- Etiket önizlemesi (ekranda görüntü) yok; istenirse eklenebilir. labelary.com ZPL'yi görüntüler ama veriyi dışarı gönderir.
