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

[Code]
const
  DotNetKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App';

function HasDotNet10: Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM, DotNetKey, Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
      if Copy(Versions[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not HasDotNet10 then
    if MsgBox('SqlStudio needs the .NET 10 Runtime (x64), which was not found on this PC.' + #13#10 + #13#10 +
              'Open the download page now? Run this setup again after installing it.',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
      Result := False;
    end
    else
      Result := False;
end;
