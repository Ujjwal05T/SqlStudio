; Built by CI: ISCC /DAppVersion=1.0.0 installer\SqlStudio.iss  (expects the publish output in ..\publish)
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F1B7C0E-3A52-4C1D-9B7E-5D2A8E4C9F10}
AppName=SqlStudio
AppVersion={#AppVersion}
AppPublisher=SqlStudio
DefaultDirName={autopf}\SqlStudio
DefaultGroupName=SqlStudio
UninstallDisplayIcon={app}\SqlStudio.exe
SetupIconFile=..\Assets\AppIcon.ico
OutputDir=..\installer-out
OutputBaseFilename=SqlStudio-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SqlStudio"; Filename: "{app}\SqlStudio.exe"
Name: "{autodesktop}\SqlStudio"; Filename: "{app}\SqlStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SqlStudio.exe"; Description: "Launch SqlStudio"; Flags: nowait postinstall skipifsilent
