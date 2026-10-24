# Builds ScrollShot: Native AOT for x64 + arm64, portable zips, and an .msixbundle.
#   ./build.ps1                                  # unsigned bundle
#   ./build.ps1 -Cert devcert.pfx                # signed with a dev cert (winapp cert generate)
#   ./build.ps1 -Version 1.2.0 -Cert release.pfx -CertPassword ***
# Requires: .NET 10 SDK, MSVC build tools for x64 and arm64 (Native AOT), winapp CLI.
param(
    [string]$Version = "1.0.0",
    [string]$Cert,
    [string]$CertPassword = "password"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$dist = Join-Path $PSScriptRoot "dist"
Remove-Item -Recurse -Force publish, $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist | Out-Null

foreach ($arch in "x64", "arm64") {
    dotnet publish -c Release -r "win-$arch" -o "publish/$arch" -p:Version=$Version -nologo
    if ($LASTEXITCODE) { throw "publish $arch failed" }
    Remove-Item "publish/$arch/*.pdb" -ErrorAction SilentlyContinue
    Compress-Archive "publish/$arch/ScrollShot.exe" "$dist/ScrollShot_${Version}_$arch.zip"
}

# Stamp the package version (MSIX needs 4 parts) into a temporary copy next to the real manifest.
$staged = "packaging/Package.build.appxmanifest"
$xml = Get-Content "packaging/Package.appxmanifest" -Raw
$xml = $xml -replace '(<Identity[^>]*Version=")[^"]+', "`${1}$Version.0"
Set-Content $staged $xml -NoNewline

$pack = @("package", "publish/x64", "publish/arm64", "--manifest", $staged, "--output", $dist)
if ($Cert) { $pack += @("--cert", $Cert, "--cert-password", $CertPassword) } else { $pack += "--no-sign" }
try {
    winapp @pack
    if ($LASTEXITCODE) { throw "winapp package failed" }
} finally { Remove-Item $staged -ErrorAction SilentlyContinue }

Get-ChildItem $dist | ForEach-Object { "{0,-45} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
