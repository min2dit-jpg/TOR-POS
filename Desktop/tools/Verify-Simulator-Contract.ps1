$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$contractPath = Join-Path $root 'Shared/simulator-contract.json'
$appRoot = Join-Path $root 'Desktop/src/TorPos.App'
$mainWindowPath = Join-Path $appRoot 'MainWindow.axaml'

if (-not (Test-Path $contractPath)) {
    throw "Simulator contract missing: $contractPath"
}

$contract = Get-Content $contractPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($contract.schemaVersion -ne 1) {
    throw "Unsupported simulator contract schemaVersion: $($contract.schemaVersion)"
}

if (-not (Test-Path $mainWindowPath)) {
    throw "MainWindow.axaml missing: $mainWindowPath"
}

$sourceText = Get-ChildItem $appRoot -Recurse -File -Include *.axaml,*.cs |
    ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 } |
    Out-String

$requiredLabels = @()
$requiredLabels += $contract.publicEditions.retail.labelDe
$requiredLabels += $contract.publicEditions.gastro.labelDe
$requiredLabels += @($contract.desktopUi.topMenu)

# These status texts may be shortened by runtime header-density logic at narrow
# widths. At least the semantic source token must still exist in TorPos.App.
$requiredLabels += 'TSE-AUSFALL'
$requiredLabels += 'ABMELDEN'
$requiredLabels += 'AUSSER HAUS'
$requiredLabels += 'GEMISCHT'
$requiredLabels += $contract.desktopUi.quickActions.barcode
$requiredLabels += $contract.desktopUi.quickActions.search
$requiredLabels += $contract.desktopUi.quickActions.quickItem
$requiredLabels += $contract.desktopUi.quickActions.discount
$requiredLabels += $contract.desktopUi.quickActions.openOrders
$requiredLabels += $contract.desktopUi.quickActions.checkout
$requiredLabels += $contract.desktopUi.cashier.currentSale
$requiredLabels += 'BESTELLUNG'

$missing = @()
foreach ($label in ($requiredLabels | Select-Object -Unique)) {
    if ([string]::IsNullOrWhiteSpace($label)) { continue }
    if ($sourceText.IndexOf([string]$label, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        $missing += $label
    }
}

$mainWindow = Get-Content $mainWindowPath -Raw -Encoding UTF8
$requiredColors = @(
    $contract.theme.background,
    $contract.theme.header,
    $contract.theme.productTile,
    $contract.theme.productBorder
) | Select-Object -Unique

foreach ($color in $requiredColors) {
    if ($mainWindow.IndexOf([string]$color, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        $missing += "theme:$color"
    }
}

foreach ($editionName in @('retail', 'gastro')) {
    $catalog = $contract.demoCatalogs.$editionName
    if ($null -eq $catalog -or @($catalog).Count -lt 1) {
        $missing += "demoCatalogs.$editionName"
        continue
    }

    foreach ($category in @($catalog)) {
        if ([string]::IsNullOrWhiteSpace([string]$category.id) -or
            [string]::IsNullOrWhiteSpace([string]$category.name) -or
            [string]::IsNullOrWhiteSpace([string]$category.color) -or
            @($category.products).Count -lt 1) {
            $missing += "invalid-category:$editionName/$($category.id)"
        }
    }
}

# The TOR Cloud portal preview in the website simulator uses the portal's own
# labels. They must exist in Cloud/public (portal.html / app.js) so the preview
# never shows a menu or heading the real portal does not have.
if ($null -ne $contract.cloudPortal) {
    $cloudText = Get-ChildItem (Join-Path $root 'Cloud/public') -File -Include *.html,*.js -Recurse |
        ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 } |
        Out-String
    $cloudLabels = @()
    $cloudLabels += @($contract.cloudPortal.menu)
    $cloudLabels += @($contract.cloudPortal.reports.PSObject.Properties | ForEach-Object { $_.Value })
    $cloudLabels += @($contract.cloudPortal.deviceStatus.PSObject.Properties | ForEach-Object { $_.Value })
    foreach ($label in ($cloudLabels | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($label)) { continue }
        if ($cloudText.IndexOf([string]$label, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $missing += "cloudPortal:$label"
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$contract.cloudPortal.previewNote)) {
        $missing += 'cloudPortal.previewNote'
    }
}

if ($missing.Count -gt 0) {
    $details = ($missing | Sort-Object -Unique) -join ', '
    throw "Simulator contract is out of sync with TOR POS: $details"
}

Write-Host "Simulator contract OK (schema v$($contract.schemaVersion))."
