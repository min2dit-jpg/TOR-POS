$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Get-Location)
$corePath = Join-Path $repoRoot 'Desktop/src/TorPos.Core/CheckoutSafety.cs'
$compliancePath = Join-Path $repoRoot 'Desktop/src/TorPos.Infrastructure/FiscalComplianceServices.cs'
$acceptancePath = Join-Path $repoRoot 'verification/PRODUCTION-FISCAL-ACCEPTANCE.json'

$core = Get-Content -LiteralPath $corePath -Raw
$compliance = Get-Content -LiteralPath $compliancePath -Raw

$releaseEnabled = $core -match 'public\s+static\s+bool\s+Enabled\s*=>\s*true\s*;'
$buildEnabled = $compliance -match 'const\s+bool\s+fiscalReleaseBuild\s*=\s*true\s*;'

if (-not (Test-Path -LiteralPath $acceptancePath)) {
    if ($releaseEnabled -or $buildEnabled) {
        throw 'FISCAL RELEASE GATE FAILED: production gate enabled without verification/PRODUCTION-FISCAL-ACCEPTANCE.json'
    }

    Write-Host 'FISCAL RELEASE LOCKED: no production acceptance artifact; code gates remain false.'
    exit 0
}

try {
    $acceptance = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json
}
catch {
    throw "FISCAL RELEASE GATE FAILED: acceptance artifact is not valid JSON. $($_.Exception.Message)"
}

$requiredText = @(
    'tse_serial',
    'hardware_test_date',
    'dsfinvk_evidence',
    'receipt_evidence',
    'reviewer'
)
$missing = @()
foreach ($name in $requiredText) {
    $value = $acceptance.$name
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        $missing += $name
    }
}

if ($acceptance.hardware_tse_e2e -ne $true) { $missing += 'hardware_tse_e2e=true' }
if ($acceptance.dsfinvk_validated -ne $true) { $missing += 'dsfinvk_validated=true' }
if ($acceptance.receipt_validated -ne $true) { $missing += 'receipt_validated=true' }
if ($acceptance.independent_review -ne $true) { $missing += 'independent_review=true' }
if ($acceptance.approved_for_production -ne $true) { $missing += 'approved_for_production=true' }

if ($missing.Count -gt 0) {
    if ($releaseEnabled -or $buildEnabled) {
        throw ('FISCAL RELEASE GATE FAILED: incomplete acceptance artifact while production is enabled: ' + ($missing -join ', '))
    }

    Write-Host ('FISCAL RELEASE LOCKED: acceptance artifact exists but is incomplete: ' + ($missing -join ', '))
    exit 0
}

if (-not ($releaseEnabled -and $buildEnabled)) {
    Write-Host 'FISCAL ACCEPTANCE COMPLETE: evidence is present, but production gates are still intentionally false.'
    exit 0
}

Write-Host 'FISCAL RELEASE GATE OK: acceptance evidence is complete and both production gates are enabled.'
