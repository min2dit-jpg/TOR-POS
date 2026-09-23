$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Get-Location)
$corePath = Join-Path $repoRoot 'Desktop/src/TorPos.Core/CheckoutSafety.cs'
$acceptancePath = Join-Path $repoRoot 'verification/PRODUCTION-FISCAL-ACCEPTANCE.json'

$core = Get-Content -LiteralPath $corePath -Raw

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

$cloudFlagName = 'CloudTseValidated'
$allFlagNames = @($commonFlagNames) + @($physicalFlags.Values) + @($cloudFlagName)

function Test-FlagEnabled([string]$name) {
    return $core -match ('public\s+const\s+bool\s+' + [regex]::Escape($name) + '\s*=\s*true\s*;')
}

function Require-Text($object, [string]$name, [System.Collections.Generic.List[string]]$missing) {
    $value = $object.$name
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        $missing.Add($name)
    }
}

$enabledFlags = @($allFlagNames | Where-Object { Test-FlagEnabled $_ })

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

if (Test-FlagEnabled 'DsfinvkValidated') {
    if ($acceptance.dsfinvk_validated -ne $true) { $missing.Add('dsfinvk_validated=true') }
    Require-Text $acceptance 'dsfinvk_evidence' $missing
}

if (Test-FlagEnabled 'KassenSichVReceiptValidated') {
    if ($acceptance.receipt_validated -ne $true) { $missing.Add('receipt_validated=true') }
    Require-Text $acceptance 'receipt_evidence' $missing
}

if (Test-FlagEnabled 'ParkedOrderTseValidated') {
    if ($acceptance.parked_order_tse_validated -ne $true) {
        $missing.Add('parked_order_tse_validated=true')
    }
}

if (Test-FlagEnabled 'PfandTaxValidated') {
    if ($acceptance.pfand_tax_validated -ne $true) {
        $missing.Add('pfand_tax_validated=true')
    }
}

if (Test-FlagEnabled 'IndependentFiscalReviewValidated') {
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

if (Test-FlagEnabled $cloudFlagName) {
    $cloud = $acceptance.cloud_tse_acceptance
    if ($null -eq $cloud -or $cloud.validated -ne $true) {
        $missing.Add('cloud_tse_acceptance.validated=true')
    }
    else {
        foreach ($field in @('provider','test_date','evidence')) {
            $value = $cloud.$field
            if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
                $missing.Add("cloud_tse_acceptance.$field")
            }
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
