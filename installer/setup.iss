#ifndef PayloadDir
  #error PayloadDir must be specified
#endif
#ifndef OutputDir
  #error OutputDir must be specified
#endif
[Setup]
AppId={{D076FE88-E0A5-4EAD-98E8-A92B21D56D13}
AppName=CAD MCP для AutoCAD, Map 3D и Civil 3D 2025–2027 (предварительная версия)
AppVersion=0.4.0-preview
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
OutputBaseFilename=CAD-MCP-2025-2027-0.4.0-preview-Setup
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

[Icons]
Name: "{group}\Инструкция"; Filename: "{app}\INSTALL.txt"
Name: "{group}\Вход в Codex"; Filename: "{app}\Contents\Tools\Codex\bin\codex.exe"; Parameters: "login"
Name: "{group}\Остановить MCP broker"; Filename: "{app}\Contents\Host\CadMcp.Host.exe"; Parameters: "--stop-broker"
Name: "{group}\Репозиторий проекта"; Filename: "https://github.com/debug23win/autocad-mcp"
Name: "{group}\Удалить CAD MCP"; Filename: "{uninstallexe}"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name="acad.exe" OR Name="CadMcp.Host.exe" OR Name="CadMcp.Client.exe"');
    if Processes.Count > 0 then
      Result := 'Закройте AutoCAD и CAD MCP перед установкой. Установщик не закрывает чертежи автоматически.';
  except
    Result := 'Не удалось проверить запущенные процессы. Закройте AutoCAD и CAD MCP и повторите установку.';
  end;
end;

function InitializeUninstall(): Boolean;
var
  NeedsRestart: Boolean;
  Reason: String;
begin
  Reason := PrepareToInstall(NeedsRestart);
  Result := Reason = '';
  if not Result then MsgBox(Reason, mbError, MB_OK);
end;
