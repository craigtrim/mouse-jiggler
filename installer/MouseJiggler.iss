; Mouse Jiggler per-user installer.
;
; Per-user by design: PrivilegesRequired=lowest, a fixed path under the user's
; local app data, and no elevation override anywhere. A keep-awake utility has
; no reason to ask for administrator rights, and asking would be a reason for a
; cautious user to decline.
;
; Requires Inno Setup 6.7.3 (pinned in scripts/tool-versions.json).

#define AppName "Mouse Jiggler"
#define AppVersion "1.0.0"
#define AppPublisher "Mouse Jiggler contributors"
#define AppUrl "https://github.com/craigtrim/mouse-jiggler"
#define ExeName "MouseJiggler.exe"

; The uninstall registry key is named after this. It is defined once because the
; [Setup] directive and the upgrade check have to agree, and when they silently
; disagreed every upgrade behaved as a first install.
#define ProductCode "{6EB8C7A8-8891-4E78-8DC7-2A6D8F919C02}"

[Setup]
AppId={{#ProductCode}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user install, no elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; A fixed path keeps the registered startup command stable across upgrades.
DefaultDirName={localappdata}\Programs\MouseJiggler
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}

; x64 desktop Windows only. ARM64 and emulated x64 are out of scope for 1.0.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045

OutputDir=..\artifacts
OutputBaseFilename=MouseJiggler-{#AppVersion}-windows-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#ExeName}
; The setup executable carries the product icon too, so a download in the browser and the
; file left in Downloads afterwards are recognisable rather than a generic installer.
SetupIconFile=..\assets\MouseJiggler.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup"; Flags: checkedonce

[Files]
Source: "..\artifacts\payload\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\payload\MouseJiggler.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\payload\MouseJiggler.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\payload\MouseJiggler.Windows.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\payload\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\payload\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\payload\README-install.txt"; DestDir: "{app}"; Flags: ignoreversion

; Ships only with the installer. Its absence is what identifies a portable copy.
Source: "..\artifacts\installed.marker"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
; No desktop shortcut: this is a tray utility, and the desktop is the user's.

[Run]
; Initialization runs once, after the files are in place, and reports its result
; through an exit code. It runs no engine and shows no window.
Filename: "{app}\{#ExeName}"; \
  Parameters: "--install-initialize --startup-enabled true"; \
  StatusMsg: "Configuring startup..."; \
  Flags: runhidden waituntilterminated; \
  Tasks: startup; \
  Check: not IsUpgrade

Filename: "{app}\{#ExeName}"; \
  Parameters: "--install-initialize --startup-enabled false"; \
  StatusMsg: "Completing setup..."; \
  Flags: runhidden waituntilterminated; \
  Tasks: not startup; \
  Check: not IsUpgrade

Filename: "{app}\{#ExeName}"; \
  Description: "Launch {#AppName}"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Ask the running copy of this exact installation to close before files go away.
Filename: "{app}\{#ExeName}"; Parameters: "--shutdown-for-update"; Flags: runhidden waituntilterminated; RunOnceId: "ShutdownForUninstall"

[Code]
const
  GENERIC_READ = $80000000;
  GENERIC_WRITE = $40000000;
  OPEN_EXISTING = 3;
  INVALID_HANDLE = $FFFFFFFF;

  { GetWindowsVersionEx reports this for a client SKU. Anything else is a server. }
  ProductTypeWorkstation = 1;

  { How long to wait for a copy that has just been asked to close. }
  ShutdownPollMilliseconds = 250;
  ShutdownPollAttempts = 24;

function OpenFileHandle(FileName: String; Access, ShareMode, Security, Disposition, Flags, Template: Cardinal): Cardinal;
  external 'CreateFileW@kernel32.dll stdcall';

function CloseFileHandle(Handle: Cardinal): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  UpgradeDetected: Boolean;

function IsUpgrade: Boolean;
begin
  Result := UpgradeDetected;
end;

{ Whether the executable is still loaded, asked the only way that actually
  answers it: open for writing with no sharing. A running image is held with
  read sharing only, so the request fails while it is running and succeeds the
  moment it is not.

  Renaming is not a substitute. Windows allows a running executable to be
  renamed, so that test passes on a machine where the app is very much running. }
function ExecutableIsInUse(FileName: String): Boolean;
var
  Handle: Cardinal;
begin
  if not FileExists(FileName) then
  begin
    Result := False;
    Exit;
  end;

  Handle := OpenFileHandle(FileName, GENERIC_READ or GENERIC_WRITE, 0, 0, OPEN_EXISTING, 0, 0);
  Result := Handle = INVALID_HANDLE;

  if not Result then
    CloseFileHandle(Handle);
end;

{ Asks this session's copy to close, then waits for the image to be released.
  Returns True when nothing holds it any more. }
function TryCloseRunningCopy(FileName: String): Boolean;
var
  ResultCode: Integer;
  Attempt: Integer;
begin
  if not ExecutableIsInUse(FileName) then
  begin
    Result := True;
    Exit;
  end;

  Exec(FileName, '--shutdown-for-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  { The instance answers and then exits, so the image is released slightly after
    the request returns. Polling is what closes that gap. }
  for Attempt := 1 to ShutdownPollAttempts do
  begin
    if not ExecutableIsInUse(FileName) then
    begin
      Result := True;
      Exit;
    end;

    Sleep(ShutdownPollMilliseconds);
  end;

  Result := False;
end;

{ The .NET Framework 4.8 release number. Every supported Windows build ships
  with it, so a missing runtime means something is wrong with the machine
  rather than something setup should try to fix by running an elevated
  bootstrapper. }
function FrameworkRelease: Cardinal;
var
  Release: Cardinal;
begin
  Result := 0;
  if RegQueryDWordValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := Release;
end;

function InitializeSetup: Boolean;
var
  PreviousVersion: String;
  Version: TWindowsVersion;
begin
  Result := True;

  { MinVersion excludes Server 2019 by build number but not Server 2022 or 2025,
    which are numbered above Windows 10 22H2. A keep-awake tray utility has no
    meaning on a server: there is no interactive session to keep awake, the sleep
    policy is set centrally, and an administrator finding it installed would
    reasonably want to know how it got there. }
  GetWindowsVersionEx(Version);
  if Version.ProductType <> ProductTypeWorkstation then
  begin
    MsgBox('Mouse Jiggler is for desktop editions of Windows and will not be installed on Windows Server.' + #13#10#13#10 +
           'Server sleep and display behaviour is managed by power policy rather than by an application in the notification area.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  if FrameworkRelease < 528040 then
  begin
    MsgBox('Mouse Jiggler needs .NET Framework 4.8, which is normally already part of Windows 10 22H2 and Windows 11.' + #13#10#13#10 +
           'Please repair or install it from https://dotnet.microsoft.com/download/dotnet-framework and run setup again.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  { An existing installation means this is an upgrade: settings, the explicit
    stopped state and the Windows startup decision are all left alone. }
  { SetupSetting('AppId') returns the raw script text, which for a GUID keeps the
    doubled opening brace Inno uses to escape it. The registry key it named therefore
    never existed, UpgradeDetected was false on every run, and three behaviours failed
    silently as a result: the startup decision was reset on every upgrade, the running
    copy was never asked to close, and the downgrade guard never fired. The product
    code is a define now, so there is one string and nothing to escape. }
  UpgradeDetected := RegQueryStringValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductCode}_is1',
    'DisplayVersion', PreviousVersion);

  if UpgradeDetected then
  begin
    { Refuse to go backwards rather than downgrade a settings file the older
      build may not understand. }
    if CompareStr(PreviousVersion, '{#AppVersion}') > 0 then
    begin
      MsgBox('A newer version of Mouse Jiggler (' + PreviousVersion + ') is already installed.' + #13#10#13#10 +
             'Uninstall it first if you really want to go back to {#AppVersion}.',
             mbCriticalError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  { Ask a running copy of this exact installation to close before replacing its
    files. Anything at another path answers with an ownership conflict and is
    left alone rather than closed.

    Deliberately not gated on UpgradeDetected. An executable sitting in the target
    directory has to be closed before it can be replaced whether or not the registry
    agrees that this is an upgrade, and tying the two together is what hid the
    detection bug: the files were replaced anyway, by Restart Manager, so nothing
    looked wrong. }
  if FileExists(ExpandConstant('{app}\{#ExeName}')) then
  begin
    if Exec(ExpandConstant('{app}\{#ExeName}'), '--shutdown-for-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if ResultCode = 3 then
      begin
        Result := 'Another copy of Mouse Jiggler is running from a different folder. Please close it and run setup again.';
        Exit;
      end;
    end;

    { The request returns before the process has finished exiting, and replacing
      an image that is still loaded fails. Waiting for the handle to be released
      is what makes the upgrade reliable rather than usually fine. }
    if not TryCloseRunningCopy(ExpandConstant('{app}\{#ExeName}')) then
      Result := 'Mouse Jiggler is still running and its files cannot be replaced. If it is running for another signed-in user, sign that session out first, then run setup again.';
  end;
end;

{ Remove the startup entry only when it points into the installation being removed.
  Done here in registry code rather than by running the app, because by the time the
  uninstaller reaches this point the executable is on its way out. A portable copy's
  registration, or another product's value, is left untouched. }
procedure RemoveOwnedStartupEntry;
var
  Command: String;
  AppPath: String;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'MouseJiggler', Command) then
    Exit;

  AppPath := ExpandConstant('{app}');

  { Pos rather than an exact match: the stored command is a quoted path followed by
    --startup. Comparing against the installation directory is what distinguishes this
    installation from a portable copy that registered itself from somewhere else. }
  if Pos(Uppercase(AppPath), Uppercase(Command)) > 0 then
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'MouseJiggler');
end;

{ Nothing may be removed while a copy is still holding the image. Deleting around
  a running process leaves a half removed installation: the executable survives
  because it cannot be deleted, the uninstall entry goes anyway, and there is then
  no supported way to finish the job.

  Closing works only for a copy in this Windows session. The mutex and the pipe
  are both per session, so a copy left running under another signed-in account or
  behind fast user switching cannot be reached from here. That case is reported
  rather than guessed at. }
function InitializeUninstall: Boolean;
var
  ExePath: String;
begin
  Result := True;
  ExePath := ExpandConstant('{app}\{#ExeName}');

  if TryCloseRunningCopy(ExePath) then
    Exit;

  if UninstallSilent then
  begin
    { An unattended run has nobody to ask, and stopping leaves a working
      installation rather than a broken one. }
    Result := False;
    Exit;
  end;

  while True do
  begin
    if MsgBox('Mouse Jiggler is still running and cannot be removed yet.' + #13#10#13#10 +
              'If it is running for another signed-in user, or in a session left behind by fast user switching, sign that session out first. Otherwise close it from the notification area.' + #13#10#13#10 +
              'Try again?', mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;

    if TryCloseRunningCopy(ExePath) then
      Exit;
  end;
end;

{ Whether a writer is active right now. Opened with no sharing, the same way the
  application takes the lock, so a copy part way through a save makes this fail.

  What this does not establish is that no copy is running. The application holds
  the lock only while it writes, so an idle copy leaves it free, and the delete
  below will then succeed and be undone by that copy's next save. There is no
  better signal available here: the ownership mutex is scoped Local\, which makes
  it invisible from any other session, and nothing else in the data directory is
  held open. So the prompt asks the user to close other copies, because that is
  the part only they can do, and this catches the narrower case of a save in
  flight, which is the one that would corrupt rather than merely undo.

  An absent lock file means no copy has ever written here, which is not a reason
  to refuse. }
function SettingsAreUnlocked(DataDir: String): Boolean;
var
  LockPath: String;
  Handle: Cardinal;
begin
  LockPath := DataDir + '\settings.lock';

  if not FileExists(LockPath) then
  begin
    Result := True;
    Exit;
  end;

  Handle := OpenFileHandle(LockPath, GENERIC_READ or GENERIC_WRITE, 0, 0, OPEN_EXISTING, 0, 0);

  if Handle = INVALID_HANDLE then
  begin
    Result := False;
    Exit;
  end;

  CloseFileHandle(Handle);
  Result := True;
end;

{ Every file this product creates in its data directory, and nothing else.

  The recovery backups matter as much as settings.json does. They are whole
  copies of a settings file, so leaving them behind means answering yes to
  "delete my settings" and still having the settings on disk. They are named
  settings.invalid-<timestamp>.json and the application keeps at most three, so
  they are found rather than guessed at. }
procedure DeleteOwnedDataFiles(DataDir: String);
var
  Search: TFindRec;
  LogName: String;
  Index: Integer;
begin
  DeleteFile(DataDir + '\settings.json');
  DeleteFile(DataDir + '\settings.json.bak');
  DeleteFile(DataDir + '\settings.lock');

  if FindFirst(DataDir + '\settings.invalid-*.json', Search) then
  begin
    try
      repeat
        DeleteFile(DataDir + '\' + Search.Name);
      until not FindNext(Search);
    finally
      FindClose(Search);
    end;
  end;

  { A settings.tmp-<guid> left behind by a write that was interrupted. It holds
    whole settings content, so it counts as settings for the purpose of a request
    to delete them. }
  if FindFirst(DataDir + '\settings.tmp-*', Search) then
  begin
    try
      repeat
        DeleteFile(DataDir + '\' + Search.Name);
      until not FindNext(Search);
    finally
      FindClose(Search);
    end;
  end;

  { The current log and its four numbered backups, named rather than swept, so a
    file someone else put in this folder is left where they put it. }
  LogName := DataDir + '\Logs\mousejiggler.log';
  DeleteFile(LogName);

  for Index := 1 to 4 do
    DeleteFile(LogName + '.' + IntToStr(Index));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Before the files go away, while the app directory still resolves. }
    RemoveOwnedStartupEntry;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    { A silent uninstall must never stop and wait for someone. Keeping the data is the
      safe default, and it is what an unattended run gets. }
    if UninstallSilent then
      Exit;

    if MsgBox('Delete your Mouse Jiggler settings and diagnostics as well?' + #13#10#13#10 +
              'The installed and portable editions share this data, so a portable copy would also lose its settings.' + #13#10#13#10 +
              'Close any portable copy first, in this session and in any other signed-in session. A copy still running holds these settings in memory and writes them back, so deleting them now would not last.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DataDir := ExpandConstant('{localappdata}\MouseJiggler');

      { The installation being removed is already known to be closed, or this
        uninstall would not have got past InitializeUninstall. A portable copy of
        the same user is a different matter: it shares this directory, it can be
        running in this session or another one, and nothing so far has looked for
        it. Deleting settings.json underneath it would not even take effect. The
        portable copy holds the state in memory and writes it back on its next
        save, so the user is told their data is gone while it is not.

        The settings lock is what answers this across sessions, because it is a
        file rather than a per-session kernel object. }
      if not SettingsAreUnlocked(DataDir) then
      begin
        MsgBox('Mouse Jiggler has been removed, but your settings were left in place.' + #13#10#13#10 +
               'Another copy of Mouse Jiggler is writing to them right now, most likely a portable copy, possibly in another signed-in session. Close it, then delete ' + DataDir + ' yourself if you still want the settings gone.',
               mbInformation, MB_OK);
        Exit;
      end;

      DeleteOwnedDataFiles(DataDir);

      { Only if this app put nothing else there. RemoveDir refuses a directory
        that still holds anything, which is the behaviour wanted: a file this
        product did not create is a file it has no business removing. }
      RemoveDir(DataDir + '\Logs');
      RemoveDir(DataDir);
    end;
  end;
end;
