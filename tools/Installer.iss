#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z to ISCC.
#endif
#ifndef RuntimeDir
  #define RuntimeDir "..\publish\win-x64"
#endif

[Setup]
AppId={{95AF077F-9586-420A-AC6A-70C70E35ADF4}
AppName=Legado.UI3
AppVersion={#AppVersion}
AppPublisher=ShenShenSshen
AppPublisherURL=https://github.com/ShenShenSshen/Legado.UI3
AppSupportURL=https://github.com/ShenShenSshen/Legado.UI3/issues
DefaultDirName={localappdata}\Programs\Legado.UI3
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\Assets\Legado.UI3.ico
UninstallDisplayIcon={app}\Legado.UI3.exe
OutputDir=..\publish
OutputBaseFilename=Legado.UI3-v{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#RuntimeDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Legado.UI3"; Filename: "{app}\Legado.UI3.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Legado.UI3"; Filename: "{app}\Legado.UI3.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Legado.UI3.exe"; Description: "Launch Legado.UI3"; Flags: nowait postinstall skipifsilent
