# Depo sunucusu kurulumu

Depo sunucusu, fabrikanın yerel ağında sürekli açık duran bilgisayarda çalışan küçük bir programdır
(`KumasBarkodSunucu.exe`). Ekranı yoktur; Windows hizmeti olarak arka planda çalışır. Bütün
bilgisayarların ortak veritabanını tutar: depo stoğu ve (Faz 4'ten sonra) sevkiyat buradan yürür.

> **Durum (2026-11-15):** Kurulum paketi geliştirme bilgisayarında yönetici olarak kurulup denendi
> (kur → hizmet çalışıyor, güvenlik duvarı yalnızca yerel ağ, kayıt yazılıyor, yedek alınıyor →
> güncelleme kurulumu veriyi koruyor → kaldırma hizmeti/kuralı/program klasörünü siliyor, veriyi koruyor).
> Başka bir bilgisayardan ağ üzerinden bağlantı henüz denenmedi (fabrikada).

## 1. Bilgisayar

- Ayrı, sürekli açık bir mini PC (depocunun ya da patronun bilgisayarı kapatılır, güncelleme alır).
- UPS'e bağlı; ağ anahtarı (switch) ve Wi-Fi erişim noktası da aynı UPS'e.
- Modemden **sabit IP** (DHCP rezervasyonu) ya da sabit bilgisayar adı (ör. `depo-sunucu`).
- Windows güncellemelerinin otomatik yeniden başlatması mesai dışına ayarlanır.
- Sunucuya **takılı kalan bir USB disk** (ikinci yedek) önerilir.

## 2. Kurulum paketi

Geliştirme bilgisayarında: `pwsh tools\installer\build-server-installer.ps1` → testler + yayın →
`artifacts\installer\KumasBarkodSunucu-Kurulum-<sürüm>.exe` (~35 MB, kendi .NET'ini taşır).

Sunucu bilgisayarında kurulum dosyası çalıştırılır (yönetici onayı ister). Kurulum:
- programı `C:\Program Files\Kumaş Barkod Sunucu` klasörüne kurar,
- `KumasBarkodSunucu` Windows hizmetini kurar (bilgisayar açılınca kendiliğinden başlar; çökerse 5 sn sonra yeniden başlar),
- güvenlik duvarında TCP **5080**'i **yalnızca yerel ağa** açar (internetten erişilemez),
- isteğe bağlı **ikinci yedek klasörünü** sorar (ör. `E:\KumasYedek`; boş bırakılabilir),
- bitişte denetim adresini gösterir.

Güncelleme: yeni kurulum dosyası aynı şekilde çalıştırılır; hizmet durdurulur, program yenilenir, veri ve ayarlar korunur.
Kaldırma: Ayarlar → Uygulamalar → "Kumaş Barkod Sunucu". **Veri silinmez.**

Sessiz kurulum (ör. uzaktan): `KumasBarkodSunucu-Kurulum-<sürüm>.exe /VERYSILENT /SUPPRESSMSGBOXES` (ikinci yedek sorulmaz).

## 3. Veri ve ayarlar

- Veri klasörü: `C:\ProgramData\KumasBarkodSunucu` → `sunucu.db` (veritabanı), `backups\`, `logs\`, `ayarlar.json`.
- `ayarlar.json` (isteğe bağlı, güncellemede korunur):
  ```json
  {
    "KumasBarkod": { "BackupMirrorDirectory": "E:\\KumasYedek" },
    "Urls": "http://*:5080"
  }
  ```
  Değişiklikten sonra hizmet yeniden başlatılır (`net stop KumasBarkodSunucu` / `net start KumasBarkodSunucu`).
- Dinleme adresi varsayılan `http://*:5080` (IPv4 + IPv6). **`0.0.0.0` yazmayın:** yalnızca IPv4 dinlenirse
  Windows'ta her yeni bağlantı ~2 sn gecikir (ölçüldü).

## 4. Çalıştığını kontrol etme

Herhangi bir bilgisayarın tarayıcısında: `http://depo-sunucu:5080/health` → `{"ok":true,"service":"KumasBarkod.Server",...}`.

Her QC bilgisayarında: **Ayarlar → Depo sunucusu** → adres (`depo-sunucu` ya da IP) → **Bağlantıyı test et** → **Kaydet**.
Durum çubuğunda "Depo güncel" ve "Depo bekleyen: 0" görünmeli.

## 5. Yedek

| Tür | Yer | Saklanan | Neye karşı |
|---|---|---|---|
| Saatlik | `backups\saatlik\sunucu-YYYY-MM-DD-HH.db` | son 48 (2 gün) | disk/dosya bozulması: kayıp en fazla 1 saat |
| Günlük | `backups\gunluk\sunucu-YYYY-MM-DD.db` | son 30 gün | geç fark edilen yanlış işlem |
| İkinci klasör | `ayarlar.json` → `BackupMirrorDirectory` | aynı | **disk arızası** (aynı diskteki yedek korumaz) |

- Yedek sunucu çalışırken, okutmayı durdurmadan alınır (SQLite Backup API) ve doğrulanır (`integrity_check`).
- İkinci klasöre ulaşılamazsa (USB çıkarılmış) yerel yedek yine alınır, günlüğe `[HATA]` yazılır; disk geri takılınca eksik yedekler tamamlanır.
- Günde bir veri bütünlüğü kontrolü çalışır; sorun varsa günlüğe `[HATA]`.
- Google Sheets kopyası şimdilik QC bilgisayarlarından gönderilmeye devam eder (bina dışı kopya; yalnızca kesimler).

**Geri dönüş (elle):** hizmeti durdur → `sunucu.db`, `sunucu.db-wal`, `sunucu.db-shm` dosyalarını başka yere taşı →
seçilen yedeği `sunucu.db` adıyla kopyala → hizmeti başlat. QC bilgisayarlarının kuyruğunda bekleyen kesimler kendiliğinden gelir.

## 6. Yönetim komutları

Yönetici komut isteminde, `C:\Program Files\Kumaş Barkod Sunucu` klasöründe (hizmet çalışırken de güvenli):

| Komut | Ne yapar |
|---|---|
| `KumasBarkodSunucu.exe --kontrol` | Veri bütünlüğü kontrolü. Çıkış kodu 0 = sorun yok. |
| `KumasBarkodSunucu.exe --istasyon-serbest-birak K1` | QC bilgisayarı değiştirildiyse eski bilgisayarın `K1` kaydını kaldırır; `K1` ile ilk gönderen bilgisayar kaydı alır. |

## 7. Sık karşılaşılan durumlar

| Belirti (QC durum çubuğu) | Sebep | Çözüm |
|---|---|---|
| "Depoya ulaşılamıyor" | Sunucu kapalı, ağ kablosu/Wi-Fi yok, güvenlik duvarı | Sunucuyu ve ağı kontrol edin. Kayıtlar QC bilgisayarında bekler, kaybolmaz; bağlantı gelince en geç 1 dk içinde gider. |
| "K1 istasyon kodu … başka bir bilgisayara kayıtlı" | İki bilgisayar aynı istasyon kodunu kullanıyor ya da QC bilgisayarı değişti | Yeni bilgisayara Ayarlar'dan farklı kod verin; bilgisayar değiştiyse sunucuda `--istasyon-serbest-birak`. |
| "… depo sunucusunda bu ID başka bir topa ait … karantina" | Aynı barkodu taşıyan iki fiziksel top var | Toplardan biri iptal edilip yeniden etiketlenmeli. Karantinadaki top sevk edilemez. (Yetkili ekranı Faz 5'te.) |
