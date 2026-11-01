# Fabrika pilotu — kontrol listesi

Planın 11. adımı: temiz bilgisayara kurulum ve **bir vardiya kağıtla paralel** çalışma.
Bitti sayılır: pilot sonunda uygulamadaki kayıtlar kağıttaki kayıtlarla **birebir** uyuşuyor.

## 1. Kurulumdan önce

- [ ] İstasyon bilgisayarı: Windows 10 (1809+) veya 11, 64 bit. İnternet şart değil.
- [ ] Kurulum dosyası: `KumasBarkod-Kurulum-<sürüm>.exe` (USB bellekle de taşınabilir).
- [ ] Bu istasyonun **istasyon kodu** belli (ilk istasyon `K1`; her bilgisayara farklı kod).
- [ ] Google: fabrika için **yeni** e-tablo + yeni anahtar (test tablosu kullanılmaz) — `tools/sheets/KURULUM.md`.
- [ ] Yazıcı: etiket yazıcısı yoksa **Gösterim** veya ofis yazıcısı; varsa sürücüsü kurulu.

## 2. Kurulum (≈10 dk)

- [ ] Kurulum dosyasını çalıştırın → İleri → Kur → "Kumaş Barkod uygulamasını başlat". (Yönetici izni gerekmez.)
- [ ] **Ayarlar** (Ctrl+4):
  - [ ] İstasyon kodu → Kaydet
  - [ ] Metre üst sınırı (fabrikadaki en uzun kesime göre)
  - [ ] Yazıcı → Kaydet
  - [ ] Sheets adresi + anahtar → **Bağlantıyı test et** → Kaydet
- [ ] Deneme kaydı: lot `DENEME`, 1 m → etiket çıktı mı / ekranda göründü mü?
- [ ] Deneme kaydını okutun (veya Okutma ekranına elle yazın) → kayıt açılıyor mu?
- [ ] Deneme kaydını **iptal edin** (sebep: "Kurulum denemesi").
- [ ] Alt çubuk: "Sheets güncel" görünüyor mu? Google tablosunda satır var mı?
- [ ] Google tablosunda `Kesimler` sayfasını **koruyun** (KURULUM.md 6. adım).

## 3. Pilot vardiyası

- Operatör her kesimde **hem** uygulamaya girer **hem** kağıda yazar (lot, metre, saat, uygulamanın verdiği ID).
- Sorun olursa: ne olduğunu, saatini ve ekranda ne yazdığını not edin; uygulama hata günlüğünü kendisi tutar.

## 4. Vardiya sonu karşılaştırma

- [ ] **Geçmiş** (Ctrl+3) → Bugün → kayıt sayısı = kağıttaki kesim sayısı
- [ ] Toplam aktif metre = kağıttaki metrelerin toplamı
- [ ] İptal edilenler kağıtta da iptal işaretli
- [ ] Rastgele 5 etiketi okutun → lot ve metre kağıtla aynı
- [ ] Google tablosundaki satır sayısı = uygulamadaki kayıt sayısı (iptaller dahil)
- [ ] Ayarlar → Yerel yedekler: bugünün yedeği var
- [ ] Ayarlar → Hata günlüğü → Aç: `[HATA]` satırı var mı? Varsa günlük dosyasını geliştiriciye iletin.

## 5. Sonuç

| | Uygulama | Kağıt | Uyuşuyor mu |
|---|---|---|---|
| Kesim sayısı | | | |
| Toplam metre (aktif) | | | |
| İptal sayısı | | | |
| Google tablosu satır | | — | |

Notlar / sorunlar:

&nbsp;
