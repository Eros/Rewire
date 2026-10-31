# Builds the Windows installer for Rewire with Velopack.
#
#   pwsh packaging/windows/package.ps1
#
# Without signing settings the installer is unsigned and Windows SmartScreen will warn about it.
# For a release, set one of:
#   REWIRE_SIGN_PARAMS             arguments for signtool, such as "/f cert.pfx /p ... /fd sha256 /tr http://timestamp.digicert.com /td sha256"
#   REWIRE_TRUSTED_SIGNING_FILE    an Azure Trusted Signing metadata.json file
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$out = Join-Path $root 'artifacts' 'windows'
$publish = Join-Path $out 'publish'

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Publishing Rewire $version for win-x64"
dotnet publish (Join-Path $root 'src' 'Rewire.App' 'Rewire.App.csproj') -c Release -r win-x64 --self-contained -o $publish -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

dotnet tool restore
$arguments = @(
    'pack',
    '--packId', 'Rewire',
    '--packTitle', 'Rewire',
    '--packAuthors', 'Rewire contributors',
    '--packVersion', $version,
    '--packDir', $publish,
    '--mainExe', 'Rewire.exe',
    '--icon', (Join-Path $root 'src' 'Rewire.App' 'Assets' 'rewire.ico'),
    '--outputDir', (Join-Path $out 'releases')
)

if ($env:REWIRE_SIGN_PARAMS) {
    $arguments += @('--signParams', $env:REWIRE_SIGN_PARAMS)
} elseif ($env:REWIRE_TRUSTED_SIGNING_FILE) {
    $arguments += @('--azureTrustedSignFile', $env:REWIRE_TRUSTED_SIGNING_FILE)
} else {
    Write-Warning 'No signing settings, so the installer will be unsigned.'
}

dotnet vpk @arguments
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Write-Host "Built the installer in $(Join-Path $out 'releases')"
