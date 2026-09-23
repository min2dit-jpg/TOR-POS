$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Get-Location)
$corePath = Join-Path $repoRoot 'Desktop/src/TorPos.Core/CheckoutSafety.cs'
$cloudPath = Join-Path $repoRoot 'Desktop/src/TorPos.Core/CloudTse.cs'
$acceptancePath = Join-Path $repoRoot 'verification/PRODUCTION-FISCAL-ACCEPTANCE.json'

$core = Get-Content -LiteralPath $corePath -Raw
$cloudCore = Get-Content -LiteralPath $cloudPath -Raw

$commonFlagNames = @(
    'DsfinvkValidated',
    'KassenSichVReceiptValidated',
    'ParkedOrderTseValidated',
    'PfandTaxValidated',
    'IndependentFiscalReviewValidated'
)

$physicalFlags = [ordered]@{
    '1'   = 'PhysicalTseGeneration1E2EValidated'
    '1.1' = 'PhysicalTseGeneration11E2EValidated'
    '2'   = 'PhysicalTseGeneration2E2EValidated'
}

$cloudFlags = [ordered]@{
    'FISKALTRUST'      = 'FiskaltrustValidated'
    'FISKALY'          = 'FiskalyValidated'
    'DEUTSCHE_FISKAL'  = 'DeutscheFiskalValidated'
}

function Test-FlagEnabled([string]$source, [string]$name) {
    return $source -match ('public\s+const\s+bool\s+' + [regex]::Escape($name) + '\s*=\s*true\s*;')
}

function Test-CoreFlagEnabled([string]$name) {
    return Test-FlagEnabled $core $name
}

function Test-CloudFlagEnabled([string]$name) {
    return Test-FlagEnabled $cloudCore $name
}

$enabledFlags = @(
    @($commonFlagNames) + @($physicalFlags.Values) |
    Where-Object { Test-CoreFlagEnabled $_ }
)

$enabledCloudFlags = @(
    $cloudFlags.Values |
    Where-Object { Test-CloudFlagEnabled $_ }
)

$enabledFlags = @($enabledFlags) + @($enabledCloudFlags)

function Require-Text($object, [string]$name, [System.Collections.Generic.List[string]]$missing) {
    $value = $object.$name
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        $missing.Add($name)
    }
}

if (-not (Test-Path -LiteralPath $acceptancePath)) {
    if ($enabledFlags.Count -gt 0) {
        throw ('FISCAL RELEASE GATE FAILED: acceptance artifact missing but these flags are true: ' + ($enabledFlags -join ', '))
    }

    Write-Host 'FISCAL RELEASE LOCKED: no production acceptance artifact; all qualification/provider flags remain false.'
    exit 0
}

try {
    $acceptance = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json
}
catch {
    throw "FISCAL RELEASE GATE FAILED: acceptance artifact is not valid JSON. $($_.Exception.Message)"
}

$missing = [System.Collections.Generic.List[string]]::new()

if ($enabledFlags.Count -gt 0) {
    Require-Text $acceptance 'reviewer' $missing
    if ($acceptance.approved_for_production -ne $true) {
        $missing.Add('approved_for_production=true')
    }
}

if (Test-CoreFlagEnabled 'DsfinvkValidated') {
    if ($acceptance.dsfinvk_validated -ne $true) { $missing.Add('dsfinvk_validated=true') }
    Require-Text $acceptance 'dsfinvk_evidence' $missing
}

if (Test-CoreFlagEnabled 'KassenSichVReceiptValidated') {
    if ($acceptance.receipt_validated -ne $true) { $missing.Add('receipt_validated=true') }
    Require-Text $acceptance 'receipt_evidence' $missing
}

if (Test-CoreFlagEnabled 'ParkedOrderTseValidated') {
    if ($acceptance.parked_order_tse_validated -ne $true) {
        $missing.Add('parked_order_tse_validated=true')
    }
}

if (Test-CoreFlagEnabled 'PfandTaxValidated') {
    if ($acceptance.pfand_tax_validated -ne $true) {
        $missing.Add('pfand_tax_validated=true')
    }
}

if (Test-CoreFlagEnabled 'IndependentFiscalReviewValidated') {
    if ($acceptance.independent_review -ne $true) {
        $missing.Add('independent_review=true')
    }
}

$physicalAcceptances = @($acceptance.physical_tse_acceptances)
foreach ($entry in $physicalFlags.GetEnumerator()) {
    if (-not (Test-FlagEnabled $entry.Value)) {
        continue
    }

    $generation = [string]$entry.Key
    $match = @(
        $physicalAcceptances |
        Where-Object {
            ([string]$_.generation).Trim() -eq $generation -and
            $_.validated -eq $true
        }
    ) | Select-Object -First 1

    if ($null -eq $match) {
        $missing.Add("physical_tse_acceptances[$generation].validated=true")
        continue
    }

    foreach ($field in @('tse_serial','hardware_test_date','evidence')) {
        $value = $match.$field
        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
            $missing.Add("physical_tse_acceptances[$generation].$field")
        }
    }
}

$cloudAcceptances = @($acceptance.cloud_tse_acceptances)
if ($cloudAcceptances.Count -eq 0 -and $null -ne $acceptance.cloud_tse_acceptance) {
    # Backward-compatible read for an older single-entry acceptance file.
    $cloudAcceptances = @($acceptance.cloud_tse_acceptance)
}

foreach ($entry in $cloudFlags.GetEnumerator()) {
    if (-not (Test-CloudFlagEnabled $entry.Value)) {
        continue
    }

    $provider = [string]$entry.Key
    $match = @(
        $cloudAcceptances |
        Where-Object {
            ([string]$_.provider).Trim().ToUpperInvariant() -eq $provider -and
            $_.validated -eq $true
        }
    ) | Select-Object -First 1

    if ($null -eq $match) {
        $missing.Add("cloud_tse_acceptances[$provider].validated=true")
        continue
    }

    foreach ($field in @('test_date','evidence')) {
        $value = $match.$field
        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
            $missing.Add("cloud_tse_acceptances[$provider].$field")
        }
    }
}

if ($missing.Count -gt 0) {
    if ($enabledFlags.Count -gt 0) {
        throw ('FISCAL RELEASE GATE FAILED: enabled flags lack matching acceptance evidence. Missing: ' + ($missing -join ', '))
    }

    Write-Host ('FISCAL RELEASE LOCKED: acceptance artifact exists but no release flags are enabled.')
    exit 0
}

if ($enabledFlags.Count -eq 0) {
    Write-Host 'FISCAL RELEASE LOCKED: acceptance artifact may exist, but all release flags remain false.'
    exit 0
}

Write-Host ('FISCAL RELEASE GATE OK: every enabled flag is backed by matching provider/generation evidence. Enabled flags: ' + ($enabledFlags -join ', '))
