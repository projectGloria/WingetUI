#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\dist\WingetUI"
#endif

#ifndef OutputDir
  #define OutputDir "..\installers"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "WingetUI-" + AppVersion + "-windows-x64"
#endif

[Setup]
AppId={{7E3F2C4B-9D8A-4B61-A5E2-1F4C8B90D3E7}
AppName=WingetUI
AppVersion={#AppVersion}
AppVerName=WingetUI {#AppVersion}
AppPublisher=projectGloria
AppPublisherURL=https://github.com/projectGloria/WingetUI
AppSupportURL=https://github.com/projectGloria/WingetUI/issues
AppUpdatesURL=https://github.com/projectGloria/WingetUI/releases
DefaultDirName={autopf}\WingetUI
DefaultGroupName=WingetUI
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\WingetUI.ico
UninstallDisplayIcon={app}\WingetUI.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WingetUI"; Filename: "{app}\WingetUI.exe"
Name: "{autodesktop}\WingetUI"; Filename: "{app}\WingetUI.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WingetUI.exe"; Description: "{cm:LaunchProgram,WingetUI}"; Flags: nowait postinstall skipifsilent
