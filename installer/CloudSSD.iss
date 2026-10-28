; Inno Setup script for the single-file CloudSSD installer (per-user; asks for administrator rights only to install WinFsp).
; Built by build-release.ps1:
;   Built by installer/build-release.ps1 with the selected CloudSSD version.
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\CloudSSD-" + AppVersion + "-win-x64"
#endif
#ifndef WinFspMsi
  #define WinFspMsi "..\tools\winfsp\winfsp-2.1.25156.msi"
#endif
#ifndef WebView2Bootstrapper
  #define WebView2Bootstrapper "..\tools\webview2\MicrosoftEdgeWebview2Setup.exe"
#endif

[Setup]
AppId={{D4B47FD5-C7E0-4614-931C-3C38A68F9916}
AppName=CloudSSD
AppVersion={#AppVersion}
AppVerName=CloudSSD {#AppVersion}
AppPublisher=CloudSSD
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CloudSSD
VersionInfoDescription=CloudSSD Setup
DefaultDirName={localappdata}\Programs\CloudSSD
DefaultGroupName=CloudSSD
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=CloudSSD-{#AppVersion}-setup
SetupIconFile=..\src\CloudSSD.Desktop\Assets\cloudssd.ico
UninstallDisplayIcon={app}\CloudSSD.exe
UninstallDisplayName=CloudSSD
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The desktop app holds this mutex while running; Setup and the uninstaller ask the user to close it first.
AppMutex=CloudSSD.Desktop.SingleInstance
; Never let Windows force-close rclone: a mounted drive may still be uploading. PrepareToInstall checks instead.
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "Install.cmd,Install-CloudSSD.ps1,Uninstall-CloudSSD.ps1"
; Official, signed WinFsp installer (Navimatics LLC, GPLv3 with FLOSS exception). Only extracted when WinFsp is missing.
Source: "{#WinFspMsi}"; DestName: "winfsp.msi"; Flags: dontcopy
Source: "{#WebView2Bootstrapper}"; DestName: "webview2-setup.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\CloudSSD"; Filename: "{app}\CloudSSD.exe"; Comment: "Cloud-to-SSD game storage engine"
Name: "{autodesktop}\CloudSSD"; Filename: "{app}\CloudSSD.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CloudSSD.exe"; Description: "Start CloudSSD"; Flags: nowait postinstall skipifsilent

[Registry]
; "Keep Cloud SSD Permanent" adds this per-user sign-in entry from inside the app. Uninstall removes it (never creates it).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CloudSSD"; Flags: uninsdeletevalue

[UninstallDelete]
; Settings, saved logins and caches (%LOCALAPPDATA%\CloudSSD and the chosen cache folder) are kept on purpose:
; the cache can hold game files that have not finished uploading yet. Files in Google Drive are never touched.
Type: files; Name: "{app}\*.log"

[Code]
function WinFspInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll')) or
            FileExists(ExpandConstant('{commonpf64}\WinFsp\bin\winfsp-x64.dll'));
end;

function WebView2Installed(): Boolean;
var
  Version, Key: String;
begin
  Key := 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := ((RegQueryStringValue(HKLM32, Key, 'pv', Version) or
              RegQueryStringValue(HKCU, Key, 'pv', Version) or
              RegQueryStringValue(HKCU32, Key, 'pv', Version)) and
              (Version <> '') and (Version <> '0.0.0.0'));
end;

{ Name of a CloudSSD process (app, cloudssd-cli CLI or a mounted drive's rclone) running from Dir, or '' if none. }
function RunningFrom(const Dir: String): String;
var
  Locator, Service, Procs, P: Variant;
  I: Integer;
  Path, Prefix, Name: String;
begin
  Result := '';
  Prefix := Lowercase(AddBackslash(Dir));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT Name, ExecutablePath FROM Win32_Process WHERE Name = ''rclone.exe'' OR Name = ''cloudssd-cli.exe'' OR Name = ''CloudSSD.exe'' OR Name = ''CloudSSD.Agent.exe''');
    for I := 0 to Procs.Count - 1 do
    begin
      P := Procs.ItemIndex(I);
      if VarIsNull(P.ExecutablePath) then Path := '' else Path := P.ExecutablePath;
      Path := Lowercase(Path);
      if (Path <> '') and (Pos(Prefix, Path) = 1) then
      begin
        { The background agent is reported first whatever the process order: its drives (rclone, bridge) can then be
          unmounted gracefully through it instead of blocking setup with "drive still mounted". }
        Name := P.Name;
        if Lowercase(Name) = 'cloudssd.agent.exe' then
        begin
          Result := Name;
          Exit;
        end;
        if Result = '' then Result := Name;
      end;
    end;
  except
    { WMI unavailable: fall back to the AppMutex check only. }
  end;
end;

{ Asks the background agent (Keep Cloud SSD Permanent) to unmount its drives gracefully - pending uploads get up to
  60 s - and exit. Cloud files, logins and the cache are kept. }
procedure StopAgent();
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\cloudssd-cli.exe')) then
    Exec(ExpandConstant('{app}\cloudssd-cli.exe'), 'agent stop', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function BusyMessage(const Name: String): String;
begin
  if Lowercase(Name) = 'cloudssd.agent.exe' then
    Result := 'The CloudSSD background agent is keeping a permanent cloud drive mounted and could not be stopped.' + #13#10#13#10 +
              'Open CloudSSD, wait until uploads have finished, then try again.'
  else if Lowercase(Name) = 'rclone.exe' then
    Result := 'A CloudSSD cloud drive is still mounted.' + #13#10#13#10 +
              'Open CloudSSD, close its window and choose "No" so the drives are unmounted safely ' +
              '(pending uploads finish first). Then try again.'
  else
    Result := 'CloudSSD (' + Name + ') is running. Close it and try again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Busy: String;
begin
  Result := '';
  Busy := RunningFrom(ExpandConstant('{app}'));
  if Lowercase(Busy) = 'cloudssd.agent.exe' then
  begin
    { Upgrade: permanent drives are unmounted gracefully now and return at the next sign-in (or when the app starts). }
    StopAgent();
    Busy := RunningFrom(ExpandConstant('{app}'));
  end;
  if Busy <> '' then Result := BusyMessage(Busy);
end;

procedure InstallWinFsp();
var
  Code: Integer;
  Msi: String;
begin
  WizardForm.StatusLabel.Caption := 'Installing WinFsp (needed for the virtual drive) - please approve the Windows prompt...';
  ExtractTemporaryFile('winfsp.msi');
  Msi := ExpandConstant('{tmp}\winfsp.msi');
  if not ShellExec('runas', 'msiexec.exe', '/i "' + Msi + '" /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
    Code := -1;
  if (Code <> 0) and (Code <> 3010) and not WinFspInstalled() then
    MsgBox('WinFsp was not installed (it needs administrator approval).' + #13#10#13#10 +
           'CloudSSD is installed, but cloud drives cannot be mounted until WinFsp is installed. ' +
           'Run this setup again, or install WinFsp from https://winfsp.dev/rel/', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and not WinFspInstalled() then
    InstallWinFsp();
  if CurStep = ssPostInstall then
    Exec(ExpandConstant('{app}\cloudssd-cli.exe'), 'agent refresh-startup', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if (CurStep = ssPostInstall) and not WebView2Installed() then
  begin
    WizardForm.StatusLabel.Caption := 'Installing Microsoft WebView2 for TeraBox sign-in (internet required)...';
    ExtractTemporaryFile('webview2-setup.exe');
    if not Exec(ExpandConstant('{tmp}\webview2-setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Code := -1;
    if not WebView2Installed() then
      MsgBox('Microsoft WebView2 could not be installed. TeraBox website sign-in needs this runtime.' + #13#10 +
             'Connect to the internet and run this setup again. Installer result: ' + IntToStr(Code), mbInformation, MB_OK);
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine +
    'Game compatibility and cloud speed vary. Smooth gameplay from uncached files is not guaranteed.' + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine + NewLine;
  if WinFspInstalled() then
    Result := Result + 'WinFsp (virtual drive driver): already installed'
  else
    Result := Result + 'WinFsp (virtual drive driver): will be installed - Windows asks for permission once';
  if not WebView2Installed() then
    Result := Result + NewLine + 'Microsoft WebView2 (TeraBox sign-in): will be downloaded and installed';
end;

function InitializeUninstall(): Boolean;
var
  Busy: String;
begin
  Result := True;
  Busy := RunningFrom(ExpandConstant('{app}'));
  if Lowercase(Busy) = 'cloudssd.agent.exe' then
  begin
    if MsgBox('A permanent CloudSSD cloud drive is mounted by the background agent.' + #13#10#13#10 +
              'Unmount it now? Pending uploads get up to 60 s to finish. Your cloud files, logins and cache are kept.',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
    StopAgent();
    Busy := RunningFrom(ExpandConstant('{app}'));
  end;
  if Busy <> '' then
  begin
    MsgBox(BusyMessage(Busy), mbInformation, MB_OK);
    Result := False;
  end;
end;
