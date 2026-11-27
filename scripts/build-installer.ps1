<#
.SYNOPSIS
  Builds the Windows installer locally, same steps as .github/workflows/release.yml and .gitlab-ci.yml.

.EXAMPLE
  Run from the folder that contains SqlStudio.csproj (src\SqlStudio):
    .\scripts\build-installer.ps1 -BackendUrl https://api.example.com
    .\scripts\build-installer.ps1 -BackendUrl https://api.example.com -Version 1.1.0

  Output: installer-out\SqlStudio-Setup-<Version>.exe  and  SqlStudio-win-x64-portable.zip
  Needs: .NET 10 SDK. Inno Setup 6 is installed automatically (winget or choco) if missing.
#>
param(
  [Parameter(Mandatory)][string]$BackendUrl,
  [string]$Version = '1.1.0'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path .\SqlStudio.csproj)) { throw "Run this from the folder containing SqlStudio.csproj (src\SqlStudio)." }
if ($BackendUrl -notmatch '^https://\S+$') { throw "-BackendUrl must start with https://" }
if (-not ((dotnet --list-sdks 2>$null) -match '^10\.')) { throw ".NET 10 SDK not found. Install it from https://dot.net/download" }

Remove-Item publish, installer-out, SqlStudio-win-x64-portable.zip -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish SqlStudio.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  "-p:CompanyBackendUrl=$BackendUrl" -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  if (Get-Command winget -ErrorAction SilentlyContinue) { winget install -e --id JRSoftware.InnoSetup --accept-source-agreements --accept-package-agreements }
  elseif (Get-Command choco -ErrorAction SilentlyContinue) { choco install innosetup -y --no-progress }
  else { throw "Install Inno Setup 6 from https://jrsoftware.org/isdl.php and re-run." }
  $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $iscc) { throw "Inno Setup installed but ISCC.exe not found." }
}

& $iscc "/DAppVersion=$Version" installer\SqlStudio.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Compress-Archive -Path publish\* -DestinationPath SqlStudio-win-x64-portable.zip
Write-Host "`nDone: $(Resolve-Path installer-out\SqlStudio-Setup-$Version.exe)"
