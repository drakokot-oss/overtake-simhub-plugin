; Overtake SimHub Plugin - Inno Setup Installer
; Version 1.1.11

#define MyAppName "Overtake SimHub Plugin"
#define MyAppVersion "1.1.11"
#define MyAppPublisher "Overtake F1"
#define MyAppURL "https://racehub.overtakef1.com"

[Setup]
AppId={{E7A3B5C1-2D4F-4E6A-8B9C-0D1E2F3A4B5C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={code:GetSimHubDir}
DirExistsWarning=no
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=Overtake.SimHub.Plugin-v{#MyAppVersion}-Setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Overtake F1 25 Telemetry Plugin for SimHub
VersionInfoCopyright=2026 Overtake F1
CreateUninstallRegKey=yes
WizardImageFile=compiler:WizClassicImage-IS.bmp
WizardSmallImageFile=compiler:WizClassicSmallImage-IS.bmp

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
english.WelcomeLabel2=This will install the Overtake F1 25 Telemetry Plugin v{#MyAppVersion} for SimHub.%n%nThe plugin captures race telemetry data from F1 25 and exports it as JSON for league management.%n%nIMPORTANT: SimHub must be closed before proceeding.
portuguese.WelcomeLabel2=Este instalador vai instalar o Plugin Overtake F1 25 Telemetry v{#MyAppVersion} para o SimHub.%n%nO plugin captura dados de telemetria do F1 25 e exporta como JSON para gerenciamento de ligas.%n%nIMPORTANTE: O SimHub deve estar fechado antes de continuar.

[Files]
Source: "files\Overtake.SimHub.Plugin.dll"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\SimHubWPF.exe"; Description: "Launch SimHub"; Flags: nowait postinstall skipifsilent unchecked; Check: SimHubExeExists

[Code]
function GetSimHubDir(Param: String): String;
var
  InstallPath: String;
begin
  // Try WOW6432Node first (SimHub is a 32-bit app on 64-bit Windows)
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\SimHub_is1',
     'InstallLocation', InstallPath) then
  begin
    if (InstallPath <> '') and DirExists(InstallPath) then
    begin
      Result := InstallPath;
      Exit;
    end;
  end;

  // Try standard Uninstall key
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SimHub_is1',
     'InstallLocation', InstallPath) then
  begin
    if (InstallPath <> '') and DirExists(InstallPath) then
    begin
      Result := InstallPath;
      Exit;
    end;
  end;

  // Fallback: check common installation paths
  if DirExists(ExpandConstant('{commonpf32}\SimHub')) then
  begin
    Result := ExpandConstant('{commonpf32}\SimHub');
    Exit;
  end;

  if DirExists(ExpandConstant('{commonpf64}\SimHub')) then
  begin
    Result := ExpandConstant('{commonpf64}\SimHub');
    Exit;
  end;

  // Last resort default
  Result := ExpandConstant('{commonpf32}\SimHub');
end;

function SimHubExeExists(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\SimHubWPF.exe'));
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill', '/F /IM SimHubWPF.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SimHubExe: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    SimHubExe := ExpandConstant('{app}\SimHubWPF.exe');
    if not FileExists(SimHubExe) then
    begin
      if MsgBox('SimHub was not found in the selected folder:' + #13#10 +
                ExpandConstant('{app}') + #13#10#13#10 +
                'SimHub is usually installed at:' + #13#10 +
                'C:\Program Files (x86)\SimHub' + #13#10#13#10 +
                'Do you want to continue anyway?',
                mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MsgBox('Overtake Plugin v{#MyAppVersion} installed successfully!' + #13#10#13#10 +
           'Next steps:' + #13#10 +
           '1. Open SimHub' + #13#10 +
           '2. SimHub will detect the new plugin automatically' + #13#10 +
           '3. Click "Enable" when the plugin detection popup appears' + #13#10 +
           '4. Restart SimHub when prompted' + #13#10 +
           '5. The "Overtake Telemetry" tab will appear in the left menu' + #13#10#13#10 +
           'If the popup does not appear, check the left sidebar -' + #13#10 +
           'the plugin may already be active.',
           mbInformation, MB_OK);
  end;
end;
