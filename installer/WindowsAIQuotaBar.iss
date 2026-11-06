; Inno Setup script for Windows AI Quota Bar.
; Build: .\build-release.ps1   (publishes the app, then compiles this script)

#define AppName "Windows AI Quota Bar"
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\WindowsAIQuotaBar"
#endif

[Setup]
AppId={{6B0F3C7E-5A2D-4C8B-9E1F-2D7A9C4B8E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=valuex-creative
AppPublisherURL=https://github.com/valuex-creative/Windows-AI-Quota-Bar
AppSupportURL=https://github.com/valuex-creative/Windows-AI-Quota-Bar/issues
AppUpdatesURL=https://github.com/valuex-creative/Windows-AI-Quota-Bar/releases
; Per-user install, no admin prompt. Same folder the README and the MCP command use.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\QuotaBar
DisableDirPage=auto
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=WindowsAIQuotaBar-Setup-{#AppVersion}
SetupIconFile=..\src\QuotaBar.App\Assets\quotabar.ico
UninstallDisplayIcon={app}\QuotaBar.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.DesktopIcon=Create a &desktop shortcut
tr.DesktopIcon=&Masaüstü kısayolu oluştur
en.LaunchApp=Start {#AppName}
tr.LaunchApp={#AppName} uygulamasını başlat
en.DeleteData=Also delete your settings and saved accounts?%n%n(%LOCALAPPDATA%\QuotaBar)
tr.DeleteData=Ayarların ve kayıtlı hesapların da silinsin mi?%n%n(%LOCALAPPDATA%\QuotaBar)

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\QuotaBar.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\QuotaBar.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\QuotaBar.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
; Silent install = in-app update: start the new version again (it shows an "Updated" notification).
Filename: "{app}\QuotaBar.exe"; Parameters: "--background --updated"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM QuotaBar.exe /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM qb.exe /F"; Flags: runhidden; RunOnceId: "KillCli"

[Registry]
; The app registers itself to start with Windows; remove that on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "QuotaBar"; Flags: uninsdeletevalue dontcreatekey

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Close running copies so their files can be replaced (upgrade in place). qb.exe also runs as the
  // MCP server of AI assistants; it has no window, so Restart Manager cannot close it and a silent
  // update would abort. The assistant starts it again on its next request.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM QuotaBar.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM qb.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\QuotaBar');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
