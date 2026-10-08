#ifndef PayloadDir
  #error PayloadDir must be specified
#endif
#ifndef OutputDir
  #error OutputDir must be specified
#endif
[Setup]
AppId={{D076FE88-E0A5-4EAD-98E8-A92B21D56D13}
AppName=CAD MCP для AutoCAD, Map 3D и Civil 3D 2025–2027 (предварительная версия)
AppVersion=0.11.1-preview
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
OutputBaseFilename=CAD-MCP-2025-2027-0.11.1-preview-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=CAD MCP 2025–2027 Preview
LicenseFile={#PayloadDir}\LICENSE
InfoBeforeFile=before-install.txt
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\Contents\Host\CadMcp.Host.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "claudedesktop"; Description: "Подключить CAD MCP к Claude Desktop (настройка mcpServers, остальные серверы сохраняются)"; Flags: unchecked
Name: "claudecode"; Description: "Подключить CAD MCP к Claude Code для текущего пользователя (нужна команда claude)"; Flags: unchecked

[Run]
Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--register-client claude-desktop"; StatusMsg: "Подключение к Claude Desktop..."; Flags: runhidden waituntilterminated; Tasks: claudedesktop
Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--register-client claude-code"; StatusMsg: "Подключение к Claude Code..."; Flags: runhidden waituntilterminated; Tasks: claudecode

[UninstallRun]
Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--unregister-client all"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterMcpClients"

[Icons]
Name: "{group}\Инструкция"; Filename: "{app}\INSTALL.txt"
Name: "{group}\Вход в Codex"; Filename: "{app}\Contents\Tools\Codex\bin\codex.exe"; Parameters: "login"
Name: "{group}\Остановить MCP broker"; Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--stop-broker"
Name: "{group}\Репозиторий проекта"; Filename: "https://github.com/debug23win/autocad-mcp"
Name: "{group}\Удалить CAD MCP"; Filename: "{uninstallexe}"

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
  if not Document.load(Path) then RaiseException('Не удалось прочитать PackageContents.xml');
  Groups := Document.selectNodes('/ApplicationPackage/Components');
  for I := 0 to Groups.length - 1 do
  begin
    Group := Groups.item[I];
    Requirements := Group.selectSingleNode('RuntimeRequirements');
    if VarIsNull(Requirements) then Continue;
    if Requirements.getAttribute('SeriesMin') <> Series then Continue;
    Entry := Group.selectSingleNode('ComponentEntry');
    if VarIsNull(Entry) then RaiseException('Нет адаптера для ' + Series);
    Entry.setAttribute('ModuleName', ModuleName);
    Document.save(Path);
    Exit;
  end;
  RaiseException('Не найдена серия AutoCAD ' + Series);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  if AutoCADRuntime('R25.0') = 10 then
    SelectAdapter('R25.0', './Contents/Net10R250/CadMcp.AutoCAD2025Net10.dll');
  if AutoCADRuntime('R25.1') = 10 then
    SelectAdapter('R25.1', './Contents/Net10R251/CadMcp.AutoCAD2026Net10.dll');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AutoCADRuntime('R25.0') = -1 then
    Result := 'Продукты AutoCAD 2025 используют разные версии .NET. Установите согласованные обновления Autodesk.';
  if AutoCADRuntime('R25.1') = -1 then
    Result := 'Продукты AutoCAD 2026 используют разные версии .NET. Установите согласованные обновления Autodesk.';
  if (AutoCADRuntime('R25.0') = -2) or (AutoCADRuntime('R25.1') = -2) then
    Result := 'Не удалось определить среду .NET установленного AutoCAD. Проверьте файлы продукта или переустановите обновление Autodesk.';
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
