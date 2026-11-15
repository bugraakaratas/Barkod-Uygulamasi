# Depo sunucusu kurulum paketini üretir: testler → yayın (self-contained) → Inno Setup.
# Kullanım: pwsh tools\installer\build-server-installer.ps1 [-SkipTests]
# Çıktı: artifacts\installer\KumasBarkodSunucu-Kurulum-<sürüm>.exe
# Gerekenler: .NET SDK 10, Inno Setup 6 (winget install JRSoftware.InnoSetup)
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $root 'src\KumasBarkod.Server\KumasBarkod.Server.csproj'
$publish = Join-Path $root 'artifacts\server'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "KumasBarkod.Server.csproj içinde <Version> bulunamadı." }
"Sunucu sürümü: $version"

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue)?.Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 bulunamadı. Kurmak için: winget install JRSoftware.InnoSetup" }

if (-not $SkipTests) {
    "1/3 Testler…"
    dotnet test (Join-Path $root 'KumasBarkod.slnx') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Testler başarısız; kurulum paketi üretilmedi." }
}

"2/3 Yayın (self-contained, win-x64)…"
if (Test-Path $publish) { Get-ChildItem $publish -Force | Remove-Item -Recurse -Force }
dotnet publish $project -c Release -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Yayın başarısız." }

"3/3 Inno Setup…"
& $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'KumasBarkodSunucu.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi başarısız." }

$exe = Join-Path $root "artifacts\installer\KumasBarkodSunucu-Kurulum-$version.exe"
"Hazır: $exe ($([Math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
