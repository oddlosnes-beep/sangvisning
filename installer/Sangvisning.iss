; Bygges av GitHub Actions. AppVersion sendes inn med /DAppVersion=...
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6E0A3C1B-4B1E-4C0D-9F47-5A9D1C2F7A10}
AppName=Sangvisning
AppVersion={#AppVersion}
AppPublisher=Sangvisning
DefaultDirName={localappdata}\Programs\Sangvisning
DefaultGroupName=Sangvisning
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Sangvisning-Setup
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\Sangvisning.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
DisableProgramGroupPage=yes
CloseApplications=yes
CloseApplicationsFilter=Sangvisning.exe

[Languages]
Name: "norwegian"; MessagesFile: "compiler:Languages\Norwegian.isl"

[Tasks]
Name: "desktopicon"; Description: "Lag snarvei på skrivebordet"; Flags: checkedonce

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Sangvisning"; Filename: "{app}\Sangvisning.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Sangvisning"; Filename: "{app}\Sangvisning.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Sangvisning.exe"; WorkingDir: "{app}"; Description: "Start Sangvisning nå"; Flags: nowait postinstall skipifsilent
