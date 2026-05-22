[Setup]
AppName=SwiftShare
AppVersion=1.0
DefaultDirName={autopf}\SwiftShare
DefaultGroupName=SwiftShare
UninstallDisplayIcon={app}\SwiftShare.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.
OutputBaseFilename=SwiftShareSetup
; The following line fixes the "mysterious blue curtain on the left" by using the modern installer style
WizardStyle=modern
; The following line fixes the "different icon from main app" by applying the app's icon to the installer
SetupIconFile=icon.ico
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "SwiftShare.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "SwiftShare.manifest"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\SwiftShare"; Filename: "{app}\SwiftShare.exe"
Name: "{autodesktop}\SwiftShare"; Filename: "{app}\SwiftShare.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
Filename: "{app}\SwiftShare.exe"; Description: "{cm:LaunchProgram,SwiftShare}"; Flags: nowait postinstall skipifsilent
