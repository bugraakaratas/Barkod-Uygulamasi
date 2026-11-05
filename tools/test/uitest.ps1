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

try {
  if ($Part -eq 'A') { PartA } elseif ($Part -eq 'B') { PartB } elseif ($Part -eq 'D') { PartD } else { PartC }
} catch {
  $results.Add("HATA: $($_.Exception.Message) @ satir $($_.InvocationInfo.ScriptLineNumber)")
} finally {
  try { $script:p.Kill() } catch {}
}
$results | ForEach-Object { $_ }
$kaldi = @($results | Where-Object { $_ -match '^(KALDI|HATA)' }).Count
$gecti = @($results | Where-Object { $_ -match '^GECTI' }).Count
Write-Output "TOPLAM gecti=$gecti kaldi=$kaldi"
Write-Output $data
