#ifndef PayloadDir
  #error PayloadDir must be specified
#endif
#ifndef OutputDir
  #error OutputDir must be specified
#endif
[Setup]
AppId={{D076FE88-E0A5-4EAD-98E8-A92B21D56D13}
AppName={cm:ProductName}
AppVersion=0.11.7-preview
AppPublisher=CAD MCP contributors
AppPublisherURL=https://github.com/debug23win/autocad-mcp
DefaultDirName={userappdata}\Autodesk\ApplicationPlugins\CadMcp.AutoCAD2025.bundle
DisableDirPage=yes
DefaultGroupName=CAD MCP
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=CAD-MCP-2025-2027-0.11.7-preview-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=CAD MCP 2025–2027 Preview
LicenseFile={#PayloadDir}\LICENSE
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\Contents\Host\CadMcp.Host.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"; InfoBeforeFile: "before-install.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "before-install.en.txt"

[CustomMessages]
russian.ProductName=CAD MCP для AutoCAD, Map 3D и Civil 3D 2025–2027 (предварительная версия)
english.ProductName=CAD MCP for AutoCAD, Map 3D and Civil 3D 2025–2027 (Preview)
russian.ClaudeDesktop=Подключить CAD MCP к Claude Desktop (настройка mcpServers, остальные серверы сохраняются)
english.ClaudeDesktop=Connect CAD MCP to Claude Desktop (configure mcpServers, preserve other servers)
russian.ClaudeCode=Подключить CAD MCP к Claude Code для текущего пользователя (нужна команда claude)
english.ClaudeCode=Connect CAD MCP to Claude Code for the current user (requires claude command)
russian.ConnectingDesktop=Подключение к Claude Desktop...
english.ConnectingDesktop=Connecting to Claude Desktop...
russian.ConnectingCode=Подключение к Claude Code...
english.ConnectingCode=Connecting to Claude Code...
russian.Instructions=Инструкция
english.Instructions=Instructions
russian.SignIn=Вход в Codex
english.SignIn=Sign in to Codex
russian.StopBroker=Остановить MCP broker
english.StopBroker=Stop MCP broker
russian.Repository=Репозиторий проекта
english.Repository=Project repository
russian.Uninstall=Удалить CAD MCP
english.Uninstall=Uninstall CAD MCP

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "claudedesktop"; Description: "{cm:ClaudeDesktop}"; Flags: unchecked
Name: "claudecode"; Description: "{cm:ClaudeCode}"; Flags: unchecked

[Run]
Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--register-client claude-desktop"; StatusMsg: "{cm:ConnectingDesktop}"; Flags: runhidden waituntilterminated; Tasks: claudedesktop
Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--register-client claude-code"; StatusMsg: "{cm:ConnectingCode}"; Flags: runhidden waituntilterminated; Tasks: claudecode

[UninstallRun]
Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--unregister-client all"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterMcpClients"

[Icons]
Name: "{group}\{cm:Instructions}"; Filename: "{app}\INSTALL.txt"
Name: "{group}\{cm:SignIn}"; Filename: "{app}\Contents\Tools\Codex\bin\codex.exe"; Parameters: "login"
Name: "{group}\{cm:StopBroker}"; Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--stop-broker"
Name: "{group}\{cm:Repository}"; Filename: "https://github.com/debug23win/autocad-mcp"
Name: "{group}\{cm:Uninstall}"; Filename: "{uninstallexe}"

[Code]
#include "processes.iss"

function AutoCADRuntime(const Series: String): Integer;
var
  Root, Location, Key: String;
  Config: AnsiString;
  Names: TArrayOfString;
  I, Found: Integer;
begin
  Result := 0;
  Root := 'SOFTWARE\Autodesk\AutoCAD\' + Series;
  if not RegGetSubkeyNames(HKLM64, Root, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Key := Root + '\' + Names[I];
    if not RegQueryStringValue(HKLM64, Key, 'AcadLocation', Location) then Continue;
    if not LoadStringFromFile(AddBackslash(Location) + 'acdbmgd.runtimeconfig.json', Config) then
    begin
      Result := -2;
      Exit;
    end;
    if Pos('net10.0', Lowercase(Config)) > 0 then Found := 10
    else if Pos('net8.0', Lowercase(Config)) > 0 then Found := 8
    else
    begin
      Result := -2;
      Exit;
    end;
    if (Result <> 0) and (Result <> Found) then
    begin
      Result := -1;
      Exit;
    end;
    Result := Found;
  end;
end;

procedure SelectAdapter(const Series, ModuleName: String);
var
  Document, Groups, Group, Requirements, Entry: Variant;
  I: Integer;
  Path: String;
begin
  Path := ExpandConstant('{app}\PackageContents.xml');
  Document := CreateOleObject('Msxml2.DOMDocument.6.0');
  Document.async := False;
  if not Document.load(Path) then RaiseException(InstallerText('Не удалось прочитать PackageContents.xml', 'Could not read PackageContents.xml'));
  Groups := Document.selectNodes('/ApplicationPackage/Components');
  for I := 0 to Groups.length - 1 do
  begin
    Group := Groups.item[I];
    Requirements := Group.selectSingleNode('RuntimeRequirements');
    if VarIsNull(Requirements) then Continue;
    if Requirements.getAttribute('SeriesMin') <> Series then Continue;
    Entry := Group.selectSingleNode('ComponentEntry');
    if VarIsNull(Entry) then RaiseException(InstallerText('Нет адаптера для ', 'No adapter for ') + Series);
    Entry.setAttribute('ModuleName', ModuleName);
    Document.save(Path);
    Exit;
  end;
  RaiseException(InstallerText('Не найдена серия AutoCAD ', 'AutoCAD series not found: ') + Series);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LanguageFolder: String;
begin
  if CurStep <> ssPostInstall then Exit;
  LanguageFolder := ExpandConstant('{localappdata}\CadMcp');
  if not ForceDirectories(LanguageFolder) then
    RaiseException(InstallerText('Не удалось сохранить язык установки.', 'Could not save the installation language.'));
  if not SaveStringToFile(AddBackslash(LanguageFolder) + 'installation-language.txt', ActiveLanguage, False) then
    RaiseException(InstallerText('Не удалось сохранить язык установки.', 'Could not save the installation language.'));
  if AutoCADRuntime('R25.0') = 10 then
    SelectAdapter('R25.0', './Contents/Net10R250/CadMcp.AutoCAD2025Net10.dll');
  if AutoCADRuntime('R25.1') = 10 then
    SelectAdapter('R25.1', './Contents/Net10R251/CadMcp.AutoCAD2026Net10.dll');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AutoCADRuntime('R25.0') = -1 then
    Result := InstallerText('Продукты AutoCAD 2025 используют разные версии .NET. Установите согласованные обновления Autodesk.', 'AutoCAD 2025 products use different .NET versions. Install consistent Autodesk updates.');
  if AutoCADRuntime('R25.1') = -1 then
    Result := InstallerText('Продукты AutoCAD 2026 используют разные версии .NET. Установите согласованные обновления Autodesk.', 'AutoCAD 2026 products use different .NET versions. Install consistent Autodesk updates.');
  if (AutoCADRuntime('R25.0') = -2) or (AutoCADRuntime('R25.1') = -2) then
    Result := InstallerText('Не удалось определить среду .NET установленного AutoCAD. Проверьте файлы продукта или переустановите обновление Autodesk.', 'Could not determine the installed AutoCAD .NET runtime. Check product files or reinstall the Autodesk update.');
  if Result = '' then Result := PrepareMcpFiles();
end;

function InitializeUninstall(): Boolean;
var
  Reason: String;
begin
  Reason := PrepareMcpFiles();
  Result := Reason = '';
  if not Result then MsgBox(Reason, mbError, MB_OK);
end;
