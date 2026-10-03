#ifndef ProbeRoot
  #error ProbeRoot must be specified
#endif
[Setup]
AppId=CADMCP-Installer-Process-Probe
AppName=CAD MCP installer process test
AppVersion=1
DefaultDirName={#ProbeRoot}\bundle
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
OutputDir={#ProbeRoot}
OutputBaseFilename=InstallerProcessProbe
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Files]
Source: "fixture.txt"; DestDir: "{app}"; Flags: ignoreversion

[Code]
#include "../../installer/processes.iss"
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := PrepareMcpFiles();
end;
