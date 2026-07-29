; Inno Setup Compiler Script for MSSQL IntelliSense Extension & Debug App
#define AppName "MSSQL IntelliSense"
#ifndef AppVersion
  #define AppVersion "0.2.73"
#endif
#define AppPublisher "N2FlowJS / MSSQL IntelliSense"
#define AppURL "https://github.com/N2FlowJS/mssql-intelliSense"
#define AppExeName "MssqlIntelliSense.DebugApp.exe"

[Setup]
AppId={{5C1B2A10-6C2A-4B6E-9F93-7A4B6941C1A2}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={userappdata}\Microsoft\SSMS\22.0_5313988d\Extensions\MssqlIntelliSense.SsmsHost
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=MssqlIntelliSense-Setup-v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\src\MssqlIntelliSense.SsmsHost\bin\Release\net472\*"; DestDir: "{userappdata}\Microsoft\SSMS\22.0_5313988d\Extensions\MssqlIntelliSense.SsmsHost"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.vsix"
Source: "..\src\MssqlIntelliSense.SsmsHost\bin\Release\net472\*"; DestDir: "{userappdata}\Microsoft\SSMS\20.0_5313988d\Extensions\MssqlIntelliSense.SsmsHost"; Flags: ignoreversion recursesubdirs createallsubdirs uninsdeletethisrow; Excludes: "*.vsix"; Check: DirExists(ExpandConstant('{userappdata}\Microsoft\SSMS\20.0_5313988d'))
Source: "..\src\MssqlIntelliSense.DebugApp\bin\Release\net472\MssqlIntelliSense.DebugApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName} Debugger"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName} Debugger"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\install.ps1"" -NoKill"; Flags: runhidden waituntilterminated

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigChangedFile: String;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigChangedFile := ExpandConstant('{userappdata}\Microsoft\SSMS\22.0_5313988d\Extensions\extensions.configurationchanged');
    SaveStringToFile(ConfigChangedFile, '', False);
  end;
end;
