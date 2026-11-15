; Kumaş Barkod DEPO SUNUCUSU — kurulum paketi (Inno Setup 6)
; Derleme: pwsh tools\installer\build-server-installer.ps1
;
; Tasarım kararları:
; - Yönetici izni GEREKİR: Windows hizmeti ve güvenlik duvarı kuralı kurulur.
; - Hizmet adı KumasBarkodSunucu, LocalSystem, gecikmeli otomatik başlar; çökerse 5 sn sonra yeniden başlar.
; - Güvenlik duvarı: TCP 5080, yalnızca YEREL AĞ (localsubnet), her ağ profilinde (fabrika ağı Windows'ta
;   "Ortak" görünse de çalışsın; internetten erişilemez).
; - Veri %ProgramData%\KumasBarkodSunucu'da; kaldırma VERİYİ SİLMEZ. Güncellemede hizmet durdurulur,
;   program klasörü yenilenir, hizmet yeniden başlar; veri ve ayarlar.json korunur.
; - İsteğe bağlı ikinci yedek klasörü (USB/ağ diski) kurulumda sorulur → ayarlar.json.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "Kumaş Barkod Sunucu"
#define AppExe "KumasBarkodSunucu.exe"
#define Service "KumasBarkodSunucu"
#define Port "5080"
#define FirewallRule "Kumas Barkod Sunucu (TCP 5080)"
#define Root "..\.."

[Setup]
AppId={{6E0B8E52-6C1D-4B8F-9D3B-2F6A3C0B7A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Kumaş Barkod
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} kurulumu
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Root}\artifacts\installer
OutputBaseFilename=KumasBarkodSunucu-Kurulum-{#AppVersion}
SetupIconFile={#Root}\src\KumasBarkod.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#Root}\artifacts\server\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Run]
; Hizmet: yoksa oluştur, varsa yolunu güncelle (güncelleme); açıklama, çökme davranışı, başlat.
Filename: "{sys}\sc.exe"; Parameters: "create {#Service} binPath= ""{app}\{#AppExe}"" start= delayed-auto DisplayName= ""Kumaş Barkod Depo Sunucusu"""; Flags: runhidden waituntilterminated; StatusMsg: "Windows hizmeti kuruluyor…"
Filename: "{sys}\sc.exe"; Parameters: "config {#Service} binPath= ""{app}\{#AppExe}"" start= delayed-auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#Service} ""Kumaş Barkod depo stok ve sevkiyat sunucusu (http://*:{#Port}). Veri: %ProgramData%\KumasBarkodSunucu"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#Service} reset= 86400 actions= restart/5000/restart/5000/restart/60000"; Flags: runhidden waituntilterminated
; Güvenlik duvarı: önce eski kural silinir (tekrar kurulumda çift kural olmasın)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow protocol=TCP localport={#Port} remoteip=localsubnet profile=any program=""{app}\{#AppExe}"""; Flags: runhidden waituntilterminated; StatusMsg: "Güvenlik duvarı ayarlanıyor…"
Filename: "{sys}\net.exe"; Parameters: "start {#Service}"; Flags: runhidden waituntilterminated; StatusMsg: "Sunucu başlatılıyor…"

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#Service}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#Service}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewall"

[UninstallDelete]
; Kaldırıcı dosyaları siler ama boş program klasörü kalabiliyordu (kurulum testinde görüldü).
Type: dirifempty; Name: "{app}"

[Messages]
turkish.UninstalledAll=%1 kaldırıldı.%n%nVeritabanı, yedekler ve günlükler SİLİNMEDİ:%n%ProgramData%\KumasBarkodSunucu

[Code]
var
  MirrorPage: TInputQueryWizardPage;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\KumasBarkodSunucu');
end;

procedure InitializeWizard;
begin
  // Klasör seçme sayfası (InputDir) boş değere izin vermiyor → serbest metin; boş bırakılabilir.
  MirrorPage := CreateInputQueryPage(wpSelectDir,
    'İkinci yedek klasörü (önerilir)',
    'Yedekler başka bir diske de kopyalansın mı?',
    'Sunucu her saat yedek alır. Yedekler aynı diskte durursa disk bozulduğunda yedekler de kaybolur.' + #13#10 +
    'Sunucuya takılı kalan bir USB disk ya da ağdaki başka bir bilgisayar seçin (ör. E:\KumasYedek).' + #13#10 +
    'Boş bırakılabilir; sonra ' + DataDir + '\ayarlar.json ile eklenir.');
  MirrorPage.Add('İkinci yedek klasörü (boş bırakılabilir):', False);
  MirrorPage.Values[0] := '';
end;

function LooksLikeFullPath(const S: String): Boolean;
begin
  // C:\... ya da \\sunucu\paylaşım
  Result := ((Length(S) >= 3) and (S[2] = ':') and (S[3] = '\')) or (Copy(S, 1, 2) = '\\');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = MirrorPage.ID) and (Trim(MirrorPage.Values[0]) <> '') then
  begin
    if not LooksLikeFullPath(Trim(MirrorPage.Values[0])) then
    begin
      MsgBox('Sürücü harfiyle tam yol yazın (ör. E:\KumasYedek) ya da ağ yolu (\\bilgisayar\paylaşım).', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if CompareText(ExtractFileDrive(Trim(MirrorPage.Values[0])), ExtractFileDrive(ExpandConstant('{commonappdata}'))) = 0 then
      Result := MsgBox('Seçilen klasör sunucuyla AYNI diskte. Disk bozulursa bu yedek de kaybolur.' + #13#10 +
                       'Yine de devam edilsin mi?', mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure WriteSettings;
var
  Path, Mirror: String;
begin
  Mirror := Trim(MirrorPage.Values[0]);
  if Mirror = '' then Exit;
  Path := DataDir + '\ayarlar.json';
  if FileExists(Path) then
  begin
    MsgBox('Ayar dosyası zaten var, değiştirilmedi:' + #13#10 + Path + #13#10 +
           'İkinci yedek klasörünü bu dosyada "BackupMirrorDirectory" olarak düzenleyin.', mbInformation, MB_OK);
    Exit;
  end;
  ForceDirectories(DataDir);
  SaveStringToFile(Path,
    '{' + #13#10 +
    '  "KumasBarkod": {' + #13#10 +
    '    "BackupMirrorDirectory": "' + JsonEscape(Mirror) + '"' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10, False);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Güncelleme: çalışan hizmet dosyaları kilitler → önce durdur (yoksa sessizce geçer).
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#Service}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Dosyalar kopyalanmadan ve hizmet başlamadan önce: hizmet ilk açılışta ayarı okusun.
  if CurStep = ssInstall then WriteSettings;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Host: String;
begin
  if CurPageID = wpFinished then
  begin
    Host := ExpandConstant('{computername}');
    WizardForm.FinishedLabel.Caption :=
      'Depo sunucusu kuruldu ve Windows hizmeti olarak çalışıyor.' + #13#10#13#10 +
      'Denetim: herhangi bir bilgisayarın tarayıcısında' + #13#10 +
      '    http://' + Host + ':{#Port}/health' + #13#10#13#10 +
      'QC bilgisayarlarında: Ayarlar → Depo sunucusu → adres: ' + Host + #13#10 +
      '(Bu bilgisayara modemden sabit IP verilmesi önerilir.)';
  end;
end;
