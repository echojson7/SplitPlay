; SPDX-License-Identifier: MIT
; SplitPlay installer (Inno Setup 6.3 or newer).
;
; Build:
;   ISCC.exe /DStageDir=C:\path\to\stage /DAppVersion=0.1.0 [/DOutputDir=C:\out] installer\splitplay.iss
; Output: <OutputDir>\SplitPlay-Setup-<AppVersion>.exe
; The stage folder layout is documented in installer/README.md.
;
; Setup command-line parameters (besides the standard Inno Setup ones):
;   /AllowUnsupportedOS=1  install on Windows older than 11 22H2 or on Windows Server (CI, testing)
;   /SkipWebView2=1        do not download the WebView2 runtime even if it is missing
;   /SkipViGEmBus=1        do not install the bundled ViGEmBus driver even if it is missing
;   /ViGEmBusOnServer=1    run the ViGEmBus setup on Windows Server too (it refuses Server; CI tests the timeout path)
;   /ViGEmBusTimeout=<s>   seconds to wait for the ViGEmBus setup or removal before killing it (default 120, 5 to 3600)
;   /SkipTermWrap=1        install the TermWrap files but leave TermService's ServiceDll alone
;                          ("splitplay host prepare --skip-termwrap"; CI runners must keep their RDP stack)
; Uninstaller parameters:
;   /PurgeData=1           also delete %ProgramData%\SplitPlay (default: keep it; interactive runs ask)
;   /ViGEmBusTimeout=<s>   as for setup, for removing a ViGEmBus that SplitPlay installed

#if VER < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required
#endif

#ifndef StageDir
  #define StageDir AddBackslash(SourcePath) + "..\build\stage"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\build\installer"
#endif
#ifndef WebPort
  #define WebPort "38299"
#endif
; ViGEmBus (nefarius/ViGEmBus, BSD-3-Clause): the official signed setup, staged unmodified in <StageDir>\vigembus.
#ifndef ViGEmBusSetup
  #define ViGEmBusSetup "ViGEmBus_1.22.0_x64_x86_arm64.exe"
#endif

; VersionInfoVersion must be numeric: strip any "-suffix" from AppVersion.
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

; Fail at compile time if the stage folder is incomplete.
#if !FileExists(AddBackslash(StageDir) + "service\SplitPlay.Service.exe")
  #error Missing staged file service\SplitPlay.Service.exe (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "service\splitplay.exe")
  #error Missing staged file service\splitplay.exe (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "tray\SplitPlay.Tray.exe")
  #error Missing staged file tray\SplitPlay.Tray.exe (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "service\wwwroot\index.html")
  #error Missing staged file service\wwwroot\index.html (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "native\splitplay-seat.exe")
  #error Missing staged file native\splitplay-seat.exe (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "native\splitplay-rdsprov.dll")
  #error Missing staged file native\splitplay-rdsprov.dll (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "native\splitplay-stubworker.exe")
  #error Missing staged file native\splitplay-stubworker.exe (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "worker\splitplay-worker.exe")
  #error Missing staged file worker\splitplay-worker.exe (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "worker\COPYING")
  #error Missing staged file worker\COPYING (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "termwrap\TermWrap.dll")
  #error Missing staged file termwrap\TermWrap.dll (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "termwrap\UmWrap.dll")
  #error Missing staged file termwrap\UmWrap.dll (check /DStageDir, see installer/README.md)
#endif
#if !FileExists(AddBackslash(StageDir) + "vigembus\" + ViGEmBusSetup)
  #error Missing staged ViGEmBus setup in vigembus\ (check /DStageDir and /DViGEmBusSetup, see installer/README.md)
#endif

[Setup]
AppId={{6F1C3B2A-5D4E-4C7B-9A8E-2B1D0F3C5A71}
AppName=SplitPlay
AppVersion={#AppVersion}
AppVerName=SplitPlay {#AppVersion}
AppPublisher=SplitPlay contributors
AppPublisherURL=https://github.com/echojson7/SplitPlay
AppSupportURL=https://github.com/echojson7/SplitPlay/issues
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=SplitPlay
DefaultDirName={autopf}\SplitPlay
DefaultGroupName=SplitPlay
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; The real check (Windows 11 22H2+, warning below 24H2) is in InitializeSetup so that it can be overridden.
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=SplitPlay-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=SplitPlay
UninstallDisplayIcon={app}\tray\SplitPlay.Tray.exe
SetupLogging=yes
ChangesEnvironment=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.LicenseLabel=Please read the licence and the Windows licensing notice below.
ru.LicenseLabel=Прочитайте лицензию и предупреждение о лицензии Windows ниже.
en.RdsNotice=IMPORTANT: WINDOWS LICENSING NOTICE%n%nSplitPlay runs several desktops at once on one PC. To do that it modifies Windows Remote Desktop Services so that several users can be signed in at the same time (concurrent sessions, through the TermWrap component).%n%nOn Windows client editions (Windows 11 Home, Pro, Enterprise and Education) concurrent remote sessions are not permitted, so this modification violates the Microsoft Software License Terms for your copy of Windows. You use it at your own risk; it may also be flagged by antivirus software and can stop working after Windows updates.%n%nTermWrap is included and selected by default. Setup copies it into the SplitPlay folder and points the Remote Desktop service at it (the same registry change as TermWrap's own install script), turns Remote Desktop on for loopback desks and records every previous value; uninstalling SplitPlay restores them. Windows must be restarted once before desks can start. If you clear the TermWrap component, Remote Desktop Services are not replaced, but a desk cannot run while you are signed in at the PC.%n%nBy accepting, you confirm that you have read and understood this notice and the licence below.%n%n------------------------------------------------------------%n%n
ru.RdsNotice=ВАЖНО: ПРЕДУПРЕЖДЕНИЕ О ЛИЦЕНЗИИ WINDOWS%n%nSplitPlay запускает несколько рабочих столов одновременно на одном ПК. Для этого он изменяет службы удалённых рабочих столов Windows, чтобы несколько пользователей могли работать одновременно (параллельные сеансы, компонент TermWrap).%n%nКлиентские редакции Windows (Windows 11 Домашняя, Pro, Корпоративная и для образовательных учреждений) не разрешают параллельные удалённые сеансы, поэтому это изменение нарушает условия лицензии Microsoft на вашу копию Windows. Вы используете его на свой страх и риск; антивирусы могут на него реагировать, а обновления Windows могут его ломать.%n%nTermWrap входит в сборку и выбран по умолчанию. Установщик копирует его в папку SplitPlay и подключает к службе удалённых рабочих столов (то же изменение реестра, что и в установочном скрипте самого TermWrap), включает удалённый рабочий стол для локальных столов и запоминает все прежние значения; удаление SplitPlay их восстанавливает. Перед первым запуском столов Windows нужно один раз перезагрузить. Если снять компонент TermWrap, службы удалённых рабочих столов не заменяются, но стол не сможет работать, пока вы вошли в систему на этом ПК.%n%nПринимая условия, вы подтверждаете, что прочитали и поняли это предупреждение и лицензию ниже.%n%n------------------------------------------------------------%n%n
en.TypeFull=Full installation
ru.TypeFull=Полная установка
en.TypeCustom=Custom installation
ru.TypeCustom=Выборочная установка
en.CompCore=SplitPlay host (service, CLI, tray, web UI, native helpers, worker)
ru.CompCore=Хост SplitPlay (служба, CLI, трей, веб-интерфейс, нативные модули, воркер)
en.CompTermWrap=TermWrap concurrent sessions (several desks while you are signed in; needs a restart)
ru.CompTermWrap=TermWrap: параллельные сеансы (несколько столов, пока вы в системе; нужна перезагрузка)
en.TaskTrayAutostart=Start the SplitPlay tray when a user signs in
ru.TaskTrayAutostart=Запускать трей SplitPlay при входе пользователя
en.LaunchTray=Launch SplitPlay
ru.LaunchTray=Запустить SplitPlay
en.WebUiShortcut=SplitPlay Web UI
ru.WebUiShortcut=Веб-интерфейс SplitPlay
en.OsTooOld=SplitPlay requires Windows 11 version 22H2 (build 22621) or newer. This PC runs build %1.
ru.OsTooOld=Для SplitPlay нужна Windows 11 версии 22H2 (сборка 22621) или новее. На этом ПК сборка %1.
en.OsBelow24H2=This PC runs Windows 11 build %1. SplitPlay is designed for Windows 11 24H2 (build 26100) or newer; some features (HDR, high refresh rates in remote sessions) may not work.%n%nContinue anyway?
ru.OsBelow24H2=На этом ПК Windows 11 сборки %1. SplitPlay рассчитан на Windows 11 24H2 (сборка 26100) или новее; часть функций (HDR, высокая частота в удалённых сеансах) может не работать.%n%nПродолжить?
en.OsServer=This is a Windows Server edition. SplitPlay targets Windows 11 Pro and Enterprise and is not tested on Windows Server.%n%nContinue anyway?
ru.OsServer=Это серверная редакция Windows. SplitPlay рассчитан на Windows 11 Pro и Корпоративную и на Windows Server не проверялся.%n%nПродолжить?
en.WebView2Missing=Downloading the Microsoft Edge WebView2 runtime (needed by the tray app)...
ru.WebView2Missing=Загрузка среды Microsoft Edge WebView2 (нужна приложению в трее)...
en.WebView2DownloadFailed=The WebView2 runtime could not be downloaded: %1%n%nSplitPlay will be installed anyway; the web UI works in any browser. Install WebView2 later from https://developer.microsoft.com/microsoft-edge/webview2/.
ru.WebView2DownloadFailed=Не удалось загрузить среду WebView2: %1%n%nSplitPlay всё равно будет установлен; веб-интерфейс работает в любом браузере. WebView2 можно установить позже: https://developer.microsoft.com/microsoft-edge/webview2/.
en.ServiceInstallFailed=Registering the SplitPlay service failed (exit code %1). See the setup log for details; you can retry with "splitplay service install" from an elevated prompt.
ru.ServiceInstallFailed=Не удалось зарегистрировать службу SplitPlay (код %1). Подробности в журнале установки; повторить можно командой "splitplay service install" от имени администратора.
en.ServiceStartFailed=The SplitPlay service was installed but did not start (exit code %1). See the logs in %2.
ru.ServiceStartFailed=Служба SplitPlay установлена, но не запустилась (код %1). Логи: %2.
en.KeepDataQuestion=Keep the SplitPlay configuration, desk data and logs in %1?%n%nYes: keep them for a later reinstall.%nNo: delete them.
ru.KeepDataQuestion=Сохранить конфигурацию, данные столов и логи SplitPlay в %1?%n%nДа: оставить для повторной установки.%nНет: удалить.
en.StatusAcl=Securing the data folder...
ru.StatusAcl=Настройка прав на папку данных...
en.StatusFirewall=Adding firewall rules...
ru.StatusFirewall=Добавление правил брандмауэра...
en.StatusService=Registering and starting the SplitPlay service...
ru.StatusService=Регистрация и запуск службы SplitPlay...
en.StatusWebView2=Installing the WebView2 runtime...
ru.StatusWebView2=Установка среды WebView2...
en.StatusViGEmBus=Installing the ViGEmBus gamepad driver...
ru.StatusViGEmBus=Установка драйвера геймпадов ViGEmBus...
en.StatusHost=Preparing Remote Desktop for SplitPlay desks...
ru.StatusHost=Настройка удалённого рабочего стола для столов SplitPlay...
en.HostPrepareFailed=Preparing the Remote Desktop host failed (exit code %1). Desks cannot start until it succeeds: run "splitplay host prepare" from an elevated prompt and see the setup log for details.
ru.HostPrepareFailed=Не удалось настроить удалённый рабочий стол (код %1). Столы не запустятся, пока настройка не пройдёт: выполните "splitplay host prepare" от имени администратора; подробности в журнале установки.

[Types]
Name: "full"; Description: "{cm:TypeFull}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

[Components]
Name: "core"; Description: "{cm:CompCore}"; Types: full custom; Flags: fixed
; TermWrap (llccd/TermWrap, MIT): "splitplay host prepare" points TermService's ServiceDll at {app}\termwrap.
Name: "termwrap"; Description: "{cm:CompTermWrap}"; Types: full custom

[Tasks]
Name: "trayautostart"; Description: "{cm:TaskTrayAutostart}"; Components: core

[Dirs]
Name: "{commonappdata}\SplitPlay"; Flags: uninsneveruninstall
Name: "{commonappdata}\SplitPlay\desks"; Flags: uninsneveruninstall
Name: "{commonappdata}\SplitPlay\logs"; Flags: uninsneveruninstall

[Files]
Source: "{#StageDir}\service\*"; DestDir: "{app}\service"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs
; The tray carries its own self-contained runtime (with WindowsDesktop), so it cannot share the service folder.
Source: "{#StageDir}\tray\*"; DestDir: "{app}\tray"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\native\*"; DestDir: "{app}\native"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\worker\*"; DestDir: "{app}\worker"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs
; TermService (svchost) keeps TermWrap.dll loaded: an upgrade replaces it at the next restart, and the uninstaller
; deletes it at the next restart after "host revert" has pointed TermService back at termsrv.dll.
Source: "{#StageDir}\termwrap\*"; DestDir: "{app}\termwrap"; Components: termwrap; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace uninsrestartdelete
; Driver packages are shipped but not installed in Stage 2 (pnputil comes with the drivers in Stage 4).
Source: "{#StageDir}\drivers\*"; DestDir: "{app}\drivers"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Interim gamepads (until the Stage 4 pad driver): extracted to {tmp} and run only when ViGEmBus is missing.
Source: "{#StageDir}\vigembus\{#ViGEmBusSetup}"; DestDir: "{tmp}"; Flags: dontcopy
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Components: core; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; Components: core; Flags: ignoreversion

[INI]
Filename: "{group}\{cm:WebUiShortcut}.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://127.0.0.1:{#WebPort}/"

[Icons]
Name: "{group}\SplitPlay"; Filename: "{app}\tray\SplitPlay.Tray.exe"; WorkingDir: "{app}\tray"
Name: "{group}\{cm:UninstallProgram,SplitPlay}"; Filename: "{uninstallexe}"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SplitPlayTray"; ValueData: """{app}\tray\SplitPlay.Tray.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: trayautostart

[Run]
; Started by hand the tray opens its dashboard window; the Run entry above passes --autostart to stay an icon.
Filename: "{app}\tray\SplitPlay.Tray.exe"; Description: "{cm:LaunchTray}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallDelete]
Type: files; Name: "{group}\{cm:WebUiShortcut}.url"
Type: dirifempty; Name: "{app}"

[Code]
const
  ServiceName = 'SplitPlay';
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\SplitPlay';
  FirewallRuleWeb = 'SplitPlay Web UI';
  { Per-desk rules created by the service ("SplitPlay desk <id> (TCP|UDP)"). }
  FirewallRuleDeskPattern = 'SplitPlay desk *';
  { "splitplay host prepare|revert": the change needs a restart. }
  ExitRebootRequired = 3010;
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2File = 'MicrosoftEdgeWebview2Setup.exe';
  BuildWin11_22H2 = 22621;
  BuildWin11_24H2 = 26100;
  { ViGEmBus. SplitPlay records in SplitPlayKey that it installed the driver, and removes it on uninstall only then. }
  SplitPlayKey = 'Software\SplitPlay';
  ViGEmBusMarkerValue = 'ViGEmBusInstalledBySplitPlay';
  ViGEmBusProductValue = 'ViGEmBusProductCode';
  ViGEmBusServiceKey = 'SYSTEM\CurrentControlSet\Services\ViGEmBus';
  { ProductCode of the ViGEmBus 1.22.0 MSI (setup/ViGEmBus.aip at tag v1.22.0, same in the winget manifest): recorded
    when its Uninstall entry exists, and also when no ViGEmBus entry that the setup added is found. }
  ViGEmBusProductCode = '{9C581C76-2D68-40F8-AA6F-94D3C5215C05}';
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';
  { The ViGEmBus MSI runs nefconw.exe (deferred custom actions) to create the device node and install the driver. }
  ViGEmBusHelperImage = 'nefconw';
  DefaultViGEmBusTimeoutSec = 120;
  MaxViGEmBusTimeoutSec = 3600;
  { After a timeout: how long to wait for the Windows Installer service to finish (commit or roll back). }
  MsiIdleWaitSec = 60;
  ExitTimedOut = 1460; { ERROR_TIMEOUT }

var
  DownloadPage: TDownloadWizardPage;
  WebView2Downloaded: Boolean;
  HostRebootRequired: Boolean;
  ViGEmBusRebootRequired: Boolean;
  UninstallRebootRequired: Boolean;

{ Runs a program hidden and waits; returns its exit code, or -1 if it could not start. }
function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  Log('Running: "' + FileName + '" ' + Params);
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Log(Format('  exit code %d', [Code]));
    Result := Code;
  end
  else
  begin
    Log(Format('  could not start: %s', [SysErrorMessage(Code)]));
    Result := -1;
  end;
end;

function SysTool(const Name: String): String;
begin
  Result := ExpandConstant('{sys}\' + Name);
end;

{ A PowerShell single-quoted string. PowerShell also ends such a string at the typographic single quotes U+2018 to
  U+201B, so those are doubled too. }
function PsQuote(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '''', '''''', True);
  StringChangeEx(Result, #$2018, #$2018#$2018, True);
  StringChangeEx(Result, #$2019, #$2019#$2019, True);
  StringChangeEx(Result, #$201A, #$201A#$201A, True);
  StringChangeEx(Result, #$201B, #$201B#$201B, True);
  Result := '''' + Result + '''';
end;

{ Like RunHidden, but never waits longer than TimeoutSec (Exec alone waits forever). On timeout it kills every
  HelperImage process (a helper the program waits for, e.g. a custom action of an MSI it runs), gives the program
  15 s to finish, then kills its whole process tree, waits up to MsiIdleWaitSec for the Windows Installer service to
  finish what the program started (it commits or rolls back on its own; killing the helper again could break the
  rollback), and returns ExitTimedOut. Runs through Windows PowerShell; returns -1 or PowerShell's error code if that
  cannot run. }
function RunHiddenWithTimeout(const FileName, Params: String; TimeoutSec: Integer; const HelperImage: String): Integer;
var
  Script: String;
  Lines: TArrayOfString;
begin
  Script := ExpandConstant('{tmp}\splitplay-run-with-timeout.ps1');
  SetArrayLength(Lines, 9);
  Lines[0] := '$ErrorActionPreference = ''Stop''';
  Lines[1] := '$p = Start-Process -FilePath ' + PsQuote(FileName) + ' -ArgumentList ' + PsQuote(Params) +
    ' -PassThru -WindowStyle Hidden';
  Lines[2] := '$null = $p.Handle';
  Lines[3] := 'if ($p.WaitForExit(' + IntToStr(TimeoutSec * 1000) + ')) { exit $p.ExitCode }';
  Lines[4] := 'Get-Process -Name ' + PsQuote(HelperImage) +
    ' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue';
  Lines[5] := 'if (-not $p.WaitForExit(15000)) { & taskkill.exe /F /T /PID $p.Id | Out-Null }';
  { Global\_MSIExecute exists while the Windows Installer service runs an installation (or its rollback). Any error
    other than "does not exist" (e.g. access denied) counts as busy. }
  Lines[6] := '$deadline = (Get-Date).AddSeconds(' + IntToStr(MsiIdleWaitSec) + ')';
  Lines[7] := 'while ((Get-Date) -lt $deadline) { $m = $null; try { if (-not [System.Threading.Mutex]::TryOpenExisting(' +
    '''Global\_MSIExecute'', [ref] $m)) { break }; $m.Dispose() } catch { }; Start-Sleep -Milliseconds 500 }';
  Lines[8] := 'exit ' + IntToStr(ExitTimedOut);
  Log(Format('Running with a %d s timeout: "%s" %s', [TimeoutSec, FileName, Params]));
  { Inno Setup writes this file as UTF-8 with a BOM (SaveStringsToUTF8FileWithoutBOM is the variant without one),
    which Windows PowerShell 5.1 needs to read non-ASCII paths (e.g. Cyrillic) correctly. }
  if not SaveStringsToUTF8File(Script, Lines, False) then
  begin
    Log('  could not write ' + Script);
    Result := -1;
    Exit;
  end;
  Result := RunHidden(SysTool('WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(Script));
  if Result = ExitTimedOut then
    Log(Format('  timed out after %d s; killed %s.exe and the process tree, waited up to %d s for Windows Installer', [
      TimeoutSec, HelperImage, MsiIdleWaitSec]));
  DeleteFile(Script);
end;

{ 5 to 3600 s (more would overflow the milliseconds passed to WaitForExit). }
function ViGEmBusTimeoutSec: Integer;
begin
  Result := StrToIntDef(ExpandConstant('{param:ViGEmBusTimeout|' + IntToStr(DefaultViGEmBusTimeoutSec) + '}'),
    DefaultViGEmBusTimeoutSec);
  if Result < 5 then
    Result := 5
  else if Result > MaxViGEmBusTimeoutSec then
    Result := MaxViGEmBusTimeoutSec;
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, ServiceKey);
end;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\SplitPlay');
end;

function ParamIsSet(const Name: String): Boolean;
begin
  Result := ExpandConstant('{param:' + Name + '|0}') = '1';
end;

function WebView2Installed: Boolean;
var
  V: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', V) then
    Result := (V <> '') and (V <> '0.0.0.0');
  if not Result and RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', V) then
    Result := (V <> '') and (V <> '0.0.0.0');
end;

function InitializeSetup: Boolean;
var
  V: TWindowsVersion;
  Allow: Boolean;
begin
  Result := True;
  Allow := ParamIsSet('AllowUnsupportedOS');
  GetWindowsVersionEx(V);
  Log(Format('Windows %d.%d build %d, product type %d', [V.Major, V.Minor, V.Build, V.ProductType]));
  if (V.Major < 10) or (V.Build < BuildWin11_22H2) then
  begin
    if Allow then
      Log('Unsupported Windows build, continuing because of /AllowUnsupportedOS=1')
    else
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('OsTooOld'), [IntToStr(V.Build)]), mbCriticalError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
  end
  else if V.Build < BuildWin11_24H2 then
  begin
    if not Allow and (SuppressibleMsgBox(FmtMessage(CustomMessage('OsBelow24H2'), [IntToStr(V.Build)]),
      mbConfirmation, MB_YESNO, IDYES) = IDNO) then
    begin
      Result := False;
      Exit;
    end;
  end;
  if (V.ProductType <> VER_NT_WORKSTATION) and not Allow then
  begin
    if SuppressibleMsgBox(CustomMessage('OsServer'), mbConfirmation, MB_YESNO, IDYES) = IDNO then
      Result := False;
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if (ProgressMax > 0) and (Progress = ProgressMax) then
    Log('Downloaded ' + FileName);
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), CustomMessage('WebView2Missing'), @OnDownloadProgress);
  { Licence page: the Remote Desktop Services notice comes first, then the MIT licence. }
  WizardForm.LicenseLabel1.Caption := CustomMessage('LicenseLabel');
  WizardForm.LicenseMemo.Lines.Text := CustomMessage('RdsNotice') + WizardForm.LicenseMemo.Lines.Text;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not ParamIsSet('SkipWebView2') and not WebView2Installed then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(WebView2Url, WebView2File, '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        WebView2Downloaded := True;
      except
        if DownloadPage.AbortedByUser then
          Log('WebView2 download cancelled by the user')
        else
          SuppressibleMsgBox(FmtMessage(CustomMessage('WebView2DownloadFailed'), [GetExceptionMessage]),
            mbError, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { Upgrade: stop the running service so that its files can be replaced. net stop waits until it has stopped. }
  if ServiceExists then
    RunHidden(SysTool('net.exe'), 'stop ' + ServiceName);
end;

procedure SecureDataDir;
begin
  { Only SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544); children inherit. }
  WizardForm.StatusLabel.Caption := CustomMessage('StatusAcl');
  RunHidden(SysTool('icacls.exe'), AddQuotes(DataDir) +
    ' /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F');
end;

procedure AddFirewallRules;
begin
  { The web UI listens on loopback only by default, which the firewall does not filter. This rule is for
    LAN access and stays disabled until host.lanAccess is turned on (the service enables it then).
    Per-desk rules ("SplitPlay desk <id> (TCP|UDP)") are created and removed by the service. }
  WizardForm.StatusLabel.Caption := CustomMessage('StatusFirewall');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="' + FirewallRuleWeb + '"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall add rule name="' + FirewallRuleWeb + '"' +
    ' dir=in action=allow protocol=TCP localport={#WebPort} remoteip=localsubnet profile=private,domain enable=no' +
    ' program="' + ExpandConstant('{app}\service\SplitPlay.Service.exe') + '"' +
    ' description="SplitPlay web UI and API from the local network (enabled by the service when LAN access is on)"');
end;

{ Turns Remote Desktop on for loopback desks and, with the TermWrap component, swaps TermService's ServiceDll.
  Every previous value goes to ProgramData\SplitPlay\host-tuning.json for "host revert". }
procedure PrepareHost;
var
  Params: String;
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('StatusHost');
  Params := 'host prepare';
  if not WizardIsComponentSelected('termwrap') then
    Params := Params + ' --skip-termwrap'
  else if ParamIsSet('SkipTermWrap') then
  begin
    Log('/SkipTermWrap=1: TermWrap files installed, TermService left alone');
    Params := Params + ' --skip-termwrap';
  end;
  Code := RunHidden(ExpandConstant('{app}\service\splitplay.exe'), Params);
  if Code = ExitRebootRequired then
  begin
    Log('host prepare: a restart is required before desks can start');
    HostRebootRequired := True;
  end
  else if Code <> 0 then
    SuppressibleMsgBox(FmtMessage(CustomMessage('HostPrepareFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
end;

function NeedRestart: Boolean;
begin
  Result := HostRebootRequired or ViGEmBusRebootRequired;
end;

function UninstallNeedRestart: Boolean;
begin
  Result := UninstallRebootRequired;
end;

{ True when a ViGEmBus driver is installed: its binary exists in System32\drivers or where its service points. }
function ViGEmBusPresent: Boolean;
var
  Path: String;
begin
  Result := FileExists(ExpandConstant('{sys}\drivers\ViGEmBus.sys'));
  if not Result and RegQueryStringValue(HKLM, ViGEmBusServiceKey, 'ImagePath', Path) then
  begin
    if Pos('\SYSTEMROOT\', Uppercase(Path)) = 1 then
      Path := ExpandConstant('{win}') + Copy(Path, Length('\SystemRoot') + 1, Length(Path))
    else if Pos('\??\', Path) = 1 then
      Path := Copy(Path, 5, Length(Path))
    else if Pos(':', Path) = 0 then
      Path := ExpandConstant('{win}\') + Path;
    Result := FileExists(Path);
  end;
end;

{ The Uninstall subkeys of one registry view (empty if there are none). }
procedure GetUninstallKeys(const RootKey: Integer; var Keys: TArrayOfString);
begin
  if not RegGetSubkeyNames(RootKey, UninstallKey, Keys) then
    SetArrayLength(Keys, 0);
end;

function InList(const S: String; const List: TArrayOfString): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(List) - 1 do
    if CompareText(List[I], S) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

{ A Windows Installer Uninstall entry that is not in Before, names ViGEm and is published by Nefarius, or ''. }
function FindNewViGEmBusProductIn(const RootKey: Integer; const Before: TArrayOfString): String;
var
  Keys: TArrayOfString;
  I: Integer;
  Name, Publisher: String;
begin
  Result := '';
  GetUninstallKeys(RootKey, Keys);
  for I := 0 to GetArrayLength(Keys) - 1 do
    if (Copy(Keys[I], 1, 1) = '{') and not InList(Keys[I], Before) then
    begin
      Name := '';
      Publisher := '';
      RegQueryStringValue(RootKey, UninstallKey + '\' + Keys[I], 'DisplayName', Name);
      RegQueryStringValue(RootKey, UninstallKey + '\' + Keys[I], 'Publisher', Publisher);
      if ((Pos('VIGEM', Uppercase(Name)) > 0) or (Pos('VIRTUAL GAMEPAD EMULATION', Uppercase(Name)) > 0)) and
        (Pos('NEFARIUS', Uppercase(Publisher)) > 0) then
      begin
        Log('ViGEmBus Uninstall entry (new): ' + Keys[I] + ' (' + Name + ', ' + Publisher + ')');
        Result := Keys[I];
        Exit;
      end;
    end;
end;

{ The Windows Installer product code of the ViGEmBus that the bundled setup installed: the known 1.22.0 code when its
  Uninstall entry exists, otherwise a matching entry that the setup added (not in the Before lists), or ''. }
function FindViGEmBusProduct(const Before64, Before32: TArrayOfString): String;
begin
  if RegKeyExists(HKLM64, UninstallKey + '\' + ViGEmBusProductCode) or
    RegKeyExists(HKLM32, UninstallKey + '\' + ViGEmBusProductCode) then
  begin
    Log('ViGEmBus Uninstall entry: ' + ViGEmBusProductCode + ' (1.22.0)');
    Result := ViGEmBusProductCode;
    Exit;
  end;
  Result := FindNewViGEmBusProductIn(HKLM64, Before64);
  if Result = '' then
    Result := FindNewViGEmBusProductIn(HKLM32, Before32);
end;

{ Interim gamepads (until the Stage 4 per-session pad driver): the workers emulate Moonlight's gamepads as ViGEm pads,
  which every session sees. Installs the bundled ViGEmBus only when no ViGEmBus driver is present; a failure is logged
  and setup continues (desks then have no gamepads). }
procedure InstallViGEmBus;
var
  Code: Integer;
  Product: String;
  V: TWindowsVersion;
  Before64, Before32: TArrayOfString;
  Installed: Boolean;
begin
  if ParamIsSet('SkipViGEmBus') then
  begin
    Log('/SkipViGEmBus=1: ViGEmBus is not installed');
    Exit;
  end;
  { The ViGEmBus 1.22.0 MSI has a launch condition that refuses 64-bit Windows Server ("can not be installed on ...
    Windows Server x64"); its setup does not return there (seen on the windows-2022 CI runner). }
  GetWindowsVersionEx(V);
  if (V.ProductType <> VER_NT_WORKSTATION) and not ParamIsSet('ViGEmBusOnServer') then
  begin
    Log('ViGEmBus: not installed on Windows Server (its setup supports client Windows only); desks have no gamepads');
    Exit;
  end;
  if ViGEmBusPresent then
  begin
    if RegValueExists(HKLM, SplitPlayKey, ViGEmBusMarkerValue) then
      Log('ViGEmBus is present (installed by SplitPlay before)')
    else
      Log('ViGEmBus is already present and was not installed by SplitPlay: leaving it alone');
    Exit;
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('StatusViGEmBus');
  try
    ExtractTemporaryFile('{#ViGEmBusSetup}');
  except
    Log('ViGEmBus: could not extract the setup: ' + GetExceptionMessage);
    Exit;
  end;
  { The Uninstall entries before the setup runs, to tell which one it adds. }
  GetUninstallKeys(HKLM64, Before64);
  GetUninstallKeys(HKLM32, Before32);
  { Advanced Installer bootstrapper: /exenoui = no bootstrapper UI, /qn /norestart go to msiexec, /exelog = its log. }
  Code := RunHiddenWithTimeout(ExpandConstant('{tmp}\{#ViGEmBusSetup}'),
    '/exenoui /qn /norestart /exelog ' + AddQuotes(DataDir + '\logs\vigembus-setup.log'),
    ViGEmBusTimeoutSec, ViGEmBusHelperImage);
  if (Code = 3010) or (Code = 1641) then
    ViGEmBusRebootRequired := True;
  Installed := False;
  if Code = ExitTimedOut then
  begin
    { Killing the setup does not stop the Windows Installer service, which may still have committed the install. }
    if ViGEmBusPresent then
    begin
      Log('ViGEmBus setup timed out and was stopped, but Windows Installer finished installing the driver');
      Installed := True;
    end
    else
      Log('ViGEmBus setup timed out and was stopped; continuing without gamepads (not recorded as installed)');
  end
  else if (Code <> 0) and (Code <> 3010) and (Code <> 1641) then
    Log(Format('ViGEmBus setup failed (exit code %d); continuing without gamepads, see %s', [Code,
      DataDir + '\logs\vigembus-setup.log']))
  else if not ViGEmBusPresent then
    Log('ViGEmBus setup reported success but no ViGEmBus driver is present; continuing without gamepads')
  else
    Installed := True;
  if Installed then
  begin
    Product := FindViGEmBusProduct(Before64, Before32);
    if Product = '' then
    begin
      Log('ViGEmBus: no Uninstall entry found, recording the 1.22.0 product code');
      Product := ViGEmBusProductCode;
    end;
    RegWriteStringValue(HKLM, SplitPlayKey, ViGEmBusProductValue, Product);
    RegWriteDWordValue(HKLM, SplitPlayKey, ViGEmBusMarkerValue, 1);
    Log('ViGEmBus installed by SplitPlay (product ' + Product + ')');
  end;
end;

{ Removes ViGEmBus only if SplitPlay installed it. A failed removal is logged and keeps the record. }
procedure UninstallViGEmBus;
var
  Product: String;
  Code: Integer;
begin
  if not RegValueExists(HKLM, SplitPlayKey, ViGEmBusMarkerValue) then
  begin
    Log('ViGEmBus was not installed by SplitPlay: leaving it alone');
    Exit;
  end;
  if not RegQueryStringValue(HKLM, SplitPlayKey, ViGEmBusProductValue, Product) or (Product = '') then
    Product := ViGEmBusProductCode;
  Code := RunHiddenWithTimeout(SysTool('msiexec.exe'), '/x ' + Product + ' /qn /norestart /l*v ' +
    AddQuotes(DataDir + '\logs\vigembus-uninstall.log'), ViGEmBusTimeoutSec, ViGEmBusHelperImage);
  if (Code = 0) or (Code = 3010) or (Code = 1641) or (Code = 1605) then
  begin
    if Code = 1605 then
      Log('ViGEmBus ' + Product + ' is no longer installed')
    else
      Log('ViGEmBus removed');
    if (Code = 3010) or (Code = 1641) then
      UninstallRebootRequired := True;
    RegDeleteValue(HKLM, SplitPlayKey, ViGEmBusMarkerValue);
    RegDeleteValue(HKLM, SplitPlayKey, ViGEmBusProductValue);
    RegDeleteKeyIfEmpty(HKLM, SplitPlayKey);
  end
  else
    Log(Format('Removing ViGEmBus failed (msiexec exit code %d); it stays installed', [Code]));
end;

procedure InstallAndStartService;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('StatusService');
  if not ServiceExists then
  begin
    Code := RunHidden(ExpandConstant('{app}\service\splitplay.exe'), 'service install');
    if (Code <> 0) or not ServiceExists then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceInstallFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
      Exit;
    end;
  end;
  Code := RunHidden(SysTool('net.exe'), 'start ' + ServiceName);
  if Code <> 0 then
    SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceStartFailed'), [IntToStr(Code), DataDir + '\logs']),
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  SecureDataDir;
  AddFirewallRules;
  PrepareHost;
  InstallViGEmBus;
  InstallAndStartService;
  if WebView2Downloaded then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('StatusWebView2');
    RunHidden(ExpandConstant('{tmp}\' + WebView2File), '/silent /install');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Purge: Boolean;
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Files are still in place here. }
    RunHidden(SysTool('taskkill.exe'), '/F /IM SplitPlay.Tray.exe');
    if ServiceExists then
    begin
      { Stopping the service stops every desk (worker, then seat, which logs the desk session off). }
      RunHidden(ExpandConstant('{app}\service\splitplay.exe'), 'service uninstall');
      if ServiceExists then
      begin
        Log('CLI did not remove the service; falling back to net stop + sc delete');
        RunHidden(SysTool('net.exe'), 'stop ' + ServiceName);
        RunHidden(SysTool('sc.exe'), 'delete ' + ServiceName);
      end;
    end;
    { Restore the exact Remote Desktop settings recorded by "host prepare" (TermService's ServiceDll included). }
    Code := RunHidden(ExpandConstant('{app}\service\splitplay.exe'), 'host revert');
    if Code = ExitRebootRequired then
      UninstallRebootRequired := True;
    { After the service is gone, so that no worker holds a ViGEm pad any more. }
    UninstallViGEmBus;
    RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="' + FirewallRuleWeb + '"');
    { netsh cannot delete by wildcard. }
    RunHidden(SysTool('WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' +
      '"Get-NetFirewallRule -DisplayName ''' + FirewallRuleDeskPattern + ''' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(DataDir) then
    begin
      Purge := ParamIsSet('PurgeData');
      if not Purge and not UninstallSilent then
        Purge := MsgBox(FmtMessage(CustomMessage('KeepDataQuestion'), [DataDir]), mbConfirmation, MB_YESNO) = IDNO;
      if Purge then
      begin
        Log('Deleting ' + DataDir);
        DelTree(DataDir, True, True, True);
      end
      else
        Log('Keeping ' + DataDir);
    end;
  end;
end;
