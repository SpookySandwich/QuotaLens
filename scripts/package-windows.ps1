[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [ValidateSet("x64", "x86", "ARM64")]
    [string] $Platform = "x64",

    [string] $Version = "1.0.0",

    [switch] $SkipInstaller
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$ProjectPath = Join-Path $RepoRoot "winui\QuotaLens.csproj"
$ArtifactRoot = Join-Path $RepoRoot "artifacts"
$PublishDir = Join-Path $ArtifactRoot "publish\QuotaLens-win-$Platform"
$DistDir = Join-Path $ArtifactRoot "dist"
$RuntimeIdentifier = switch ($Platform) {
    "ARM64" { "win-arm64" }
    "x86" { "win-x86" }
    default { "win-x64" }
}
$InstallerArchitecture = switch ($Platform) {
    "ARM64" { "arm64" }
    "x86" { "x86" }
    default { "x64compatible" }
}

if (Test-Path $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $PublishDir, $DistDir | Out-Null

dotnet publish $ProjectPath `
    -c $Configuration `
    -p:Platform=$Platform `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    -p:PublishDir="$PublishDir\"

$ZipPath = Join-Path $DistDir "QuotaLens-portable-$Version-win-$Platform.zip"
if (Test-Path $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath
Write-Host "Portable package: $ZipPath"

if ($SkipInstaller) {
    return
}

$IsccCandidates = @(@(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) })

if ($IsccCandidates.Count -eq 0) {
    throw "Inno Setup 6 (ISCC.exe) was not found. Install it or rerun with -SkipInstaller."
}

$InstallerScript = Join-Path $RepoRoot "installer\QuotaLens.iss"
& $IsccCandidates[0] `
    "/DSourceDir=$PublishDir" `
    "/DOutputDir=$DistDir" `
    "/DAppVersion=$Version" `
    "/DPlatform=$Platform" `
    "/DInstallerArchitecture=$InstallerArchitecture" `
    $InstallerScript

$InstallerPath = Join-Path $DistDir "QuotaLens-Setup-$Version-win-$Platform.exe"
Write-Host "Installer: $InstallerPath"
