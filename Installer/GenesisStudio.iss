; Genesis Studio user installer.
; Compiled by Build.bat --installer after a self-contained publish. Do not run ISCC by hand
; unless Installer/art and Installer/redist have already been prepared.
;
; Copy on this wizard is "Genesis Studio". Do not add Windows Forms, Visual Studio, or other
; Microsoft product names to the pages a user sees.

#ifndef MyAppVersion
  #define VerFile FileOpen(AddBackslash(SourcePath) + "AppVersion.txt")
  #define MyAppVersion Trim(FileRead(VerFile))
  #expr FileClose(VerFile)
#endif

#if MyAppVersion == ""
  #error Installer/AppVersion.txt is missing or empty.
#endif

#define MyAppName "Genesis Studio"
#define MyAppPublisher "Genesis"
#define MyAppExeName "Genesis Application.exe"
#ifndef PublishDir
  #define PublishDir "..\Genesis Application"
#endif

[Setup]
; Stable product id so upgrades reuse the same Add/Remove Programs entry and install directory.
AppId={{E8A91C4B-6F3D-4A12-9C7E-1B5D8F2A0E33}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
AlwaysShowDirOnReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\Dist
OutputBaseFilename=GenesisStudio-Setup
SetupIconFile=..\Source\Genesis.Application.Studio\Assets\Genesis.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
WizardSizePercent=120
WizardImageFile=art\wizard-side.bmp
WizardSmallImageFile=art\wizard-small.bmp
WizardImageStretch=no
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle={#MyAppName} Setup
SetupWindowTitle={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
; Runtime components are extracted in PrepareToInstall so they can run before the app files copy.
Source: "redist\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: dontcopy nocompression
; The published Documentation tree includes EditorReview workspaces whose paths exceed
; Windows MAX_PATH during compression. Ship the product README only.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.log,Quality,Quality\*,TestResults,TestResults\*,Documentation,Documentation\*"
Source: "{#PublishDir}\Documentation\README.md"; DestDir: "{app}\Documentation"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent; WorkingDir: "{app}"

[Code]
function IsVCRedistInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then
  begin
    if Installed = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
  if RegQueryDWordValue(HKLM32, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then
    Result := Installed = 1;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  RedistPath: String;
begin
  Result := '';
  NeedsRestart := False;

  if IsVCRedistInstalled then
    Exit;

  ExtractTemporaryFile('VC_redist.x64.exe');
  RedistPath := ExpandConstant('{tmp}\VC_redist.x64.exe');
  if not FileExists(RedistPath) then
  begin
    Result := 'Required runtime components are missing from this installer.';
    Exit;
  end;

  if not WizardSilent then
    WizardForm.StatusLabel.Caption := 'Installing required runtime components...';

  if not Exec(RedistPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Could not start the required runtime installer.';
    Exit;
  end;

  { 0 = success; 1638 = a newer package is already present; 3010/1641 = success, restart needed. }
  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
    Result := 'Required runtime installation failed (exit code ' + IntToStr(ResultCode) + ').';
end;
