#define MyAppName "Gem"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "kAdimiron"
#define MyAppURL "https://github.com/kAdimiron/gem"
#define MyAppExeName "Gem.exe"

[Setup]
AppId={{8B6E4A72-3D9F-4C5E-A2E1-9F6A8C14D201}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\installer
OutputBaseFilename=GemSetup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\Gem\Assets\Gem.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
english.AssocTorrent=Associate .torrent files with Gem
english.AssocMagnet=Register magnet: links with Gem
english.RemoveAppDataPrompt=Do you also want to delete Gem settings, torrent list, and traffic history (%1)?%n%nDownloaded files on your disk will NOT be deleted.
russian.AssocTorrent=Ассоциировать файлы .torrent с Gem
russian.AssocMagnet=Открывать magnet-ссылки в Gem
russian.RemoveAppDataPrompt=Удалить также настройки Gem, список торрентов и историю трафика (%1)?%n%nСкачанные вами файлы на диске НЕ будут удалены.
ukrainian.AssocTorrent=Асоціювати файли .torrent з Gem
ukrainian.AssocMagnet=Відкривати magnet-посилання в Gem
ukrainian.RemoveAppDataPrompt=Видалити також налаштування Gem, список торрентів та історію трафіку (%1)?%n%nЗавантажені вами файли на диску НЕ будуть видалені.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_torrent"; Description: "{cm:AssocTorrent}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc_magnet"; Description: "{cm:AssocMagnet}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Registered application capabilities (Windows 10/11 Default Apps integration)
Root: HKA; Subkey: "Software\{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Gem BitTorrent Client"
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".torrent"; ValueData: "Gem.Torrent"; Tasks: assoc_torrent
Root: HKA; Subkey: "Software\{#MyAppName}\Capabilities\URLAssociations"; ValueType: string; ValueName: "magnet"; ValueData: "Gem.Magnet"; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\{#MyAppName}\Capabilities"; Flags: uninsdeletevalue

; .torrent file association
Root: HKA; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; ValueData: "Gem.Torrent"; Flags: uninsdeletevalue; Tasks: assoc_torrent
Root: HKA; Subkey: "Software\Classes\.torrent\OpenWithProgids"; ValueType: string; ValueName: "Gem.Torrent"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_torrent
Root: HKA; Subkey: "Software\Classes\Gem.Torrent"; ValueType: string; ValueName: ""; ValueData: "Torrent File"; Flags: uninsdeletekey; Tasks: assoc_torrent
Root: HKA; Subkey: "Software\Classes\Gem.Torrent"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Torrent File"; Tasks: assoc_torrent
Root: HKA; Subkey: "Software\Classes\Gem.Torrent\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: assoc_torrent
Root: HKA; Subkey: "Software\Classes\Gem.Torrent\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_torrent

; magnet: URI protocol association
Root: HKA; Subkey: "Software\Classes\Gem.Magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Protocol"; Flags: uninsdeletekey; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\Classes\Gem.Magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\Classes\Gem.Magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\Classes\Gem.Magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc_magnet

Root: HKA; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Protocol"; Flags: uninsdeletevalue; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Flags: uninsdeletekey; Tasks: assoc_magnet
Root: HKA; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey; Tasks: assoc_magnet

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if UninstallSilent() then
      Exit;

    DataDir := ExpandConstant('{localappdata}\{#MyAppName}');
    if DirExists(DataDir) then
    begin
      if MsgBox(FmtMessage(CustomMessage('RemoveAppDataPrompt'), [DataDir]),
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
