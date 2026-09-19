$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Get-Location)
$corePath = Join-Path $repoRoot 'Desktop/src/TorPos.Core/CheckoutSafety.cs'
$acceptancePath = Join-Path $repoRoot 'verification/PRODUCTION-FISCAL-ACCEPTANCE.json'

$core = Get-Content -LiteralPath $corePath -Raw

$flagNames = @(
    'DsfinvkValidated',
    'KassenSichVReceiptValidated',
    'ParkedOrderTseValidated',
    'PfandTaxValidated',
    'PhysicalTseE2EValidated',
    'IndependentFiscalReviewValidated'
)

$enabledFlags = @()
foreach ($name in $flagNames) {
    if ($core -match ('public\s+const\s+bool\s+' + [regex]::Escape($name) + '\s*=\s*true\s*;')) {
        $enabledFlags += $name
    }
}

if (-not (Test-Path -LiteralPath $acceptancePath)) {
    if ($enabledFlags.Count -gt 0) {
        throw ('FISCAL RELEASE GATE FAILED: acceptance artifact missing but these flags are true: ' + ($enabledFlags -join ', '))
    }

    Write-Host 'FISCAL RELEASE LOCKED: no production acceptance artifact; all qualification flags remain false.'
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
if ($acceptance.parked_order_tse_validated -ne $true) { $missing += 'parked_order_tse_validated=true' }
if ($acceptance.pfand_tax_validated -ne $true) { $missing += 'pfand_tax_validated=true' }
if ($acceptance.independent_review -ne $true) { $missing += 'independent_review=true' }
if ($acceptance.approved_for_production -ne $true) { $missing += 'approved_for_production=true' }

if ($missing.Count -gt 0) {
    if ($enabledFlags.Count -gt 0) {
        throw ('FISCAL RELEASE GATE FAILED: incomplete acceptance artifact while qualification flags are enabled. Missing: ' + ($missing -join ', '))
    }

    Write-Host ('FISCAL RELEASE LOCKED: acceptance artifact exists but is incomplete: ' + ($missing -join ', '))
    exit 0
}

if ($enabledFlags.Count -ne $flagNames.Count) {
    Write-Host ('FISCAL ACCEPTANCE COMPLETE: evidence is complete, but production remains locked. Enabled flags: ' + ($enabledFlags -join ', '))
    exit 0
}

Write-Host 'FISCAL RELEASE GATE OK: complete acceptance evidence and all six production qualifications are enabled.'
