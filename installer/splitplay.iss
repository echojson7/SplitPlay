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
; Uninstaller parameters:
;   /PurgeData=1           also delete %ProgramData%\SplitPlay (default: keep it; interactive runs ask)

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
en.RdsNotice=IMPORTANT: WINDOWS LICENSING NOTICE%n%nSplitPlay runs several desktops at once on one PC. To do that it modifies Windows Remote Desktop Services so that several users can be signed in at the same time (concurrent sessions, through the TermWrap component).%n%nOn Windows client editions (Windows 11 Home, Pro, Enterprise and Education) concurrent remote sessions are not permitted, so this modification violates the Microsoft Software License Terms for your copy of Windows. You use it at your own risk; it may also be flagged by antivirus software and can stop working after Windows updates.%n%nThis preview build does not include TermWrap yet: the component is shown but disabled, and Remote Desktop Services are not changed by this installation.%n%nBy accepting, you confirm that you have read and understood this notice and the licence below.%n%n------------------------------------------------------------%n%n
ru.RdsNotice=ВАЖНО: ПРЕДУПРЕЖДЕНИЕ О ЛИЦЕНЗИИ WINDOWS%n%nSplitPlay запускает несколько рабочих столов одновременно на одном ПК. Для этого он изменяет службы удалённых рабочих столов Windows, чтобы несколько пользователей могли работать одновременно (параллельные сеансы, компонент TermWrap).%n%nКлиентские редакции Windows (Windows 11 Домашняя, Pro, Корпоративная и для образовательных учреждений) не разрешают параллельные удалённые сеансы, поэтому это изменение нарушает условия лицензии Microsoft на вашу копию Windows. Вы используете его на свой страх и риск; антивирусы могут на него реагировать, а обновления Windows могут его ломать.%n%nВ этой предварительной сборке TermWrap ещё нет: компонент показан, но отключён, и службы удалённых рабочих столов при этой установке не изменяются.%n%nПринимая условия, вы подтверждаете, что прочитали и поняли это предупреждение и лицензию ниже.%n%n------------------------------------------------------------%n%n
en.TypeFull=Full installation
ru.TypeFull=Полная установка
en.TypeCustom=Custom installation
ru.TypeCustom=Выборочная установка
en.CompCore=SplitPlay host (service, CLI, tray, web UI, native helpers, worker)
ru.CompCore=Хост SplitPlay (служба, CLI, трей, веб-интерфейс, нативные модули, воркер)
en.CompTermWrap=TermWrap concurrent sessions (not bundled yet)
ru.CompTermWrap=TermWrap: параллельные сеансы (пока не входит в сборку)
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

[Types]
Name: "full"; Description: "{cm:TypeFull}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

[Components]
Name: "core"; Description: "{cm:CompCore}"; Types: full custom; Flags: fixed
; Placeholder: TermWrap arrives in Stage 3. No type selects it and InitializeWizard disables it.
Name: "termwrap"; Description: "{cm:CompTermWrap}"

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
; Driver packages are shipped but not installed in Stage 2 (pnputil comes with the drivers in Stage 4).
Source: "{#StageDir}\drivers\*"; DestDir: "{app}\drivers"; Components: core; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Components: core; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; Components: core; Flags: ignoreversion

[INI]
Filename: "{group}\{cm:WebUiShortcut}.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://127.0.0.1:{#WebPort}/"

[Icons]
Name: "{group}\SplitPlay"; Filename: "{app}\tray\SplitPlay.Tray.exe"; WorkingDir: "{app}\tray"
Name: "{group}\{cm:UninstallProgram,SplitPlay}"; Filename: "{uninstallexe}"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SplitPlayTray"; ValueData: """{app}\tray\SplitPlay.Tray.exe"""; Flags: uninsdeletevalue; Tasks: trayautostart

[Run]
Filename: "{app}\tray\SplitPlay.Tray.exe"; Description: "{cm:LaunchTray}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallDelete]
Type: files; Name: "{group}\{cm:WebUiShortcut}.url"
Type: dirifempty; Name: "{app}"

[Code]
const
  ServiceName = 'SplitPlay';
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\SplitPlay';
  FirewallRuleWeb = 'SplitPlay Web UI';
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2File = 'MicrosoftEdgeWebview2Setup.exe';
  BuildWin11_22H2 = 22621;
  BuildWin11_24H2 = 26100;

var
  DownloadPage: TDownloadWizardPage;
  WebView2Downloaded: Boolean;

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
  { TermWrap placeholder (component index 1) cannot be selected yet. }
  WizardForm.ComponentsList.Checked[1] := False;
  WizardForm.ComponentsList.ItemEnabled[1] := False;
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
    Per-desk port-range rules come with multi-desk support. }
  WizardForm.StatusLabel.Caption := CustomMessage('StatusFirewall');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="' + FirewallRuleWeb + '"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall add rule name="' + FirewallRuleWeb + '"' +
    ' dir=in action=allow protocol=TCP localport={#WebPort} remoteip=localsubnet profile=private,domain enable=no' +
    ' program="' + ExpandConstant('{app}\service\SplitPlay.Service.exe') + '"' +
    ' description="SplitPlay web UI and API from the local network (enabled by the service when LAN access is on)"');
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
  if WizardIsComponentSelected('termwrap') then
    Log('TermWrap placeholder selected: nothing to install in this build');
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
begin
  if CurUninstallStep = usUninstall then
  begin
    { Files are still in place here. }
    RunHidden(SysTool('taskkill.exe'), '/F /IM SplitPlay.Tray.exe');
    if ServiceExists then
    begin
      RunHidden(ExpandConstant('{app}\service\splitplay.exe'), 'service uninstall');
      if ServiceExists then
      begin
        Log('CLI did not remove the service; falling back to net stop + sc delete');
        RunHidden(SysTool('net.exe'), 'stop ' + ServiceName);
        RunHidden(SysTool('sc.exe'), 'delete ' + ServiceName);
      end;
    end;
    RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="' + FirewallRuleWeb + '"');
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
