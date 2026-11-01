; Kumaş Barkod — kurulum paketi (Inno Setup 6)
; Derleme: pwsh tools\installer\build-installer.ps1  (önce yayın alır, sonra bu betiği derler)
;
; Tasarım kararları:
; - Kullanıcı başına kurulum (yönetici izni GEREKMEZ): fabrikada operatör hesabıyla kurulabilir.
;   Veri de kullanıcı klasöründe (%LOCALAPPDATA%\KumasBarkod) olduğu için tutarlı.
;   Yönetici isterse kurulumda "tüm kullanıcılar" seçebilir.
; - Uygulama kendi .NET'ini taşır (self-contained): bilgisayara .NET kurmak gerekmez.
; - Kaldırma VERİYİ SİLMEZ: veritabanı, yedekler ve günlükler %LOCALAPPDATA%\KumasBarkod'da kalır.
; - Güncelleme aynı AppId ile yerinde yapılır; program klasörü önce temizlenir (eski DLL kalmaz),
;   çalışan uygulama kapatılır.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Kumaş Barkod"
#define AppExe "KumasBarkod.exe"
#define Root "..\.."

[Setup]
AppId={{B963423C-EC31-47F5-AD11-B7CF6D89F797}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} kurulumu
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Root}\artifacts\installer
OutputBaseFilename=KumasBarkod-Kurulum-{#AppVersion}
SetupIconFile={#Root}\src\KumasBarkod.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Kısayollar:"

[InstallDelete]
; Güncellemede eski sürümden kalan dosyalar silinir (veri burada değil, %LOCALAPPDATA%'da).
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#Root}\artifacts\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} uygulamasını başlat"; Flags: nowait postinstall skipifsilent

[Messages]
turkish.UninstalledAll=%1 kaldırıldı.%n%nKesim kayıtları, yedekler ve günlükler SİLİNMEDİ:%n%LOCALAPPDATA%\KumasBarkod
