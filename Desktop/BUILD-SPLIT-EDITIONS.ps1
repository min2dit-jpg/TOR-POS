param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "publish\\split"
)

$ErrorActionPreference = "Stop"
$project = "src\\TorPos.App\\TorPos.App.csproj"

function Publish-Edition(
    [string]$Edition,
    [string]$Folder,
    [string]$ExpectedExe)
{
    $target = Join-Path $OutputRoot $Folder
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $target | Out-Null

    & dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:TorProductEdition=$Edition `
        -o $target

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $Edition."
    }

    $exe = Join-Path $target $ExpectedExe
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Expected executable was not produced: $exe"
    }

    Write-Host "OK: $Edition -> $exe"
}

Publish-Edition -Edition "KIOSK" -Folder "TOR-Einzelhandel" -ExpectedExe "TOR-Einzelhandel.exe"
Publish-Edition -Edition "IMBISS" -Folder "TOR-Gastro" -ExpectedExe "TOR-Gastro.exe"

Write-Host ""
Write-Host "Split product publish completed."
Write-Host "TOR Einzelhandel : $OutputRoot\\TOR-Einzelhandel"
Write-Host "TOR Gastro : $OutputRoot\\TOR-Gastro"
