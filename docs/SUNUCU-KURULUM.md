# Depo sunucusu kurulumu

Depo sunucusu, fabrikanın yerel ağında sürekli açık duran bilgisayarda çalışan küçük bir programdır
(`KumasBarkodSunucu.exe`). Ekranı yoktur; Windows hizmeti olarak arka planda çalışır. Bütün
bilgisayarların ortak veritabanını tutar: depo stoğu ve (Faz 4'ten sonra) sevkiyat buradan yürür.

> **Durum (2026-11-15, Faz 1):** Program, yayın paketi ve yönetim komutları bu geliştirme
> bilgisayarında denendi. **Windows hizmeti olarak kurulum ve güvenlik duvarı kuralı henüz gerçek
> bir sunucu bilgisayarında denenmedi.** Ayrı kurulum paketi (Inno Setup) henüz yok.

## 1. Bilgisayar

- Ayrı, sürekli açık bir mini PC önerilir (depocunun ya da patronun bilgisayarı kapatılır, güncelleme alır).
- UPS'e bağlı olmalı; ağ anahtarı (switch) ve Wi-Fi erişim noktası da aynı UPS'e.
- Sabit IP verilmeli (modemde DHCP rezervasyonu) ya da bilgisayar adı sabit olmalı (ör. `depo-sunucu`).
- Windows güncellemelerinin otomatik yeniden başlatması mesai dışına ayarlanmalı.

## 2. Yayın paketini hazırlama (geliştirme bilgisayarında)

```powershell
dotnet publish src\KumasBarkod.Server -c Release -o artifacts\server
```

`artifacts\server` klasörü (~110 MB, kendi .NET'ini taşır) sunucuya kopyalanır, ör. `C:\KumasBarkodSunucu`.

## 3. Windows hizmeti olarak kurma (yönetici PowerShell)

```powershell
sc.exe create KumasBarkodSunucu binPath= "C:\KumasBarkodSunucu\KumasBarkodSunucu.exe" start= delayed-auto DisplayName= "Kumaş Barkod Depo Sunucusu"
sc.exe failure KumasBarkodSunucu reset= 86400 actions= restart/5000/restart/5000/restart/60000
sc.exe start KumasBarkodSunucu

# Yalnızca özel (fabrika) ağdan erişim
New-NetFirewallRule -DisplayName "Kumaş Barkod Sunucu" -Direction Inbound -Protocol TCP -LocalPort 5080 -Action Allow -Profile Private
```

- Varsayılan kapı **5080**, bütün ağ kartlarında dinler. Değiştirmek için `--urls http://0.0.0.0:6000` argümanı.
- Veri klasörü: `C:\ProgramData\KumasBarkodSunucu` → `sunucu.db` (veritabanı), `backups\`, `logs\`.
  `KUMASBARKOD_SERVER_DATA_DIR` ortam değişkeniyle değişir (testlerde kullanılır).

## 4. Çalıştığını kontrol etme

Herhangi bir bilgisayarın tarayıcısında: `http://depo-sunucu:5080/health` →
`{"ok":true,"service":"KumasBarkod.Server",...}` görünmeli.

Her QC bilgisayarında: **Ayarlar → Depo sunucusu** → adres (`depo-sunucu` ya da IP) → **Bağlantıyı test et** → **Kaydet**.
Durum çubuğunda "Depo güncel" ve "Depo bekleyen: 0" görünmeli.

## 5. Yedek

- Sunucu açılışta ve saatte bir kontrol eder; günde bir yedek alır (`backups\kumas-YYYY-MM-DD.db`, 30 gün).
  Yedek alındığında veri bütünlüğü kontrolü de çalışır; sorun varsa günlüğe `[HATA]` yazılır.
- Yedekler aynı diskte. **Günlük olarak başka bir bilgisayara/USB diske kopyalanması gerekir** (henüz otomatik değil).
- Google Sheets kopyası şimdilik QC bilgisayarlarından gönderilmeye devam eder (bina dışı kopya).

## 6. Yönetim komutları

Hizmet çalışırken de güvenlidir (yönetici komut isteminde, program klasöründe):

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
