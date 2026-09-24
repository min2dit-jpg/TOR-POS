param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

# Authenticode signing for TOR executables and setups.
#
# An unsigned setup downloaded from a CI artifact has no reputation, so
# Chrome Safe Browsing and Windows SmartScreen may block it as harmful.
# Signing with a code-signing certificate is the supported fix.
#
# The certificate is supplied only through CI secrets:
#   WINDOWS_SIGNING_PFX_BASE64   base64 of the .pfx file
#   WINDOWS_SIGNING_PFX_PASSWORD password of the .pfx file
# Without them this script signs nothing and exits successfully, so builds
# without a certificate keep working exactly as before.

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:WINDOWS_SIGNING_PFX_BASE64)) {
    Write-Host "Code signing skipped: WINDOWS_SIGNING_PFX_BASE64 is not configured."
    exit 0
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) { throw 'Windows SDK signtool.exe not found.' }

$pfx = Join-Path ([System.IO.Path]::GetTempPath()) ("tor-signing-" + [guid]::NewGuid().ToString("N") + ".pfx")
try {
    [System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_SIGNING_PFX_BASE64))

    foreach ($pattern in $Path) {
        $files = @(Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue)
        if ($files.Count -eq 0) { throw "Nothing to sign for: $pattern" }

        foreach ($file in $files) {
            & $signtool.FullName sign /fd SHA256 /td SHA256 /tr "http://timestamp.digicert.com" /f $pfx /p $env:WINDOWS_SIGNING_PFX_PASSWORD $file.FullName
            if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($file.FullName)" }

            & $signtool.FullName verify /pa $file.FullName
            if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $($file.FullName)" }

            Write-Host "Signed: $($file.FullName)"
        }
    }
}
finally {
    Remove-Item -LiteralPath $pfx -Force -ErrorAction SilentlyContinue
}
