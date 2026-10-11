# Builds the Microsoft Store package (.msix) for Restyle.
#
#   powershell -ExecutionPolicy Bypass -File store\build-msix.ps1 `
#       -IdentityName "12345YourName.Restyle" -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" `
#       -PublisherDisplayName "Your Name" -Version 1.0.0.0
#
# Copy the three identity values from Partner Center (Product management > Product identity).
# Upload the resulting .msix in Partner Center; the Store signs it for you.
#
# -TestSign additionally signs it with a self-signed certificate so you can install it on this PC
# before submitting (see store/STORE-SUBMISSION.md, "Test on your PC").
param(
    [string]$IdentityName = "Restyle.Dev",
    [string]$Publisher = "CN=Restyle Dev",
    [string]$PublisherDisplayName = "Restyle",
    [string]$Version = "1.0.0.0",
    [switch]$TestSign
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\Restyle.Store\Restyle.Store.csproj"
$out = Join-Path $root "out\store"
$layout = Join-Path $out "layout"

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Version must look like 1.2.3.0 (the Store requires the last number to be 0)." }

# Find dotnet (the per-user SDK install is used if dotnet isn't on PATH).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet; $env:DOTNET_ROOT = Split-Path $userDotnet }
if (-not $dotnet) { throw "The .NET 10 SDK wasn't found." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

Write-Host "1/4  Publishing the Store edition..."
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained -p:Version=$($Version -replace '\.0$','') -o $layout -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
Get-ChildItem $layout -Filter *.pdb | Remove-Item

Write-Host "2/4  Adding the manifest and logos..."
$assets = Join-Path $root "src\Restyle.Store\Package\Assets"
if (-not (Test-Path (Join-Path $assets "StoreLogo.png"))) { & (Join-Path $PSScriptRoot "make-assets.ps1") | Out-Null }
Copy-Item $assets (Join-Path $layout "Assets") -Recurse
$manifest = Get-Content (Join-Path $root "src\Restyle.Store\Package\AppxManifest.xml") -Raw
$manifest = $manifest.Replace('$IDENTITY_NAME$', $IdentityName).Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($Publisher)).
    Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('$VERSION$', $Version)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))

# Tools come from the Microsoft.Windows.SDK.BuildTools NuGet package (downloaded by the Store project's restore).
$tools = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools") -Recurse -Filter makeappx.exe |
    Where-Object FullName -match '\\x64\\' | Sort-Object FullName | Select-Object -Last 1
if (-not $tools) { throw "makeappx.exe not found. Run 'dotnet restore src\Restyle.Store' first." }
$makeappx = $tools.FullName
$signtool = Join-Path $tools.DirectoryName "signtool.exe"

Write-Host "3/4  Packing..."
$msix = Join-Path $out "Restyle_$($Version)_x64.msix"
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { & $makeappx pack /d $layout /p $msix /o; throw "makeappx failed (details above)." }

if ($TestSign) {
    Write-Host "4/4  Signing with a self-signed test certificate..."
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "Restyle test signing" `
            -CertStoreLocation Cert:\CurrentUser\My -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    Export-Certificate -Cert $cert -FilePath (Join-Path $out "Restyle-test.cer") | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed." }
} else {
    Write-Host "4/4  Not signed (the Store signs it when you submit)."
}

$size = (Get-Item $msix).Length / 1MB
Write-Host ("Done: {0}  ({1:N1} MB)" -f $msix, $size)
