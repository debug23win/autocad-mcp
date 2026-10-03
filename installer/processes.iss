// Only processes whose executables belong to this installation may be stopped.
function InstalledMcpProcesses(const Services: Variant): Variant;
begin
  Result := Services.ExecQuery('SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process WHERE Name="CadMcp.Host.exe" OR Name="CadMcp.Client.exe" OR Name="codex.exe"');
end;

function BrokerPipeArgument(const Process: Variant): String;
var
  Command, Token: String;
  P, I: Integer;
begin
  Result := '';
  if VarIsNull(Process.CommandLine) or VarIsEmpty(Process.CommandLine) then Exit;
  Command := Process.CommandLine;
  if Pos(' --broker ', Command + ' ') = 0 then Exit;
  P := Pos(' --broker-pipe ', Command);
  if P = 0 then Exit;
  Token := Trim(Copy(Command, P + Length(' --broker-pipe '), Length(Command)));
  if Copy(Token, 1, 1) = '"' then
  begin
    Token := Copy(Token, 2, Length(Token));
    P := Pos('"', Token);
    if P = 0 then Exit;
    Token := Copy(Token, 1, P - 1);
  end
  else
  begin
    P := Pos(' ', Token);
    if P > 0 then Token := Copy(Token, 1, P - 1);
  end;
  if (Length(Token) = 0) or (Length(Token) > 200) then Exit;
  for I := 1 to Length(Token) do
    if Pos(Token[I], 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.') = 0 then Exit;
  Result := ' --broker-pipe ' + Token;
end;

function BelongsToInstallation(const Process: Variant): Boolean;
var
  Path, Root: String;
begin
  Result := False;
  if VarIsNull(Process.ExecutablePath) or VarIsEmpty(Process.ExecutablePath) then Exit;
  Path := Lowercase(String(Process.ExecutablePath));
  Root := Lowercase(AddBackslash(ExpandConstant('{app}')));
  Result := Pos(Root, Path) = 1;
end;

function RunningInstalledMcp(const Services: Variant): String;
var
  Processes, Process: Variant;
  I: Integer;
begin
  Result := '';
  Processes := InstalledMcpProcesses(Services);
  for I := 0 to Processes.Count - 1 do
  begin
    Process := Processes.ItemIndex(I);
    if not BelongsToInstallation(Process) then Continue;
    if Result <> '' then Result := Result + ', ';
    Result := Result + String(Process.Name) + ' (PID ' + IntToStr(Integer(Process.ProcessId)) + ')';
  end;
end;

function PrepareMcpFiles(): String;
var
  Locator, Services, Processes, Process: Variant;
  I, Pass, ExitCode, Terminated: Integer;
  HostPath, Remaining, PipeArgument: String;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name="acad.exe" OR Name="accoreconsole.exe"');
    if Processes.Count > 0 then
    begin
      Result := 'Закройте AutoCAD, Map 3D, Civil 3D и AutoCAD Core Console перед установкой. CAD MCP будет остановлен установщиком. Чертежи автоматически не закрываются.';
      Exit;
    end;
    Remaining := RunningInstalledMcp(Services);
    if Remaining = '' then Exit;
    Log('Stopping installed CAD MCP processes: ' + Remaining);
    HostPath := ExpandConstant('{app}\Contents\Host\CadMcp.Host.exe');
    // Stop the client before other processes, so it cannot restart the broker.
    for Pass := 0 to 1 do
    begin
      Processes := InstalledMcpProcesses(Services);
      for I := 0 to Processes.Count - 1 do
      begin
        Process := Processes.ItemIndex(I);
        if not BelongsToInstallation(Process) then Continue;
        if (Lowercase(String(Process.Name)) = 'cadmcp.client.exe') <> (Pass = 0) then Continue;
        if Lowercase(String(Process.Name)) = 'cadmcp.host.exe' then
        begin
          PipeArgument := BrokerPipeArgument(Process);
          if (PipeArgument <> '') and FileExists(HostPath) then
          begin
            if not Exec(HostPath, '--stop-broker' + PipeArgument, ExtractFileDir(HostPath), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
              Log('Broker stop helper did not start; falling back to installed process termination.')
            else Sleep(400);
          end;
        end;
        try
          Terminated := Process.Terminate(0);
          Log('Stopped ' + String(Process.Name) + ' PID ' + IntToStr(Integer(Process.ProcessId)) + ', result ' + IntToStr(Terminated));
        except
          Log('Process exited while stopping CAD MCP: ' + GetExceptionMessage);
        end;
      end;
    end;
    for I := 0 to 19 do
    begin
      Remaining := RunningInstalledMcp(Services);
      if Remaining = '' then Exit;
      Sleep(250);
    end;
    Result := 'Не удалось остановить CAD MCP: ' + Remaining + '. Закройте эти процессы и повторите установку.';
  except
    Result := 'Не удалось проверить или остановить процессы CAD MCP. Закройте AutoCAD и CAD MCP и повторите установку. Подробности: ' + GetExceptionMessage;
  end;
end;
