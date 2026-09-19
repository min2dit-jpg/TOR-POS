$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$updates=if($env:TOR_CLOUD_UPDATES){$env:TOR_CLOUD_UPDATES}else{Join-Path $root 'updates'}
& node (Join-Path $root 'update-store.js') disable-trial $updates
if($LASTEXITCODE -ne 0){throw 'TOR POS Demo konnte nicht deaktiviert werden.'}
Write-Host 'TOR POS Demo Download deaktiviert.'
