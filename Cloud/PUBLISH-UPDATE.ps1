param(
 [Parameter(Mandatory=$true)][string]$SetupPath,
 [Parameter(Mandatory=$true)][string]$Version,
 [Parameter(Mandatory=$true)][string]$Revision,
 [Parameter(Mandatory=$true)][string]$SignerThumbprint,
 [switch]$Mandatory,
 [ValidateSet("STABLE","PILOT")][string]$Channel = "STABLE",
 [string]$ReleaseNotes = ""
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$updates=if($env:TOR_CLOUD_UPDATES){$env:TOR_CLOUD_UPDATES}else{Join-Path $root 'updates'}
$expected=($SignerThumbprint -replace ' ','').ToUpperInvariant()
if($expected -notmatch '^[0-9A-F]{40}$'){throw 'Gültiger Signer-Thumbprint erforderlich.'}
if(!(Test-Path -LiteralPath $SetupPath -PathType Leaf)){throw 'Setup nicht gefunden.'}
Get-Command node -ErrorAction Stop | Out-Null
$staging=Join-Path ([IO.Path]::GetTempPath()) ('TOR-POS-update-'+[Guid]::NewGuid().ToString('N')+'.exe')
$inputFile=$staging+'.json'
try {
 Copy-Item -LiteralPath $SetupPath -Destination $staging
 $sha=(Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash.ToUpperInvariant()
 $sig=Get-AuthenticodeSignature -LiteralPath $staging
 if($sig.Status -ne 'Valid'){throw "Setup-Signatur ungültig: $($sig.Status)"}
 $actual=($sig.SignerCertificate.Thumbprint -replace ' ','').ToUpperInvariant()
 if($actual -ne $expected){throw 'Signer stimmt nicht mit freigegebenem Zertifikat überein.'}
 if((Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash.ToUpperInvariant() -ne $sha){throw 'Setup während Prüfung verändert.'}
 @{source=$staging;version=$Version;revision=$Revision;sha256=$sha;signer_thumbprint=$expected;mandatory=[bool]$Mandatory;release_notes=$ReleaseNotes} | ConvertTo-Json | Set-Content -LiteralPath $inputFile -Encoding UTF8
 $action=if($Channel -eq 'PILOT'){'publish-pilot'}else{'publish'}
 & node (Join-Path $root 'update-store.js') $action $updates $inputFile
 if($LASTEXITCODE -ne 0){throw 'Veröffentlichung fehlgeschlagen; vorheriges Manifest bleibt gültig.'}
 Write-Host "TOR Update veröffentlicht: $Revision / $Version / $Channel"
} finally { Remove-Item -LiteralPath $staging,$inputFile -Force -ErrorAction SilentlyContinue }
