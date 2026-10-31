# Google Sheets kopyası — kurulum

Uygulama, kesim kayıtlarını internet olduğunda bir Google E-Tablosuna kopyalar (tek yönlü).
Asıl veri bilgisayardaki veritabanındadır; tablo bir **kopya**dır ve disk arızasında geri kurmak için kullanılır.

Kurulum bir kez yapılır, yaklaşık 10 dakika sürer. Gerekenler: bir Google hesabı ve bu klasördeki `Code.gs` dosyası.

---

## 1. E-Tabloyu oluşturun

1. <https://sheets.google.com> → **Boş e-tablo**.
2. Adını verin, örneğin **Kumaş Kesimleri**.

Sayfayı (sekmeyi) elle oluşturmanız gerekmez; ilk gönderimde **Kesimler** adlı sayfa ve başlık satırı otomatik oluşur.

## 2. Script'i ekleyin

1. E-tabloda **Uzantılar → Apps Script**.
2. Açılan editörde `Kod.gs` (veya `Code.gs`) içindeki her şeyi silin.
3. Bu klasördeki `Code.gs` dosyasının tamamını yapıştırın.
4. **Kaydet** (disket simgesi veya Ctrl+S).

## 3. Gizli anahtarı oluşturun ve tanımlayın

Anahtar, tabloya yalnızca sizin uygulamanızın yazabilmesini sağlar. Kimseyle paylaşmayın.

1. Bilgisayarda **PowerShell** açın ve şunu çalıştırın (rastgele 48 karakterlik bir anahtar üretir):

   ```powershell
   -join ((1..48) | ForEach-Object { '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'[(Get-Random -Maximum 62)] })
   ```

2. Çıkan anahtarı kopyalayın.
3. Apps Script editöründe sol menüden **Proje Ayarları** (dişli simgesi).
4. En altta **Komut dosyası özellikleri → Komut dosyası özelliği ekle**:
   - Özellik: `API_KEY`
   - Değer: kopyaladığınız anahtar
5. **Komut dosyası özelliklerini kaydet**.

## 4. Web uygulaması olarak yayınlayın

1. Sağ üstte **Dağıt → Yeni dağıtım**.
2. Tür seçimi (dişli) → **Web uygulaması**.
3. Ayarlar:
   - **Açıklama:** Kumaş Barkod
   - **Yürütme yetkisi:** Ben (hesabınız)
   - **Erişimi olan kullanıcılar:** **Herkes**
4. **Dağıt**.
5. Google izin isteyecek → **Erişimi yetkilendir** → hesabınızı seçin.
   "Google bu uygulamayı doğrulamadı" uyarısı çıkarsa: **Gelişmiş → Kumaş Barkod'a git (güvenli değil)** → **İzin ver**.
   (Uyarı, script'i sizin yazdığınız ve Google'a onaylatmadığınız içindir; script yalnızca bu e-tabloya yazar.)
6. **Web uygulaması URL'si**'ni kopyalayın. `https://script.google.com/macros/s/.../exec` biçimindedir.

> **"Herkes" neden?** Uygulama Google'a giriş yapmadan gönderir. Adresi bilen biri bile **anahtar olmadan yazamaz**;
> sağlık kontrolü (`doGet`) de hiçbir veri döndürmez.

## 5. Uygulamaya girin

1. Uygulamada **Ayarlar** (Ctrl+4).
2. **Apps Script web uygulaması adresi:** kopyaladığınız URL.
3. **Anahtar (API_KEY):** 3. adımdaki anahtar.
4. **Bağlantıyı test et** → "Bağlantı başarılı" görmelisiniz. (Bu test e-tabloda **Kesimler** sayfasını oluşturur.)
5. **Kaydet** → bekleyen kayıtlar hemen gönderilir. Alt çubukta "Sheets güncel" yazar.

## 6. Sayfayı koruyun (zorunlu)

Tablo elle bozulursa, disk arızasında geri kurma da bozulur.

1. E-tabloda alttaki **Kesimler** sekmesine sağ tık → **Sayfayı koru**.
2. **İzinleri ayarla → Bu aralığı kimlerin düzenleyebileceğini kısıtla → Yalnızca siz**.

Script sizin adınıza çalıştığı için korumalı sayfaya yazmaya devam eder. Başkalarıyla tabloyu **görüntüleyici** olarak paylaşabilirsiniz.

---

## Bilmeniz gerekenler

- **Elle değişiklik:** Tablodaki bir hücreyi değiştirirseniz o satır, kesim bir sonraki kez gönderildiğinde uygulamadaki değerle değişir. Düzeltme uygulamadan yapılır (iptal + yeni kayıt).
- **Sütunlar:** `cut_id … last_printed_at` uygulamadaki veritabanının birebir kopyasıdır (geri kurma bunları kullanır). Son sütun `metre`, rapor kolaylığı içindir.
- **Script'i güncellemek:** `Code.gs` değişirse editöre yapıştırıp kaydedin, sonra **Dağıt → Dağıtımları yönet → (kalem) → Sürüm: Yeni sürüm → Dağıt**. URL aynı kalır, uygulamada değişiklik gerekmez.
- **Anahtarı değiştirmek:** Proje Ayarları'nda `API_KEY` değerini değiştirin, sonra uygulamada da aynısını girip kaydedin.

## Sorun giderme

| Uygulamadaki mesaj | Sebep / çözüm |
|---|---|
| Sheets reddetti: Anahtar geçersiz. | Uygulamadaki anahtar ile `API_KEY` aynı değil. |
| Sheets reddetti: Script özelliklerinde API_KEY tanımlı değil. | 3. adım yapılmamış. |
| Sheets yanıtı anlaşılamadı… 'Herkes' erişimi | Dağıtımda erişim "Herkes" değil ya da adres `/exec` ile bitmiyor (`/dev` değil). |
| Google'a ulaşılamıyor | İnternet yok. Kayıtlar birikir; bağlantı gelince kendiliğinden gönderilir. |
| Sheets reddetti: Tablo meşgul… | Aynı anda başka bir gönderim sürüyordu; birkaç dakika içinde tekrar denenir. |
