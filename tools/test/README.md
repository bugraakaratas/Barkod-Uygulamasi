# Regresyon testleri

Sonuçlar ve ölçümler: `docs/TEST-RAPORU.md`.

## Otomatik

```powershell
dotnet test                               # 262 test
node --test tools/sheets/Code.test.js     # 25 test

# 1 milyon kayıt ölçümü (yaklaşık 3 dk)
$env:KUMAS_STRESS='1'; $env:KUMAS_STRESS_OUT='stres.txt'
dotnet test --filter "FullyQualifiedName~Stress.LargeDataTests"
```

## Arayüz (uygulamayı öne alır, klavye ve fare kullanır)

Önce `dotnet build`. Test sırasında klavyeye ve fareye dokunmayın. PowerShell 7 ve Python gerekir.

```powershell
pwsh -STA -File tools\test\uitest.ps1 -Part A   # tüm ekranlar, sınırlar, okuyucu, iptal, Geçmiş, Ayarlar (60 kontrol)
pwsh -STA -File tools\test\uitest.ps1 -Part B   # 300 bin kayıt, 300 F5, zorla kapatma, iki kopya (21 kontrol)
```

A ve B yalnızca `%TEMP%\kumas-uitest` altında çalışır.

## Gerçek Google (DİKKAT: tabloya yazar)

```powershell
pwsh -STA -File tools\test\uitest.ps1 -Part C   # ekleme, güncelleme, iptal, geri kurma
pwsh -STA -File tools\test\uitest.ps1 -Part D   # çakışma (tabloda T6 satırları olmalı, önce C)
```

C ve D gerçek veritabanının GEÇİCİ KOPYASIYLA çalışır ve tabloya T6, T8 satırları ekler; yalnızca TEST tablosunda koşun.
Uygulama kapalı olmalı. Test bitince bu satırlar silinmeli ve `MAX_SEQUENCES` script özelliği sıfırlanmalı.

## Google stres testi

`stres.gs` içeriğini Apps Script editöründe `Code.gs` sonuna ekleyin (dağıtmayın, yayındaki sürüm etkilenmez) ve
`stres010` … `stres200` işlevlerini çalıştırın. Ayrı `STRES` sekmesinde çalışır. Bitince `STRES` sekmesini ve
`MAX_SEQUENCES` özelliğini silin, temiz `Code.gs` kodunu geri yapıştırın.
