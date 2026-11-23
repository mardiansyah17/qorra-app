#define AppName "Qorra"
#define AppExe "DbClient.App.exe"
#define PublishDir "..\..\artifacts\publish"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + PublishDir + "\" + AppExe)
#if AppVersion == ""
  #error Publish first: DbClient.App.exe not found in artifacts\publish
#endif

[Setup]
AppId={{BB3FC259-7319-4FDD-8D71-39772778EDB4}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\..\artifacts\installer
OutputBaseFilename=Qorra-Setup-{#AppVersion}
SetupIconFile=..\..\src\DbClient.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
