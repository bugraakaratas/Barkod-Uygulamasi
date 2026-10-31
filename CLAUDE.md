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
- **Ekran kısayolları (MainWindow):** Ctrl+1 Kesim Girişi, Ctrl+2 Okutma, Ctrl+3 Geçmiş. Okutma her ekranda yakalanır (bkz. Adım 6 notları).
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

| 6 | ✅ (cihaz testi bekliyor) | (bu commit) | Okutma: her ekrandan okutma (hız + biçim tespiti), okutma ekranı, elle ID, iptal (sebepli), yerine yeni kayıt, Ctrl+1/2/3 ekran kısayolları. 157 test |

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

## Şu anki durum

- **Adım 6 tamamlandı** (gerçek okuyucu testi hariç). Kullanıcı onayı bekleniyor.
- **Sıradaki:** Adım 7 — Geçmiş ve filtreleme: tarih, lot, durum; tekrar baskı; 100.000 kayıtla hız (filtre < 1 sn). Öneri: varsayılan son 7 gün, sayfalama (200 satır), üstte toplam kayıt ve metre özeti.

## Açık konular / riskler

- **Türkçe karakterler etikette:** Zebra yerleşik font 0 (`^A0`) ğ/ş/ı/İ'yi her firmware'de basmayabilir. Adım 10'da gerçek yazıcıda denenmeli; gerekirse yazıcıya Türkçe destekli font yüklenir.
- **Etiket boyutu** (50×30 mm, 203 dpi) varsayım; yazıcı/etiket seçilince şablon güncellenecek (Adım 10).
- **Code128 otomatik mod (A)** eski Zebra firmware'de yoksa `^BCN,80,N,N,N,N` + elle alt küme kullanılır.
- **Veritabanı konumu** kullanıcı klasöründe; fabrika bilgisayarında birden çok Windows kullanıcısı olacaksa ortak klasöre (ProgramData) taşınmalı.
- Etiket önizlemesi (ekranda görüntü) yok; istenirse eklenebilir. labelary.com ZPL'yi görüntüler ama veriyi dışarı gönderir.
