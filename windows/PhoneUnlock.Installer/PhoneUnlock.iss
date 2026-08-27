#define MyAppName "Phone Unlock"
#define MyAppVersion "0.4.0-beta.22"
#define MyAppPublisher "blossom0948"
#define MyAppURL "https://github.com/blossom0948/windowslogin"
#define MyServiceName "PhoneUnlockService"
#define MyFirewallName "Phone Unlock Local Pairing"
#define MyVpnFirewallName "Phone Unlock Private VPN"

[Setup]
AppId={{755E2A38-C0C8-4696-8337-92B97FDB1D13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\PhoneUnlock
DefaultGroupName=Phone Unlock
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\..\artifacts
OutputBaseFilename=PhoneUnlock-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=Phone Unlock
UninstallDisplayIcon={app}\setup\PhoneUnlock.Setup.exe
VersionInfoVersion=0.4.0.0
VersionInfoProductName=Phone Unlock
VersionInfoProductVersion=0.4.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Phone Unlock Windows installer

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "..\..\artifacts\package\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\artifacts\package\setup\*"; DestDir: "{app}\setup"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "..\..\artifacts\package\agent\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace
Source: "..\..\artifacts\package\credential-provider\PhoneUnlock.CredentialProvider.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "Common.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Enable-CredentialProvider.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Disable-CredentialProvider.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall-PhoneUnlock.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "RECOVERY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Phone Unlock\Phone Unlock 설정"; Filename: "{app}\setup\PhoneUnlock.Setup.exe"; WorkingDir: "{app}\setup"
Name: "{autodesktop}\Phone Unlock 설정"; Filename: "{app}\setup\PhoneUnlock.Setup.exe"; WorkingDir: "{app}\setup"; Tasks: desktopicon
Name: "{userstartup}\Phone Unlock 자동 잠금"; Filename: "{app}\agent\PhoneUnlock.Agent.exe"; WorkingDir: "{app}\agent"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 Phone Unlock 설정 바로가기 만들기"; GroupDescription: "추가 바로가기:"

[Run]
Filename: "{app}\agent\PhoneUnlock.Agent.exe"; Description: "Phone Unlock 자동 잠금 감시 시작"; WorkingDir: "{app}\agent"; Flags: postinstall nowait skipifsilent
Filename: "{app}\setup\PhoneUnlock.Setup.exe"; Description: "Phone Unlock 설정 열기"; WorkingDir: "{app}\setup"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Disable-CredentialProvider.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "DisableProvider"
Filename: "{sys}\net.exe"; Parameters: "stop {#MyServiceName} /y"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyFirewallName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewall"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#MyVpnFirewallName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteVpnFirewall"

[Code]
function RunCommand(const FileName, Parameters, Description: String; AllowedSecondCode: Integer): Integer;
var
  ResultCode: Integer;
begin
  Log(Description + ': ' + FileName + ' ' + Parameters);
  if not Exec(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException(Description + ' 명령을 시작하지 못했습니다.');
  if (ResultCode <> 0) and (ResultCode <> AllowedSecondCode) then
    RaiseException(Description + '에 실패했습니다. 오류 코드: ' + IntToStr(ResultCode));
  Result := ResultCode;
end;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'query {#MyServiceName}', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
    RaiseException('Windows 서비스 상태를 확인하지 못했습니다.');
  Result := ResultCode = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExists() then
    RunCommand(ExpandConstant('{sys}\net.exe'), 'stop {#MyServiceName} /y',
      '기존 Phone Unlock 서비스 중지', 2);
end;

procedure ConfigureService();
var
  ServiceExe: String;
  Parameters: String;
begin
  ServiceExe := ExpandConstant('{app}\service\PhoneUnlock.Service.exe');
  if ServiceExists() then
    Parameters := 'config {#MyServiceName} binPath= "' + ServiceExe +
      '" start= auto obj= LocalSystem DisplayName= "Phone Unlock Service"'
  else
    Parameters := 'create {#MyServiceName} binPath= "' + ServiceExe +
      '" start= auto obj= LocalSystem DisplayName= "Phone Unlock Service"';

  RunCommand(ExpandConstant('{sys}\sc.exe'), Parameters, 'Phone Unlock 서비스 등록', -1);
  RunCommand(ExpandConstant('{sys}\sc.exe'),
    'description {#MyServiceName} "Receives pinned Android biometric approvals for Windows login."',
    '서비스 설명 설정', -1);
  RunCommand(ExpandConstant('{sys}\sc.exe'),
    'failure {#MyServiceName} reset= 86400 actions= restart/5000/restart/15000/none/0',
    '서비스 복구 설정', -1);

  RunCommand(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="{#MyFirewallName}"',
    '기존 방화벽 규칙 정리', 1);
  RunCommand(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="{#MyVpnFirewallName}"',
    '기존 VPN 방화벽 규칙 정리', 1);
  RunCommand(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="{#MyFirewallName}" dir=in action=allow protocol=TCP localport=48231 remoteip=LocalSubnet profile=any program="' + ServiceExe + '"',
    '로컬 네트워크 방화벽 설정', -1);
  RunCommand(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="{#MyVpnFirewallName}" dir=in action=allow protocol=TCP localport=48231 remoteip=100.64.0.0/10,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16 profile=any program="' + ServiceExe + '"',
    '사설 VPN 방화벽 설정', -1);
  RunCommand(ExpandConstant('{sys}\net.exe'), 'start {#MyServiceName}',
    'Phone Unlock 서비스 시작', 2);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    ConfigureService();
end;
