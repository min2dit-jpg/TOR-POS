$ErrorActionPreference = 'Stop'

function Read-RequiredFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Versionspruefung: Datei fehlt: $Path"
    }
    return Get-Content -LiteralPath $Path -Raw
}

function Capture-Required([string]$Text, [string]$Pattern, [string]$Label) {
    $m = [regex]::Match($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $m.Success -or $m.Groups.Count -lt 2 -or [string]::IsNullOrWhiteSpace($m.Groups[1].Value)) {
        throw "Versionspruefung: $Label konnte nicht gelesen werden."
    }
    return $m.Groups[1].Value
}

function Assert-Equal([string]$Expected, [string]$Actual, [string]$Label) {
    if ($Expected -cne $Actual) {
        throw "Versionspruefung fehlgeschlagen: $Label = '$Actual', erwartet '$Expected'."
    }
}

$core = Read-RequiredFile 'src/TorPos.Core/ReleaseInfo.cs'

$version = Capture-Required $core 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;' 'TorRelease.Version'
$revision = Capture-Required $core 'public\s+const\s+string\s+Revision\s*=\s*"([^"]+)"\s*;' 'TorRelease.Revision'
$releaseName = Capture-Required $core 'public\s+const\s+string\s+ReleaseName\s*=\s*"([^"]+)"\s*;' 'TorRelease.ReleaseName'

$manifest = Read-RequiredFile 'manifest.json' | ConvertFrom-Json
Assert-Equal $version ([string]$manifest.version) 'manifest.json version'
Assert-Equal $revision ([string]$manifest.revision) 'manifest.json revision'
Assert-Equal $releaseName ([string]$manifest.release_name) 'manifest.json release_name'

$project = Read-RequiredFile 'src/TorPos.App/TorPos.App.csproj'
Assert-Equal $version (Capture-Required $project '<Version>([^<]+)</Version>' 'TorPos.App.csproj Version') 'TorPos.App.csproj Version'
Assert-Equal $version (Capture-Required $project '<FileVersion>([^<]+)</FileVersion>' 'TorPos.App.csproj FileVersion') 'TorPos.App.csproj FileVersion'
$expectedInformational = "$version-$revision-$releaseName"
Assert-Equal $expectedInformational (Capture-Required $project '<InformationalVersion>([^<]+)</InformationalVersion>' 'TorPos.App.csproj InformationalVersion') 'TorPos.App.csproj InformationalVersion'

$installer = Read-RequiredFile 'TOR-POS-Pro-Setup.iss'
Assert-Equal $version (Capture-Required $installer '#define\s+MyAppVersion\s+"([^"]+)"' 'Installer MyAppVersion') 'Installer MyAppVersion'
Assert-Equal $releaseName (Capture-Required $installer '#define\s+MyAppReleaseName\s+"([^"]+)"' 'Installer MyAppReleaseName') 'Installer MyAppReleaseName'

$marker = "<!-- TOR_RELEASE:$revision|$version|$releaseName -->"
$repoRoot = Split-Path -Parent (Get-Location)

foreach ($doc in @('README.md', 'CHANGELOG.md')) {
    $path = Join-Path $repoRoot $doc
    $content = Read-RequiredFile $path
    if (-not $content.Contains($marker)) {
        throw "Versionspruefung fehlgeschlagen: $doc enthaelt nicht den erwarteten Release-Marker '$marker'."
    }
}

$desktopReadme = Read-RequiredFile 'README.md'
$desktopCurrent = "**Aktueller Stand:** $revision · $releaseName · $version"
if (-not $desktopReadme.Contains($desktopCurrent)) {
    throw "Versionspruefung fehlgeschlagen: Desktop/README.md enthaelt nicht '$desktopCurrent'."
}

# R171: mirror equality alone did not catch the real drift that happened after
# R149: every mirrored file still agreed on R149 while R150+ review contracts
# and product changes were already present. The highest R###ReviewTests.cs file
# now forms a lower bound for the declared release revision.
$revisionMatch = [regex]::Match($revision, '^R([0-9]+))
if (-not $revisionMatch.Success) {
    throw "Versionspruefung: TorRelease.Revision hat kein R###-Format: '$revision'."
}
$revisionNumber = [int]$revisionMatch.Groups[1].Value

$reviewRevisions = @(
    Get-ChildItem -LiteralPath 'tests/TorPos.SafetyTests' -Filter 'R*ReviewTests.cs' -File |
        ForEach-Object {
            $m = [regex]::Match($_.Name, '^R([0-9]+)ReviewTests\.cs)
            if ($m.Success) { [int]$m.Groups[1].Value }
        }
)
if ($reviewRevisions.Count -gt 0) {
    $highestReview = ($reviewRevisions | Measure-Object -Maximum).Maximum
    if ($revisionNumber -lt $highestReview) {
        throw "Versionspruefung fehlgeschlagen: Release $revision ist aelter als vorhandener R$highestReview-Reviewvertrag. ReleaseInfo/README/Manifest/Installer aktualisieren."
    }
}

Write-Host "VERSION CONSISTENCY OK: $revision · $releaseName · $version"
