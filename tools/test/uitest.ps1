param([string]$Part = 'A')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern void keybd_event(byte v,byte s,uint f,UIntPtr e);
}
'@
# Geçici klasör: testler gerçek veritabanına yazmaz.
$scratch = Join-Path $env:TEMP 'kumas-uitest'
New-Item -ItemType Directory -Force $scratch | Out-Null
$data = Join-Path $scratch ("ui${Part}_" + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Force $data | Out-Null
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$exe = Join-Path $root 'src\KumasBarkod.App\bin\Debug\net10.0-windows\KumasBarkod.exe'
$env:KUMASBARKOD_DATA_DIR = $data
$fileSetting = (Select-String -Path (Join-Path $root 'src\KumasBarkod.App\Printing\Printers.cs') -Pattern 'FileSetting\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$db = Join-Path $data 'kumas.db'
$py = Join-Path $PSScriptRoot 'uidb.py'
$results = [System.Collections.Generic.List[string]]::new()
function Check($name, $ok) { $results.Add(("{0} {1}" -f ($(if ($ok) {'GECTI'} else {'KALDI'})), $name)) }
function Note($t) { $results.Add("NOT  $t") }
function Sql($q) { (& python $py query $db $q) -join '' }
function Count { [int](Sql 'select count(*) from cuts') }
function Pc($id) { [int](Sql "select print_count from cuts where cut_id='$id'") }
function Field($id, $col) { Sql "select $col from cuts where cut_id='$id'" }
function LastId { Sql 'select cut_id from cuts order by created_at desc, rowid desc limit 1' }

function Start-App {
  $script:p = Start-Process $exe -PassThru
  $deadline = (Get-Date).AddSeconds(40)
  while ((Get-Date) -lt $deadline) { $script:p.Refresh(); if ($script:p.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 200 }
  Start-Sleep -Seconds 3
  $script:h = $script:p.MainWindowHandle
  $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($script:h)
}
function Stop-App { try { $null = $script:p.CloseMainWindow(); if (-not $script:p.WaitForExit(6000)) { $script:p.Kill() } } catch {}; Start-Sleep -Milliseconds 500 }
function Fore {
  for ($i = 0; $i -lt 8; $i++) {
    if ([W]::GetForegroundWindow() -eq $script:h) { return }
    [W]::keybd_event(0x12,0,0,[UIntPtr]::Zero); [W]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
    [W]::SetForegroundWindow($script:h) | Out-Null
    Start-Sleep -Milliseconds 120
  }
  if ([W]::GetForegroundWindow() -ne $script:h) { throw 'Pencere one gelmedi; test durduruldu.' }
}
function K($keys, $ms = 500) { Fore; [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds $ms }
function Paste($text) { Set-Clipboard -Value $text; K '^v' 300 }
function All { $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
function Has($text) { foreach ($e in (All)) { if ($e.Current.Name -like "*$text*") { return $true } }; return $false }
function ById($id) {
  $script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function Edit($name) {
  foreach ($e in (All)) { if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $e.Current.Name -eq $name) { return $e } }
  return $null
}
function ValueOf($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function EditWithValue($v) {
  foreach ($e in (All)) { if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Edit') { try { if ((ValueOf $e) -eq $v) { return $e } } catch {} } }
  return $null
}
function SetAndSave($edit, $value) {
  $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $n = $walker.GetNextSibling($edit)
  while ($null -ne $n -and $n.Current.ControlType.ProgrammaticName -ne 'ControlType.Button') { $n = $walker.GetNextSibling($n) }
  $n.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 700
}
function Shot($name) {
  Fore; Start-Sleep -Milliseconds 300
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0,0,0,0,$b.Size)
  $bmp.Save((Join-Path $scratch "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function NewCut($lot, $len) { K '{F2}' 200; K '^a' 100; Paste $lot; K '{ENTER}' 200; K "$len{ENTER}" 700 }

function Prepare($seed) {
  Start-App; Stop-App
  & python $py seed $db $fileSetting $seed
  Start-App
}

function PartA {
  Prepare 500
  # --- Ekranlar
  K '^2'; Check 'Ctrl+2 Okutma ekrani' ($null -ne (Edit 'Okutma'))
  K '^3' 900; Check 'Ctrl+3 Gecmis ekrani' ($null -ne (ById 'SearchBox'))
  K '^4' 900; Check 'Ctrl+4 Ayarlar ekrani' ($null -ne (Edit 'Sheets adresi'))
  K '^1' 900; Check 'Ctrl+1 Kesim Girisi' ($null -ne (Edit 'Lot'))
  Shot 'a1_giris'

  # --- Lot sinirlari
  $n = Count
  NewCut ('L' * 51) 100
  Check '51 karakter lot reddedildi' ((Count) -eq $n)
  K '{ESC}' 200
  NewCut ('L' * 50) 100
  Check '50 karakter lot kabul' ((Count) -eq $n + 1 -and (Field (LastId) 'length(lot_code)') -eq '50')
  NewCut 'ĞÜŞİÖÇ ğüşiöç ıI' 200
  Check 'Turkce lot aynen saklandi' ((Field (LastId) 'lot_code') -ceq 'ĞÜŞİÖÇ ğüşiöç ıI')
  NewCut '   BOSLUK LOT   ' 200
  Check 'Lot bosluklari kirpildi' ((Field (LastId) 'lot_code') -ceq 'BOSLUK LOT')
  NewCut '=1+1^XZ~_"''%' 321
  $id = LastId
  Check 'Ozel karakterli lot aynen saklandi' ((Field $id 'lot_code') -ceq '=1+1^XZ~_"''%')
  $zpl = (Get-ChildItem (Join-Path $data 'labels') -Filter "$id*.zpl" | Get-Content -Raw -Encoding utf8)
  Check 'ZPL ozel karakteri kacisli' ($zpl -match '_5EXZ' -and $zpl -match '_7E' -and (([regex]::Matches($zpl, '\^XZ')).Count -eq 1))
  $n = Count
  K '{F2}' 200; K '^a' 100; K '{DEL}' 200; K '{ENTER}' 200; K '100{ENTER}' 500
  Check 'Bos lot reddedildi' ((Count) -eq $n)

  # --- Uzunluk sinirlari
  NewCut 'SINIR' 100
  $n = Count
  foreach ($bad in @('0', '30001', 'abc', '1{,}5', '12.5', '-5')) { K '{ESC}' 150; K "$bad{ENTER}" 400 }
  Check 'Gecersiz uzunluklar reddedildi' ((Count) -eq $n)
  K '{ESC}' 150; K '30000{ENTER}' 600
  Check '30000 cm sinirda kabul' ((Field (LastId) 'length_cm') -eq '30000')
  K '{ESC}' 150; K '2550' 400
  Check 'Canli ipucu = 25,50 m' (Has '= 25,50 m')
  K '{ESC}' 300
  Check 'Esc uzunlugu temizledi' ((ValueOf (Edit 'Uzunluk (cm)')) -eq '')

  # --- Yukari/asagi ile son lotlar
  K '{F2}' 200; $before = ValueOf (Edit 'Lot'); K '{DOWN}' 300; $down = ValueOf (Edit 'Lot')
  Check 'Asagi ok onceki lota gecti' ($before -ne $down)
  K '{UP}' 300; Check 'Yukari ok geri dondu' ((ValueOf (Edit 'Lot')) -eq $before)

  # --- Okuyucu benzetimi (hizli yazim + Enter), her ekranda
  $lotBefore = ValueOf (Edit 'Lot'); $n = Count
  K 'K1000001{ENTER}' 900
  Check 'Giris ekraninda okutma kaydi acti, kayit olusmadi' ((Has 'K1-000001') -and (Count) -eq $n)
  K '^1' 700; Check 'Okutma lot kutusunu bozmadi' ((ValueOf (Edit 'Lot')) -eq $lotBefore)
  K '{ESC}' 150; K '555' 200; K 'K1000002{ENTER}' 900
  Check 'Yarim uzunluk varken okutma kayit acmadi' ((Count) -eq $n -and (Has 'K1-000002'))
  K '^1' 700; Check 'Okutma yarim uzunlugu korudu' ((ValueOf (Edit 'Uzunluk (cm)')) -eq '555')
  K '{ESC}' 150
  K '^3' 900; K 'K1000003{ENTER}' 1000; Check 'Gecmis ekraninda okutma calisti' (Has 'K1-000003')
  K '^4' 900; K 'K1000004{ENTER}' 1000; Check 'Ayarlar ekraninda okutma calisti' (Has 'K1-000004')
  K '^2' 500; K 'XYZ{ENTER}' 500; Shot 'a5_gecersiz'
  K 'K1999999{ENTER}' 500; Shot 'a6_bulunamadi'
  K 'k1-000005{ENTER}' 500; Check 'Kucuk harf/tireli ID acildi' (Has 'K1-000005')

  # --- Iptal akisi
  K 'K1000006{ENTER}' 500
  K '{F8}' 400; K '{ENTER}' 400
  Check 'Sebepsiz iptal olmadi' ((Field 'K1-000006' 'status') -eq 'active')
  K '{ESC}' 300; Check 'Esc iptali kapatti' ((Field 'K1-000006' 'status') -eq 'active')
  K '{F8}' 400; K '2' 200; K '{ENTER}' 500
  Check 'Iptal sebebi yazildi' ((Field 'K1-000006' 'void_reason') -eq 'Yanlış lot girildi')
  Check 'Iptal zamani yazildi' ((Field 'K1-000006' 'voided_at') -ne '')
  K '{F8}' 400; Check 'Iptal edilen tekrar iptal edilemez' (-not (Has 'İptal sebebi —'))
  K 'K1000006{ENTER}' 500; Note 'Iptal uyarisi a2 goruntusunde'
  Shot 'a2_okutma_iptal'

  # --- Gecmis
  K '^3' 1000
  Check 'Gecmis sayfa bilgisi' (Has 'Sayfa 1 /')
  K '^{PGDN}' 900; Check 'Sonraki sayfa' (Has 'Sayfa 2 /')
  K '^{PGUP}' 900; Check 'Onceki sayfa' (Has 'Sayfa 1 /')
  K '^f' 300; K 'K1000010' 900
  Check 'Gecmis ID aramasi tek kayit' (Has '1–1 / 1')
  K '{DOWN}' 400; $pc = Pc 'K1-000010'; $n = Count
  K '{F5}' 700
  Check 'Gecmis F5 artik baski yapmaz' ((Pc 'K1-000010') -eq $pc -and (Count) -eq $n)
  Check 'Gecmis dugmesi yeni adla' ((Has 'Bozulan etiketi yeniden bas') -and -not (Has 'Etiketi tekrar bas'))
  K '{F6}' 500; Check 'Gecmis F6 onay paneli acti' (Has 'aynı ID ile tekrar')
  Check 'Gecmis onayi ID gosteriyor' (Has 'K1-000010')
  Shot 'a7_gecmis_onay'
  Check 'Gecmis onay basmadan once baski yok' ((Pc 'K1-000010') -eq $pc)
  K '{ESC}' 500; Check 'Gecmis Esc basmadi, panel kapandi' ((Pc 'K1-000010') -eq $pc -and -not (Has 'aynı ID ile tekrar'))
  K '{F6}' 500; K '{ENTER}' 800
  Check 'Gecmis F6 + Enter ayni ID basti' ((Pc 'K1-000010') -eq $pc + 1 -and (Count) -eq $n)
  Check 'Gecmis onay sonrasi panel kapandi' (-not (Has 'aynı ID ile tekrar'))
  K '{F6}' 500; K 'K1000012{ENTER}' 1000
  Check 'Onay acikken okutma baski yapmadi, kayit acildi' ((Pc 'K1-000010') -eq $pc + 1 -and (Has 'K1-000012'))
  K '^3' 900
  K '^f' 300; K '^a' 100; K 'K1000006' 900; K '{DOWN}' 400; $pc = Pc 'K1-000006'; K '{F6}' 600
  Check 'Gecmis F6 iptal kaydini basmaz, panel acmaz' ((Pc 'K1-000006') -eq $pc -and -not (Has 'aynı ID ile tekrar'))
  & python $py exec $db "update cuts set print_count=0 where cut_id='K1-000011'" | Out-Null
  K '^f' 300; K '^a' 100; K 'K1000011' 900; K '{DOWN}' 400; K '{F6}' 700
  Check 'Hic basilmamis kayit onaysiz basildi' ((Pc 'K1-000011') -eq 1)
  K '^f' 300; K '^a' 100; K 'LOT-0003' 900
  Check 'Gecmis lot aramasi' (Has 'LOT-0003')
  K '^f' 300; K '^a' 100; K 'yok-boyle-lot' 900
  Check 'Sonuc yok durumu' (Has 'Kayıt yok')
  K '^f' 300; K '^a' 100; K '{DEL}' 800
  Shot 'a3_gecmis'

  # --- Ayarlar
  K '^4' 1000
  $edit = EditWithValue '30000'
  SetAndSave $edit '1,5'; Check 'Ust sinir virgul reddedildi' ((Sql "select value from settings where key='max_length_cm'") -in @('','30000')); Shot 'a4_ayarlar_red'
  SetAndSave (EditWithValue '1,5') '500'; Check 'Ust sinir 500 kaydedildi' ((Sql "select value from settings where key='max_length_cm'") -eq '500')
  K '^1' 900
  NewCut 'LIMIT' 400
  $n = Count
  K '{ESC}' 150; K '501{ENTER}' 500; Check 'Yeni sinir uygulandi' ((Count) -eq $n)
  K '^4' 1000; SetAndSave (EditWithValue '500') '300'; K '^1' 900
  K '{ESC}' 150; K '{F5}' 700
  Check 'Sinir dusunce F5 kayit acmaz' ((Count) -eq $n)
  K '^4' 1000; SetAndSave (EditWithValue '300') '30000'
  SetAndSave (Edit 'İstasyon kodu') 'K2'
  Check 'Istasyon K2 kaydedildi' ((Sql "select value from settings where key='station_code'") -eq 'K2')
  K '^1' 900; K '{ESC}' 150; K '123{ENTER}' 700
  Check 'Yeni istasyon K2-000001' ((LastId) -eq 'K2-000001')
  K '{F5}' 700; Check 'K2 F5 K2-000002' ((LastId) -eq 'K2-000002')
  K '^4' 1000; SetAndSave (Edit 'İstasyon kodu') 'K1'; K '^1' 900
  K '{ESC}' 150; K '124{ENTER}' 700
  $id = LastId; Check 'K1 sayaci kaldigi yerden' ($id -like 'K1-*' -and [int]$id.Substring(3) -gt 500)

  # --- Yeniden acilis
  $n = Count; $lastBefore = LastId
  Stop-App; Start-App
  K '{F5}' 700
  Check 'Yeniden acilista F5 kayit acmaz' ((Count) -eq $n)
  K '{F6}' 500; Check 'Yeniden acilista F6 panel acmaz' (-not (Has 'aynı ID ile tekrar'))
  K '^1' 500; K '{ESC}' 150; K '125{ENTER}' 800
  $next = [int]$lastBefore.Substring(3) + 1
  Check 'Yeniden acilista ID devam etti' ((LastId) -eq ('K1-{0:D6}' -f $next))
  Stop-App

  $logs = Get-ChildItem (Join-Path $data 'logs') -Filter *.log* -ErrorAction SilentlyContinue | Get-Content -Encoding utf8
  $bad = @($logs | Where-Object { $_ -match 'Beklenmeyen|Ölümcül|Exception' })
  Check 'Gunlukte beklenmeyen hata yok' ($bad.Count -eq 0)
  if ($bad.Count) { Note ($bad | Select-Object -First 4) }
  Check 'Veritabani butunlugu' ((Sql 'pragma integrity_check') -eq 'ok')
  Check 'FK butunlugu' ((Sql 'pragma foreign_key_check') -eq '')
}

function PartB {
  Prepare 300000
  $sw = [Diagnostics.Stopwatch]::StartNew(); Stop-App; Start-App; Note ("300 bin kayitla acilis (bekleme haric): {0} ms" -f ($sw.ElapsedMilliseconds - 3000))
  K '{F2}' 150; K '^a' 80; K 'PERF{ENTER}' 200; K '1000{ENTER}' 800
  $mem0 = (Get-Process -Id $script:p.Id).WorkingSet64 / 1MB
  $h0 = (Get-Process -Id $script:p.Id).HandleCount
  $times = [System.Collections.Generic.List[double]]::new()
  $start = Count
  for ($i = 1; $i -le 300; $i++) {
    $sw.Restart(); K '{F5}' 0
    while ((Count) -lt $start + $i -and $sw.ElapsedMilliseconds -lt 3000) { }
    $times.Add($sw.ElapsedMilliseconds); Start-Sleep -Milliseconds 30
  }
  $sorted = $times | Sort-Object
  Note ("300 F5: orta {0} ms, en kotu {1} ms (olcum araci ~100 ms dahil)" -f $sorted[150], $sorted[-1])
  Check '300 F5 kayip/fazla yok' ((Count) -eq $start + 300)
  Start-Sleep -Seconds 1
  $proc = Get-Process -Id $script:p.Id
  Note ("Bellek {0:0} MB -> {1:0} MB, tutamac {2} -> {3}" -f $mem0, ($proc.WorkingSet64 / 1MB), $h0, $proc.HandleCount)
  K '^3' 300; $sw.Restart(); while (-not (Has 'Sayfa 1 /') -and $sw.ElapsedMilliseconds -lt 8000) { Start-Sleep -Milliseconds 50 }
  Note ("Gecmis ekrani acilis: {0} ms" -f $sw.ElapsedMilliseconds)
  Shot 'b1_gecmis_300k'
  K '^1' 500

  # Cokme: F5 serisinin ortasinda sureci oldur
  for ($r = 1; $r -le 6; $r++) {
    K '{ESC}' 100; K "$(1000 + $r){ENTER}" 300
    1..12 | ForEach-Object { K '{F5}' 15 }
    Stop-Process -Id $script:p.Id -Force
    Start-Sleep -Milliseconds 400
    $c = Count; $max = [int](Sql "select max(sequence_no) from cuts where station_code='K1'"); $cnt = [int](Sql "select last_sequence from station_counter where station_code='K1'")
    Check "Cokme $r butunluk" ((Sql 'pragma integrity_check') -eq 'ok')
    Check "Cokme $r sayac=en buyuk sira ($cnt/$max)" ($cnt -eq $max)
    Check "Cokme $r kuyruksuz kayit yok" ([int](Sql 'select count(*) from cuts c where sequence_no>300000 and not exists (select 1 from sync_outbox s where s.cut_id=c.cut_id)') -eq 0)
    Start-App
  }

  # Iki pencere ayni veritabaninda
  $first = $script:p; $firstH = $script:h; $firstRoot = $script:root
  Start-App
  $second = $script:p
  Note ("Ikinci kopya acildi: {0}" -f (-not $second.HasExited))
  if (-not $second.HasExited) {
    $n = Count
    K '{F2}' 150; K '^a' 80; K 'IKI{ENTER}' 200; K '500{ENTER}' 700
    $script:p = $first; $script:h = $firstH; $script:root = $firstRoot
    K '{F2}' 150; K '^a' 80; K 'BIR{ENTER}' 200; K '500{ENTER}' 700
    # Ikinci kaydin yazilmasini bekle (700 ms bazen yetmiyor; 2026-11-15'te bir kez sayim erken okundu, kayitlar dogruydu)
    $sw = [Diagnostics.Stopwatch]::StartNew(); while ((Count) -lt $n + 2 -and $sw.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 100 }
    Check 'Iki kopya farkli ID uretti' ((Count) -eq $n + 2 -and [int](Sql 'select count(*)-count(distinct cut_id) from cuts') -eq 0)
    try { $second.Kill() } catch {}
  }
  $script:p = $first
  Stop-App
  Check 'Son butunluk' ((Sql 'pragma integrity_check') -eq 'ok')
}

function PendingCount { [int](Sql 'select count(distinct cut_id) from sync_outbox where sent_at is null') }
function WaitSent($sec = 45) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ((PendingCount) -gt 0 -and $sw.Elapsed.TotalSeconds -lt $sec) { Start-Sleep -Milliseconds 500 }
  return [int]$sw.Elapsed.TotalSeconds
}
function UseCopy($name) {
  $real = Join-Path $env:LOCALAPPDATA 'KumasBarkod\kumas.db'
  $dir = Join-Path $data $name
  New-Item -ItemType Directory -Force $dir | Out-Null
  Copy-Item $real (Join-Path $dir 'kumas.db')
  foreach ($x in '-wal', '-shm') { if (Test-Path "$real$x") { Copy-Item "$real$x" (Join-Path $dir "kumas.db$x") } }
  $env:KUMASBARKOD_DATA_DIR = $dir
  $script:db = Join-Path $dir 'kumas.db'
}
function InvokeByName($text) {
  foreach ($e in (All)) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $e.Current.Name -like "*$text*") {
      $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true
    }
  }
  return $false
}

function PartC {
  if (Get-Process KumasBarkod -ErrorAction SilentlyContinue) { throw 'Uygulama acik, test baslatilmadi' }

  # 1) Gercek Google'a ekleme ve guncelleme
  UseCopy 'gercek'
  & python $py prep $db 'T6' $fileSetting
  Start-App
  Note ("Acilis gonderimi bekledi: {0} sn" -f (WaitSent))
  $lots = @('=1+1', '=IMPORTXML("http://x","//a")', '007', '1/2', '0.50', '1,5', 'TRUE', '''TIRNAK', '+90', '-5', '2026-01-01', '12:30', '%50', 'ĞÜŞİÖÇ ğüşiöç ıI', '^XZ~_"', 'A&B<C>', '1E5', 'NULL', '+1+1', '-1+1', '@SUM(1)', '=', '''''', '=A1', 'SON')
  $len = 100
  foreach ($l in $lots) { NewCut $l $len; $len += 11 }
  Check "Ozel lotlar kayit oldu ($($lots.Count))" ([int](Sql "select count(*) from cuts where station_code='T6'") -eq $lots.Count)
  Note ("Ozel lotlar gonderimi: {0} sn" -f (WaitSent))
  Check 'Ozel lotlar kuyrukta kalmadi' ((PendingCount) -eq 0)

  K '{F6}' 500; K '{ENTER}' 700
  $id6 = LastId
  Check 'F6 yerelde print_count 2' ((Pc $id6) -eq 2)
  K '^2' 500; K "$id6{ENTER}" 600; K '{F8}' 400; K '1' 200; K '{ENTER}' 600
  Check 'Iptal yerelde' ((Field $id6 'status') -eq 'voided')
  K '^1' 700
  Note ("F6 + iptal gonderimi: {0} sn" -f (WaitSent))

  NewCut 'SERI' 777
  1..20 | ForEach-Object { K '{F5}' 250 }
  $t = [int](Sql "select count(*) from cuts where lot_code='SERI'")
  Check "Seri kayit tamam ($t)" ($t -ge 20)
  Note ("Seri gonderimi: {0} sn" -f (WaitSent))
  Check 'Her sey gonderildi' ((PendingCount) -eq 0)
  Shot 'c1_gercek'
  $expected = & python $py dump $db 'T6'
  $maxT8 = [int](Sql "select max(sequence_no) from cuts where station_code='T6'")
  Stop-App
  $bad = @(Get-Content (Join-Path (Split-Path $db) 'logs\*.log') -Encoding utf8 | Where-Object { $_ -match '\[HATA\]|\[UYARI\]' })
  Check 'Gercek gonderim gunlugu temiz' ($bad.Count -eq 0)
  if ($bad.Count) { Note ($bad | Select-Object -First 3) }

  # 2) Gercek tablodan geri kurma: tum alanlar ayni mi
  UseCopy 'geri'
  & python $py wipe $db
  & python $py prep $db 'T7' $fileSetting
  Start-App
  K '^4' 1200
  Check 'Geri kur dugmesi calisti' (InvokeByName 'geri kur…')
  Start-Sleep -Seconds 1
  Check 'Geri kur onayi' (InvokeByName 'Evet, geri kur')
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ((Count) -eq 0 -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Seconds 1
  Note ("Gercek geri kurma: {0} kayit, {1} sn" -f (Count), [int]$sw.Elapsed.TotalSeconds)
  Shot 'c2_geri_kurma'
  $actual = & python $py dump $db 'T6'
  $same = ($expected -join "`n") -ceq ($actual -join "`n")
  Check 'Geri kurulan T6 kayitlari birebir ayni' $same
  Note ('Toplam geri kurulan: ' + (Count) + ', eski bozuk T8 hucreleri: ' + (Sql "select lot_code from cuts where cut_id in ('T8-000001','T8-000002') order by cut_id"))
  if (-not $same) { Compare-Object $expected $actual | Select-Object -First 8 | ForEach-Object { Note ("{0} {1}" -f $_.SideIndicator, $_.InputObject) } }
  Check 'Geri kurmada sayac atladi' ([int](Sql "select last_sequence from station_counter where station_code='T6'") -ge $maxT8 + 1000)
  Stop-App

  # 3) Internetsiz birikme + cakisma
  UseCopy 'cakisma'
  & python $py prep $db 'T8' $fileSetting
  & python $py badurl $db
  Start-App
  NewCut 'CAKISMA-YEREL' 999
  $cid = LastId
  Check "Baglantisiz T8-000001 uretildi ($cid)" ($cid -eq 'T8-000001')
  Start-Sleep -Seconds 8
  Check 'Baglantisizken kuyrukta bekler' ((PendingCount) -eq 1)
  Stop-App
  & python $py goodurl $db
  Start-App
  Start-Sleep -Seconds 14
  Check 'Cakisan kayit kuyrukta kaldi' ((PendingCount) -eq 1)
  Check 'Cakisma hatasi yazildi' ((Sql "select count(*) from sync_outbox where last_error like '%başka bir kesime%'") -ne '0')
  Shot 'c3_cakisma'
  Stop-App
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $data 'gercek\kumas.db*'), (Join-Path $data 'geri\kumas.db*'), (Join-Path $data 'cakisma\kumas.db*')
  $env:KUMASBARKOD_DATA_DIR = $data
  Note ('Gecici kopyalar silindi; son T8 sira: ' + $maxT8)
}

function PartD {
  if (Get-Process KumasBarkod -ErrorAction SilentlyContinue) { throw 'Uygulama acik, test baslatilmadi' }
  UseCopy 'cakisma2'
  & python $py prep $db 'T6' $fileSetting
  & python $py badurl $db
  Start-App
  NewCut 'CAKISMA-YEREL' 999
  $cid = LastId
  Check "Baglantisiz T6-000001 uretildi ($cid)" ($cid -eq 'T6-000001')
  Start-Sleep -Seconds 8
  Check 'Baglantisizken kuyrukta bekler' ((PendingCount) -eq 1)
  Stop-App
  & python $py goodurl $db
  Start-App
  Start-Sleep -Seconds 14
  Check 'Cakisan kayit kuyrukta kaldi' ((PendingCount) -eq 1)
  Check 'Cakisma hatasi yazildi' ((Sql "select count(*) from sync_outbox where last_error like '%başka bir kesime%'") -ne '0')
  Shot 'd1_cakisma'
  Stop-App
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $data 'cakisma2\kumas.db*')
  $env:KUMASBARKOD_DATA_DIR = $data
}

# ---------- E: Depo ekranları (Faz 2) — geçici depo sunucusu ile ----------
$serverExe = Join-Path $root 'src\KumasBarkod.Server\bin\Debug\net10.0\KumasBarkodSunucu.exe'
$serverUrl = 'http://localhost:5087/'
function Api($path) { Invoke-RestMethod ($serverUrl + $path) -TimeoutSec 5 }
# Lot adlari (Invoke-RestMethod JSON dizisini tek nesne olarak dondurur: .name ile acilir)
function LotNames { @((Api 'api/v1/lots').name) }
# Not: InfoBar mesaj metni UI Automation agacinda gorunmuyor; mesajlar ekran goruntusunden, davranis veriden kontrol edilir.
function Select-ByName($name) {
  foreach ($e in (All)) {
    if ($e.Current.Name -eq $name) {
      $pat = $null
      if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); Start-Sleep -Milliseconds 300; return $true }
    }
  }
  return $false
}
function Invoke-ByName($name) {
  foreach ($e in (All)) {
    if ($e.Current.Name -eq $name -and $e.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') {
      $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700; return $true
    }
  }
  return $false
}
function WaitFor($scriptBlock, $ms = 8000) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $ms) { if (& $scriptBlock) { return $true }; Start-Sleep -Milliseconds 200 }
  return $false
}

function PartE {
  $env:KUMASBARKOD_SERVER_DATA_DIR = Join-Path $data 'sunucu'
  $script:srv = Start-Process $serverExe -ArgumentList '--urls', $serverUrl.TrimEnd('/') -PassThru -WindowStyle Hidden
  Check 'Sunucu acildi' (WaitFor { try { (Api 'health').ok } catch { $false } } 15000)

  Start-App; Stop-App
  & python $py seed $db $fileSetting 30 | Out-Null
  & python $py exec $db "insert into settings(key,value) values('server_url','$serverUrl')" | Out-Null
  & python $py exec $db "insert into server_outbox(cut_id,operation,created_at) select cut_id,'upsert','2026-11-15T00:00:00.000Z' from cuts" | Out-Null
  Start-App
  Check 'QC kayitlari sunucuya gitti' (WaitFor { try { (Api 'api/v1/stock').inStock.count -eq 30 } catch { $false } } 20000)
  Invoke-RestMethod ($serverUrl + 'api/v1/lots') -Method Post -ContentType 'application/json' -Body '{"name":"LOT-0001"}' | Out-Null

  # --- Gorev: Yonetim
  K '^4' 900
  Check 'Gorev secenekleri gorunuyor' (Select-ByName 'Görev yönetim')
  Check 'Gorev kaydedildi' (Invoke-ByName 'Görevi kaydet')
  Check 'Gorev ayara yazildi' ((Sql "select value from settings where key='station_role'") -eq 'office')
  K '^2' 1500   # yonetim menusu: Sevkiyat, Depo Stogu, Lotlar, ...
  Check 'Ctrl+2 Depo Stogu' (WaitFor { Has 'Depoda (stokta)' })
  Check 'Stok ozeti 30 top' (WaitFor { Has 'Sayfa 1 / 1 · 30 top' })
  Shot 'e1_stok'

  # --- Arama (klavye)
  K 'LOT-0001' 1500
  Check 'Lot aramasi tek top' (WaitFor { Has 'Sayfa 1 / 1 · 1 top' })
  K '{ESC}' 1200
  Check 'Esc aramayi temizledi' (WaitFor { Has 'Sayfa 1 / 1 · 30 top' })
  K '^f' 200; K 'zzz' 1500
  Check 'Sonuc yok durumu' (WaitFor { Has 'Bu filtreye uyan top yok.' })
  K '{ESC}' 1000

  # --- Barkod okutma (okuyucu gibi hizli + Enter) → topun gecmisi
  K 'K1000005{ENTER}' 1500
  Check 'Okutma topun gecmisini acti' (WaitFor { Has 'Hareketler (en yeni üstte)' })
  Check 'Gecmiste QC kaydi var' (Has 'Kalite kontrol kaydı — stoğa girdi')
  Check 'Okutma arama kutusunu bozmadi' ((ValueOf (ById 'SearchBox')) -eq '')
  Shot 'e2_gecmis'
  K '{ESC}' 800
  Check 'Esc gecmisi kapatti' (-not (Has 'Hareketler (en yeni üstte)'))
  K 'K1000999{ENTER}' 1500
  Check 'Bilinmeyen barkod gecmis acmadi' (-not (Has 'Hareketler (en yeni üstte)'))
  Check 'Bilinmeyen barkod sonrasi uygulama calisiyor' (-not $script:p.HasExited)
  Shot 'e2b_bilinmeyen_barkod'

  # --- Lotlar: Enter ile ekle, yazim farkli ayni lot reddedilir
  K '^3' 1500   # yonetim menusu: Sevkiyat, Depo Stogu, Lotlar, Personel
  Check 'Ctrl+3 Lotlar' (WaitFor { Has 'Lotlar (firmalar)' })
  K 'YENI FIRMA{ENTER}' 1500
  Check 'Enter ile lot eklendi' (WaitFor { (LotNames) -contains 'YENI FIRMA' })
  Check 'Eklenince kutu temizlendi' ((ValueOf (Edit 'Yeni lot adı')) -eq '')
  K 'yeni-firma{ENTER}' 1500
  Check 'Yazim farkli ayni lot reddedildi (sunucuda 2 lot)' ((LotNames).Count -eq 2)
  Check 'Reddedilen yazim kutuda kaldi' ((ValueOf (Edit 'Yeni lot adı')) -eq 'yeni-firma')
  Check 'Eslesmemis yazimlar listelendi' (WaitFor { Has 'Hiçbir lota bağlanmamış' })
  Shot 'e3_lotlar'

  # --- Okutma Lotlar ekranindayken de calisir (Depo Stogu'na gecer)
  K 'K1000007{ENTER}' 1500
  Check 'Lotlarda okutma Depo Stogu na gecti' (WaitFor { Has 'Hareketler (en yeni üstte)' })
  K '{ESC}' 500

  # --- Gorev: Kalite kontrol'e geri
  K '^4' 900
  Check 'QC gorevi secildi' (Select-ByName 'Görev kalite kontrol')
  Check 'QC gorevi kaydedildi' (Invoke-ByName 'Görevi kaydet')
  K '^1' 900
  Check 'QC Ctrl+1 Kesim Girisi' ($null -ne (Edit 'Lot'))
  $n = Count
  NewCut 'LOT-0001' 1234
  Check 'QC kaydi calisiyor' ((Count) -eq $n + 1)
  Check 'Yeni kayit sunucuya gitti' (WaitFor { (Api 'api/v1/stock').inStock.count -eq 31 } 15000)
  Check 'Yeni kayit lota baglandi' (WaitFor { (Api 'api/v1/rolls/K1000031').roll.lotName -eq 'LOT-0001' })
  Stop-App

  $log = Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Get-Content -Raw
  Check 'Gunlukte beklenmeyen hata yok' ($log -notmatch 'Beklenmeyen')
  Check 'Sunucu butunluk' ((& $serverExe --kontrol) -match 'sorun yok')
  try { $script:srv.Kill() } catch {}
}

# ---------- F: QC'de lot önerisi ve tanımsız lot onayı (Faz 3) ----------
function LotOf($id) { Field $id 'lot_code' }
function LotValue { ValueOf (Edit 'Lot') }

function PartF {
  $env:KUMASBARKOD_SERVER_DATA_DIR = Join-Path $data 'sunucu'
  $script:srv = Start-Process $serverExe -ArgumentList '--urls', $serverUrl.TrimEnd('/') -PassThru -WindowStyle Hidden
  Check 'Sunucu acildi' (WaitFor { try { (Api 'health').ok } catch { $false } } 15000)
  foreach ($n in 'LOT-3', 'Akın Tekstil', 'LOT-30') {
    $body = [Text.Encoding]::UTF8.GetBytes((@{ name = $n } | ConvertTo-Json))
    Invoke-RestMethod ($serverUrl + 'api/v1/lots') -Method Post -ContentType 'application/json; charset=utf-8' -Body $body | Out-Null
  }

  Start-App; Stop-App
  & python $py seed $db $fileSetting 3 | Out-Null
  & python $py exec $db "insert into settings(key,value) values('server_url','$serverUrl')" | Out-Null
  & python $py exec $db "insert into server_outbox(cut_id,operation,created_at) select cut_id,'upsert','2026-11-15T00:00:00.000Z' from cuts" | Out-Null
  Start-App
  Check 'Lot listesi onbellege geldi' (WaitFor { [int](Sql 'select count(*) from lots_cache where active=1') -eq 3 } 20000)
  K '^1' 900

  # --- Yazarken tanimli lotlar
  K '{F2}' 200; K '^a' 100; K 'lo' 600
  Check 'Yazarken tanimli lotlar gosterildi' (Has 'Tanımlı lotlar')
  Check 'Oneride LOT-3' (Has 'LOT-3')
  K '{DOWN}' 300
  Check 'Asagi ok ilk oneriyi secti' ((LotValue) -eq 'LOT-3')
  K '{F2}' 200; K '^a' 100; Paste 'akin'; Start-Sleep -Milliseconds 400
  Check 'Turkce harfsiz yazim onerildi' (Has 'Akın Tekstil')

  # --- Yazim farki: tanimli ad yazilir
  $n = Count
  NewCut 'lot 3' 500
  Check 'Yazim farki kaydedildi' ((Count) -eq $n + 1)
  Check 'Tanimli ad yazildi (lot 3 -> LOT-3)' ((LotOf (LastId)) -ceq 'LOT-3')

  # --- Tanimsiz, onerisiz: Enter yazildigi gibi kaydeder
  $n = Count
  NewCut 'YENI LOT' 600
  Check 'Tanimsiz lot hemen kaydedilmedi' ((Count) -eq $n)
  Shot 'f1_tanimsiz'
  K '{ENTER}' 900
  Check 'Enter yazildigi gibi kaydetti' ((Count) -eq $n + 1 -and (LotOf (LastId)) -ceq 'YENI LOT')
  NewCut 'yeni lot' 601
  Check 'Ayni tanimsiz lot ikinci kez sorulmadi' ((Count) -eq $n + 2)
  Check 'Ikinci yazim ilk onaylanan yazimla basildi' ((LotOf (LastId)) -ceq 'YENI LOT')

  # --- Sifir farki: oneri var, secmeden Enter kaydetmez
  $n = Count
  NewCut 'LOT-03' 700
  Shot 'f2_oneri'
  K '{ENTER}' 600
  Check 'Secimsiz Enter kaydetmedi' ((Count) -eq $n)
  K '1' 200; K '{ENTER}' 900
  Check '1 + Enter onerilen lotla kaydetti' ((Count) -eq $n + 1 -and (LotOf (LastId)) -ceq 'LOT-3')
  NewCut 'LOT-03' 701
  K '2' 200; K '{ENTER}' 900
  Check '2 + Enter yazildigi gibi kaydetti' ((Count) -eq $n + 2 -and (LotOf (LastId)) -ceq 'LOT-03')

  # --- Esc: kaydetmez, lot kutusuna doner
  $n = Count
  NewCut 'XYZ FIRMA' 800
  K '{ESC}' 400
  Check 'Esc kaydetmedi' ((Count) -eq $n)
  K '^a' 100; Paste 'LOT-30'; K '{ENTER}' 200; K '801{ENTER}' 900
  Check 'Esc sonrasi lot duzeltilip kaydedildi' ((Count) -eq $n + 1 -and (LotOf (LastId)) -ceq 'LOT-30')

  # --- Onay acikken okutma: kaydetmez, okutma ekranina gecer
  $n = Count
  NewCut 'QWE FIRMA' 900
  K 'K1000001{ENTER}' 1200
  Check 'Onay acikken okutma kayit acmadi' ((Count) -eq $n)
  Check 'Okutma ekrani acildi' ($null -ne (Edit 'Okutma'))
  K '^1' 900

  # --- F5 sonraki parca onaysiz (lot zaten onaylandi)
  $n = Count
  K '{F5}' 900
  Check 'F5 sonraki parca' ((Count) -eq $n + 1)

  # --- Sunucuda lot baglama
  Check 'Kayitlar sunucuya gitti' (WaitFor { (Api 'api/v1/stock?state=all').filtered.count -eq (Count) } 20000)
  $unmatched = @((Api 'api/v1/lots/unmatched').rawText)
  Check 'LOT-3 kayitlari lota bagli' ($unmatched -notcontains 'LOT-3' -and $unmatched -notcontains 'lot 3')
  Check 'Tanimsiz yazimlar yonetime dustu' ($unmatched -contains 'YENI LOT' -and $unmatched -contains 'LOT-03')
  Stop-App

  $log = Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Get-Content -Raw
  Check 'Gunlukte beklenmeyen hata yok' ($log -notmatch 'Beklenmeyen')
  try { $script:srv.Kill() } catch {}
}

# ---------- G: Sevkiyat (Faz 4) — rıhtım akışı, geçici sunucu ----------
function Start-Server {
  $script:srv = Start-Process $serverExe -ArgumentList '--urls', $serverUrl.TrimEnd('/') -PassThru -WindowStyle Hidden
  WaitFor { try { (Api 'health').ok } catch { $false } } 15000
}
function PostJson($path, $obj, $token = $null) {
  $h = @{ 'X-Kumas-Device' = 'UITEST' }
  if ($token) { $h['Authorization'] = "Bearer $token" }
  $body = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 5 -Compress))
  Invoke-RestMethod ($serverUrl + $path) -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -Headers $h
}
function SrvSql($q) { (& python $py query (Join-Path $data 'sunucu\sunucu.db') $q) -join '' }
function RollState($id) { SrvSql "select state from rolls where cut_id='$id'" }
function SelectItemContaining($text) {
  $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  foreach ($e in (All)) {
    if ($e.Current.Name -eq $text) {
      $el = $e
      for ($i = 0; $i -lt 5 -and $null -ne $el; $i++) {
        $pat = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select(); Start-Sleep -Milliseconds 300; return $true }
        $el = $walker.GetParent($el)
      }
    }
  }
  return $false
}

function PartG {
  $env:KUMASBARKOD_SERVER_DATA_DIR = Join-Path $data 'sunucu'
  Check 'Sunucu acildi' (Start-Server)

  # Hazirlik (API): ilk yetkili + depo personeli, lotlar, toplar
  PostJson 'api/v1/users' @{ name = 'Patron'; role = 'supervisor'; badgeCode = 'Y0001'; pin = '9999' } | Out-Null
  $boss = (PostJson 'api/v1/auth/login' @{ badgeCode = 'Y0001'; pin = '9999' }).token
  PostJson 'api/v1/users' @{ name = 'Ali Depo'; role = 'warehouse'; badgeCode = 'P0001'; pin = '1234' } $boss | Out-Null
  foreach ($n in 'LOT-3', 'LOT-2') { PostJson 'api/v1/lots' @{ name = $n } | Out-Null }
  $rows = @()
  for ($i = 1; $i -le 7; $i++) {
    $rows += @{ cutId = ('K1-{0:D6}' -f $i); stationCode = 'K1'; sequenceNo = $i; lotCode = $(if ($i -le 5) { 'LOT-3' } else { 'LOT-2' });
      lengthCm = 1000 + $i; operator = $null; status = 'active'; voidReason = $null; voidedAt = $null;
      createdAt = ('2026-11-15T08:0{0}:00.000Z' -f $i); printCount = 1; lastPrintedAt = $null }
  }
  PostJson 'api/v1/cuts/batch' @{ installId = 'QC-PC'; stationCode = 'K1'; appVersion = 'test'; rows = $rows } | Out-Null
  Check 'Stok hazir (7 top)' ((Api 'api/v1/stock').inStock.count -eq 7)

  Start-App; Stop-App
  & python $py exec $db "insert into settings(key,value) values('server_url','$serverUrl')" | Out-Null
  & python $py exec $db "insert into settings(key,value) values('station_role','dock')" | Out-Null
  Start-App

  # --- Giris
  Check 'Rihtim gorevinde Sevkiyat girisi acildi' (WaitFor { Has 'Sevkiyat — giriş' })
  K 'P0001{ENTER}' 300; K '0000{ENTER}' 1500
  Check 'Yanlis PIN uyarisi' (WaitFor { Has 'Kart kodu ya da PIN hatalı' })
  K '1234{ENTER}' 2000
  Check 'Dogru PIN ile giris' (WaitFor { Has 'Yeni sevkiyat: lot seçin' })
  Check 'Personel adi gorunuyor' (Has 'Ali Depo')
  Shot 'g1_liste'

  # --- Yeni sevkiyat
  Check 'LOT-3 secildi' (SelectItemContaining 'LOT-3')
  K '{ENTER}' 2000
  Check 'Okutma ekrani acildi' (WaitFor { Has 'Barkod okutun' })
  Check 'Sunucuda acik sevkiyat' ((SrvSql "select count(*) from shipments where status='open'") -eq '1')

  # --- Okutmalar (okuyucu gibi hizli + Enter)
  K 'K1000001{ENTER}' 1500
  Check 'Dogru lot kabul (yesil)' (WaitFor { Has '✓  K1-000001' })
  Check 'Top ayrildi' ((RollState 'K1-000001') -eq 'reserved')
  Shot 'g2_kabul'
  K 'K1000006{ENTER}' 1500
  Check 'Yanlis lot ret (kirmizi)' (WaitFor { Has 'YÜKLEMEYİN' })
  Check 'Yanlis lot mesaji' (Has 'Bu barkod seçili lota ait değil. Barkodun lotu: LOT-2. Seçili lot: LOT-3.')
  Check 'Yanlis lot stokta kaldi' ((RollState 'K1-000006') -eq 'in_stock')
  Shot 'g3_ret'
  Start-Sleep -Milliseconds 1600
  K 'K1000001{ENTER}' 1500
  Check 'Ayni barkod tekrar: uyari' (WaitFor { Has 'zaten okutuldu' })
  K 'K1000002{ENTER}' 100; K 'K1000002{ENTER}' 1500
  Check 'Okuyucu cift okumasi tek kez gonderildi' ((SrvSql "select count(*) from scan_events where cut_id='K1-000002'") -eq '1')
  K 'K1000099{ENTER}' 1500
  Check 'Sistemde olmayan barkod ret' (WaitFor { Has 'sistemde yok' })

  # --- Cikarma modu
  K '{F7}' 300   # cift okuma penceresi (1,5 sn) icinde: mod degisince ayni barkod hemen okutulabilmeli
  Check 'Cikarma modu acildi' (Has 'ÇIKARMA MODU')
  K 'K1000002{ENTER}' 1500
  Check 'Cikarilan top stoga dondu' (WaitFor { (RollState 'K1-000002') -eq 'in_stock' })
  K '{F7}' 500

  # --- Baglanti kopmasi: okutma kuyrukta bekler, gelince bir kez gider
  try { $script:srv.Kill(); $script:srv.WaitForExit(5000) } catch {}
  Start-Sleep -Milliseconds 500
  K 'K1000004{ENTER}' 2500
  Check 'Baglanti yok uyarisi' (WaitFor { Has 'BAĞLANTI YOK' } 12000)
  Shot 'g4_baglanti_yok'
  Check 'Sunucu yeniden acildi' (Start-Server)
  Check 'Bekleyen okutma baglanti gelince gitti' (WaitFor { (RollState 'K1-000004') -eq 'reserved' } 20000)
  Check 'Bekleyen okutma tek kez islendi' ((SrvSql "select count(*) from scan_events where cut_id='K1-000004'") -eq '1')
  Start-Sleep -Milliseconds 1500

  # --- Onay
  K '{F9}' 800
  Check 'Onay paneli acildi' (Has 'onaylansın mı?')
  K '{ENTER}' 2500
  Check 'Onaylandi, listeye donuldu' (WaitFor { Has 'onaylandı:' })
  Check 'Sunucuda onayli' ((SrvSql "select status from shipments where shipment_id=1") -eq 'confirmed')
  Check 'Okutulan toplar sevk edildi' ((RollState 'K1-000001') -eq 'shipped' -and (RollState 'K1-000004') -eq 'shipped')
  Check 'Cikarilan ve yanlis lot stokta' ((RollState 'K1-000002') -eq 'in_stock' -and (RollState 'K1-000006') -eq 'in_stock')
  Shot 'g5_onay'

  # --- Yeni sevkiyat + iptal
  Check 'LOT-3 tekrar secildi' (SelectItemContaining 'LOT-3')
  K '{ENTER}' 2000
  K 'K1000005{ENTER}' 1500
  K 'K1000001{ENTER}' 1500
  Check 'Sevk edilmis barkod ret' (WaitFor { Has 'daha önce sevk edildi' })
  K '{F8}' 800
  Check 'Iptal paneli acildi' (Has 'iptal edilecek')
  K '{ENTER}' 800
  Check 'Sebepsiz iptal olmadi' ((SrvSql "select status from shipments where shipment_id=2") -eq 'open')
  K '1' 300; K '{ENTER}' 2000
  Check 'Iptal edildi' (WaitFor { (SrvSql "select status from shipments where shipment_id=2") -eq 'cancelled' })
  Check 'Iptalde top stoga dondu' ((RollState 'K1-000005') -eq 'in_stock')

  # --- Cikis
  Check 'Cikis dugmesi' (Invoke-ByName 'Çıkış')
  Check 'Cikista giris ekrani' (WaitFor { Has 'Sevkiyat — giriş' })
  Stop-App

  # --- Yonetim: Personel ekrani
  & python $py exec $db "update settings set value='office' where key='station_role'" | Out-Null
  $env:KUMASBARKOD_START_PAGE = 'personnel'
  Start-App
  $env:KUMASBARKOD_START_PAGE = ''
  Check 'Personel: yetkili girisi istendi' (WaitFor { Has 'Yetkili girişi' })
  K 'P0001{ENTER}' 300; K '1234{ENTER}' 2000
  Check 'Depo personeli yonetemez' (-not (Has 'Personel ekle'))
  Invoke-ByName 'Çıkış' | Out-Null
  Start-Sleep -Milliseconds 800
  $badge = Edit 'Yetkili kart kodu'
  if ($badge) { $badge.SetFocus() }
  K 'Y0001{ENTER}' 300; K '9999{ENTER}' 2000
  Check 'Yetkili personel listesini goruyor' (WaitFor { Has 'P0001' })
  Shot 'g6_personel'
  Stop-App

  Check 'Sunucu butunluk' ((& $serverExe --kontrol) -match 'sorun yok')
  $log = Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Get-Content -Raw
  Check 'Gunlukte beklenmeyen hata yok' ($log -notmatch 'Beklenmeyen')
  try { $script:srv.Kill() } catch {}
}

# ---------- H: El terminali web sayfası (Faz 4) — görünmez Edge + CDP ----------
function PartH {
  $env:KUMASBARKOD_SERVER_DATA_DIR = Join-Path $data 'sunucu'
  Check 'Sunucu acildi' (Start-Server)
  PostJson 'api/v1/users' @{ name = 'Patron'; role = 'supervisor'; badgeCode = 'Y0001'; pin = '9999' } | Out-Null
  $boss = (PostJson 'api/v1/auth/login' @{ badgeCode = 'Y0001'; pin = '9999' }).token
  PostJson 'api/v1/users' @{ name = 'Ali Depo'; role = 'warehouse'; badgeCode = 'P0001'; pin = '1234' } $boss | Out-Null
  foreach ($n in 'LOT-3', 'LOT-2') { PostJson 'api/v1/lots' @{ name = $n } | Out-Null }
  $rows = @()
  for ($i = 1; $i -le 7; $i++) {
    $rows += @{ cutId = ('K1-{0:D6}' -f $i); stationCode = 'K1'; sequenceNo = $i; lotCode = $(if ($i -le 5) { 'LOT-3' } else { 'LOT-2' });
      lengthCm = 1000 + $i; operator = $null; status = 'active'; voidReason = $null; voidedAt = $null;
      createdAt = ('2026-11-15T08:0{0}:00.000Z' -f $i); printCount = 1; lastPrintedAt = $null }
  }
  PostJson 'api/v1/cuts/batch' @{ installId = 'QC-PC'; stationCode = 'K1'; appVersion = 'test'; rows = $rows } | Out-Null

  $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $edge) { Check 'Edge bulundu' $false; return }
  $profile = Join-Path $data 'edge-profil'
  $script:edgeProc = Start-Process $edge -ArgumentList '--headless=new', '--remote-debugging-port=9333', "--user-data-dir=$profile",
    '--no-first-run', '--disable-extensions', 'about:blank' -PassThru
  Check 'Edge acildi' (WaitFor { try { (Invoke-RestMethod 'http://127.0.0.1:9333/json/version' -TimeoutSec 2).Browser } catch { $false } } 15000)

  $out = & node (Join-Path $PSScriptRoot 'handheld-e2e.mjs') 9333 $serverUrl $scratch 2>&1
  foreach ($line in $out) { if ("$line" -match '^(GECTI|KALDI) ') { $results.Add("$line") } elseif ("$line".Trim()) { $results.Add("NOT  node: $line") } }

  # Yalnızca testin KENDİ geçici profiliyle açılan Edge süreçleri kapatılır (kullanıcının Edge pencerelerine dokunulmaz).
  Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profile) } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {} }
  Check 'Sunucu butunluk' ((& $serverExe --kontrol) -match 'sorun yok')
  try { $script:srv.Kill() } catch {}
}

# ---------- I: İstisnalar, onayı geri alma, raporlar (Faz 5) ----------
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class W2 {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 public static string ForegroundTitle() { var sb = new StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, 256); return sb.ToString(); }
}
'@
function SetEdit($name, $value) { $e = Edit $name; if ($null -eq $e) { return $false }; $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value); Start-Sleep -Milliseconds 300; return $true }
function SelectCombo($name, $item) {
  foreach ($e in (All)) {
    if ($e.Current.Name -eq $name -and $e.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox') {
      $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 500
      $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $item)
      $it = $e.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
      if ($null -eq $it) { return $false }
      $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
      try { $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
      return $true
    }
  }
  return $false
}

function PartI {
  $env:KUMASBARKOD_SERVER_DATA_DIR = Join-Path $data 'sunucu'
  Check 'Sunucu acildi' (Start-Server)
  PostJson 'api/v1/users' @{ name = 'Patron'; role = 'supervisor'; badgeCode = 'Y0001'; pin = '9999' } | Out-Null
  $boss = (PostJson 'api/v1/auth/login' @{ badgeCode = 'Y0001'; pin = '9999' }).token
  PostJson 'api/v1/users' @{ name = 'Ali Depo'; role = 'warehouse'; badgeCode = 'P0001'; pin = '1234' } $boss | Out-Null
  $lot3 = (PostJson 'api/v1/lots' @{ name = 'LOT-3' }).lotId
  $rows = @()
  for ($i = 1; $i -le 5; $i++) {
    $rows += @{ cutId = ('K1-{0:D6}' -f $i); stationCode = 'K1'; sequenceNo = $i; lotCode = 'LOT-3'; lengthCm = 1000 + $i; operator = $null;
      status = 'active'; voidReason = $null; voidedAt = $null; createdAt = ('2026-11-15T08:0{0}:00.000Z' -f $i); printCount = 1; lastPrintedAt = $null }
  }
  PostJson 'api/v1/cuts/batch' @{ installId = 'QC-PC'; stationCode = 'K1'; appVersion = 'test'; rows = $rows } | Out-Null
  # Ali bir sevkiyatı onaylar (K1-1, K1-3); K1-3 sonradan QC'de iptal edilir; K1-4 için ikinci top (karantina)
  $ali = (PostJson 'api/v1/auth/login' @{ badgeCode = 'P0001'; pin = '1234' }).token
  $ship = PostJson 'api/v1/shipments' @{ lotIds = @($lot3) } $ali
  PostJson "api/v1/shipments/$($ship.shipmentId)/scans" @{ clientScanId = 'i1'; rawInput = 'K1000001' } $ali | Out-Null
  $last = PostJson "api/v1/shipments/$($ship.shipmentId)/scans" @{ clientScanId = 'i3'; rawInput = 'K1000003' } $ali
  PostJson "api/v1/shipments/$($ship.shipmentId)/confirm" @{ expectedVersion = $last.shipmentVersion; requestId = 'c1' } $ali | Out-Null
  $v = $rows[2].Clone(); $v.status = 'voided'; $v.voidReason = 'Çift kayıt'; $v.voidedAt = '2026-11-15T10:00:00.000Z'
  $dup = $rows[3].Clone(); $dup.lotCode = 'LOT-9'; $dup.lengthCm = 777; $dup.createdAt = '2026-11-15T11:00:00.000Z'
  PostJson 'api/v1/cuts/batch' @{ installId = 'QC-PC'; stationCode = 'K1'; appVersion = 'test'; rows = @($v, $dup) } | Out-Null
  Check 'Hazirlik: karantina + sevk sonrasi iptal' ((RollState 'K1-000004') -eq 'quarantined' -and (RollState 'K1-000003') -eq 'shipped')

  Start-App; Stop-App
  & python $py exec $db "insert into settings(key,value) values('server_url','$serverUrl')" | Out-Null
  & python $py exec $db "insert into settings(key,value) values('station_role','office')" | Out-Null
  $env:KUMASBARKOD_START_PAGE = 'exceptions'
  Start-App
  $env:KUMASBARKOD_START_PAGE = ''

  Check 'Istisnalar: yetkili girisi istendi' (WaitFor { Has 'Yetkili girişi' })
  K 'Y0001{ENTER}' 300; K '9999{ENTER}' 2500
  Check 'Ozet: karantina ve sevk sonrasi iptal' (WaitFor { Has '1 karantina · 1 sevk sonrası iptal' })
  Check 'Karantina iki kaydi gosteriyor' ((Has 'Sonradan gelen: LOT-9') -and (Has 'İlk kayıt: LOT-3'))
  Check 'Sevk sonrasi iptal listede' (Has 'Çift kayıt')
  Shot 'i1_istisnalar'

  # Karantina çözümü
  Check 'Karantina secildi' (SelectItemContaining 'K1-000004')
  Check 'Not yazildi' (SetEdit 'Karantina notu' 'İkinci top yeniden etiketlendi: K1-000031')
  Check 'Cozuldu dugmesi' (Invoke-ByName 'Karantina çözüldü')
  Check 'Top karantinadan cikti (stokta)' (WaitFor { (RollState 'K1-000004') -eq 'in_stock' })

  # Onayı geri alma
  Check 'Onayli sevkiyat secildi' (SelectItemContaining 'SV-2026-00001')
  Invoke-ByName 'Onayı geri al' | Out-Null
  Check 'Sebepsiz geri alma yok' ((SrvSql "select status from shipments where shipment_id=1") -eq 'confirmed')
  Check 'Sebep yazildi' (SetEdit 'Geri alma sebebi' 'Tır gitmedi, yanlışlıkla onaylandı')
  Invoke-ByName 'Onayı geri al' | Out-Null
  Check 'Geri alma onay paneli' (WaitFor { Has 'onayı geri alınsın mı?' })
  Invoke-ByName 'Geri almayı onayla' | Out-Null
  Check 'Sevkiyat geri alindi' (WaitFor { (SrvSql "select status from shipments where shipment_id=1") -eq 'reversed' })
  Check 'Top stoga dondu' ((RollState 'K1-000001') -eq 'in_stock')
  Check 'QC iptalli top iptale dondu' ((RollState 'K1-000003') -eq 'voided')
  Check 'Geri alan yetkili kayitli' ((SrvSql "select u.name from shipments s join users u on u.user_id=s.reversed_by where s.shipment_id=1") -eq 'Patron')
  Shot 'i2_geri_alindi'
  Stop-App

  # Raporlar
  $env:KUMASBARKOD_START_PAGE = 'reports'
  Start-App
  $env:KUMASBARKOD_START_PAGE = ''
  Check 'Rapor: guncel stok' (WaitFor { Has 'Toplam stok: 4 top' })
  Check 'Rapor turu: sevkiyat gecmisi' (SelectCombo 'Rapor türü' 'Sevkiyat geçmişi')
  Check 'Gecmiste geri alinan sevkiyat' (WaitFor { Has '1 geri alınmış' })
  Check 'Geri alma notu tabloda' (Has 'Geri alındı (Patron): Tır gitmedi, yanlışlıkla onaylandı')
  Shot 'i3_rapor'

  # CSV kaydet (Windows "Farklı kaydet" penceresi)
  $csv = Join-Path $data 'rapor.csv'
  Invoke-ByName 'CSV kaydet' | Out-Null
  $opened = WaitFor { [W2]::ForegroundTitle() -eq 'Raporu kaydet' } 8000
  Check 'Kaydet penceresi acildi' $opened
  if ($opened) {
    Set-Clipboard -Value $csv
    [System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 200
    if ([W2]::ForegroundTitle() -eq 'Raporu kaydet') { [System.Windows.Forms.SendKeys]::SendWait('^v'); Start-Sleep -Milliseconds 300 }
    if ([W2]::ForegroundTitle() -eq 'Raporu kaydet') { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
  }
  Check 'CSV dosyasi olustu' (WaitFor { Test-Path $csv } 8000)
  if (Test-Path $csv) {
    $bytes = [IO.File]::ReadAllBytes($csv)
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    Check 'CSV UTF-8 BOM (Excel Turkce)' ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    Check 'CSV basliklari Turkce, noktali virgul' ($text.Contains('Sevkiyat;Durum;Lot;Top;Metre'))
    Check 'CSV verisi dogru' ($text.Contains('SV-2026-00001;Onay geri alındı;LOT-3;2;'))
  }
  Stop-App

  Check 'Sunucu butunluk' ((& $serverExe --kontrol) -match 'sorun yok')
  $log = Get-ChildItem (Join-Path $data 'logs') -Filter *.log | Get-Content -Raw
  Check 'Gunlukte beklenmeyen hata yok' ($log -notmatch 'Beklenmeyen')
  try { $script:srv.Kill() } catch {}
}

try {
  if ($Part -eq 'A') { PartA } elseif ($Part -eq 'B') { PartB } elseif ($Part -eq 'D') { PartD } elseif ($Part -eq 'E') { PartE } elseif ($Part -eq 'F') { PartF } elseif ($Part -eq 'G') { PartG } elseif ($Part -eq 'H') { PartH } elseif ($Part -eq 'I') { PartI } else { PartC }
} catch {
  $results.Add("HATA: $($_.Exception.Message) @ satir $($_.InvocationInfo.ScriptLineNumber)")
} finally {
  try { $script:p.Kill() } catch {}
  try { $script:srv.Kill() } catch {}
}
$results | ForEach-Object { $_ }
$kaldi = @($results | Where-Object { $_ -match '^(KALDI|HATA)' }).Count
$gecti = @($results | Where-Object { $_ -match '^GECTI' }).Count
Write-Output "TOPLAM gecti=$gecti kaldi=$kaldi"
Write-Output $data
