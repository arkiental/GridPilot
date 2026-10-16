#ifndef Payload
  #define Payload SourcePath + '..\artifacts\GridPilot-win-x64'
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "1.1.0"
  #define VersionMS 65537
  #define VersionLS 0
#endif

[Setup]
AppId=GridPilot.Desktop
AppName=GridPilot
AppVersion={#ReleaseVersion}
AppPublisher=arkiental
AppPublisherURL=https://github.com/arkiental/GridPilot
DefaultDirName={localappdata}\Programs\GridPilot
DefaultGroupName=GridPilot
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=GridPilot-{#ReleaseVersion}-Setup
SetupIconFile=..\src\GridPilot\Assets\GridPilot.ico
UninstallDisplayIcon={app}\GridPilot.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
VersionInfoVersion={#ReleaseVersion}.0
VersionInfoDescription=GridPilot with grid sizing and movement hotkeys

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "Install.ps1,Uninstall.ps1"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GridPilot"; Filename: "{app}\GridPilot.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GridPilot"; Filename: "{app}\GridPilot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GridPilot.exe"; Description: "Open GridPilot and configure shortcuts"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  VersionMS, VersionLS: Cardinal;
begin
  Result := '';
  { Build-Installer supplies the packaged executable version. }
  if GetVersionNumbers(ExpandConstant('{app}\GridPilot.exe'), VersionMS, VersionLS) then
    if (VersionMS > {#VersionMS}) or ((VersionMS = {#VersionMS}) and (VersionLS > {#VersionLS})) then
      Result := 'A newer GridPilot version is installed. Use the latest installer from GitHub Releases.';
end;

procedure InitializeWizard();
begin
  WizardForm.ReadyLabel.Caption := 'Setup will install or update GridPilot. Your shortcuts, grids and other settings are kept in Documents\GridPilot and are not removed by updates or uninstall. Quit GridPilot from its tray menu before continuing.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Startup: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GridPilot', Startup) then
      if Pos(Lowercase(ExpandConstant('{app}\GridPilot.exe')), Lowercase(Startup)) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'GridPilot');
  { Settings in Documents\GridPilot and legacy settings are deliberately preserved. }
end;
