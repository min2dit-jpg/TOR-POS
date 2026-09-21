param(
    [switch]$RequireGit
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Get-Location)
$maxBytes = 5MB
$errors = New-Object System.Collections.Generic.List[string]

# R174: this verifier is also shipped inside the validated source ZIP. A ZIP
# has no .git metadata, so "git ls-files" cannot define a tracked-file set
# there. Local ZIP users get an explicit SKIPPED result; CI passes -RequireGit
# and therefore still fails closed if checkout metadata is missing.
$tracked = git -C $repoRoot ls-files 2>$null
if ($LASTEXITCODE -ne 0) {
    if ($RequireGit) {
        throw "Repository hygiene: git ls-files failed."
    }

    Write-Host "REPOSITORY HYGIENE SKIPPED: no Git worktree metadata (normal for source ZIP)."
    exit 0
}

foreach ($relative in $tracked) {
    $normalized = $relative.Replace('\','/')
    $full = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        continue
    }

    $size = (Get-Item -LiteralPath $full).Length

    if ($size -gt $maxBytes) {
        $errors.Add("Tracked file exceeds 5 MiB: $normalized ($size bytes)")
    }

    if ($normalized -match '(^|/)(bin|obj|publish|installer-output|node_modules)/') {
        $errors.Add("Generated/build directory is tracked: $normalized")
    }

    if ($normalized -match '\.(dll|exe|msi|nupkg|zip|7z|rar)$') {
        $errors.Add("Binary/archive artifact is tracked: $normalized")
    }

    if ($normalized -match '\.(pfx|p12|torlic)$') {
        $errors.Add("Secret/licence artifact is tracked: $normalized")
    }

    if (($normalized -match '(^|/)\.env($|\.)') -and
        $normalized -ne 'Cloud/deploy/tor-pos-cloud.env.example') {
        $errors.Add("Environment secret file is tracked: $normalized")
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw "Repository hygiene check failed with $($errors.Count) problem(s)."
}

Write-Host "REPOSITORY HYGIENE OK: $($tracked.Count) tracked paths; no forbidden build/binary/secret artifacts."
