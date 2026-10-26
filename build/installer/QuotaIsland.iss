#ifndef AppVersion
  #error AppVersion must be supplied by package-installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by package-installer.ps1
#endif
#ifndef RepoRoot
  #error RepoRoot must be supplied by package-installer.ps1
#endif

[Setup]
AppId={{ADC74E3C-B465-4C0A-9D0D-3D428A2AE5F7}
AppName=点岛
AppVersion={#AppVersion}
AppPublisher=CodexQuotaTaskbar contributors
AppPublisherURL=https://github.com/dddeeprog/CodexQuotaTaskbar
AppSupportURL=https://github.com/dddeeprog/CodexQuotaTaskbar/issues
AppUpdatesURL=https://github.com/dddeeprog/CodexQuotaTaskbar/releases
DefaultDirName={localappdata}\Programs\CodexQuotaTaskbar
DefaultGroupName=点岛
UsePreviousGroup=no
DisableProgramGroupPage=no
AllowNoIcons=yes
DisableDirPage=no
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=Local\CodexQuotaTaskbar.Host
CloseApplications=no
RestartApplications=no
LicenseFile={#RepoRoot}\LICENSE
InfoBeforeFile=install-info.txt
SetupIconFile={#RepoRoot}\src\CodexQuotaTaskbar.Host\Assets\QuotaIslandIcon.ico
UninstallDisplayIcon={app}\CodexQuotaTaskbar.exe
OutputBaseFilename=CodexQuotaTaskbar-Setup-win-x64
VersionInfoVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
UninstallDisplayName=点岛

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PublishDir}\CodexQuotaTaskbar.exe"; DestDir: "{app}"
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\src\CodexQuotaTaskbar.Host\Assets\Licenses\ThirdPartyLicenses.zip"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#RepoRoot}\src\CodexQuotaTaskbar.Host\Assets\Licenses\Live2DCubismCore.ComponentTerms.zh-CN.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "{#RepoRoot}\src\CodexQuotaTaskbar.Host\Assets\Licenses\Live2DCubismCore.ProprietaryAgreement.en.txt"; DestDir: "{app}\Licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\点岛"; Filename: "{app}\CodexQuotaTaskbar.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 点岛"; Filename: "{uninstallexe}"
Name: "{autodesktop}\点岛"; Filename: "{app}\CodexQuotaTaskbar.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CodexQuotaTaskbar.exe"; Description: "启动 点岛"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  InstalledCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    InstalledCommand := '"' + ExpandConstant('{app}\CodexQuotaTaskbar.exe') + '"';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'CodexQuotaTaskbar', StartupCommand) then
      if CompareText(StartupCommand, InstalledCommand) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CodexQuotaTaskbar');
  end;
end;
