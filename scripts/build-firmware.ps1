param([Parameter(Mandatory=$true)][string]$ArduinoCli,[string]$Board='esp32:esp32:esp32:FlashMode=dio,FlashFreq=40')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$config=Join-Path $root '.tools/arduino-cli.yaml'
if(-not(Test-Path $config)){throw 'Local Arduino configuration is absent. Prepare tools after user approval.'}
$cores=& $ArduinoCli --config-file $config core list --format json | ConvertFrom-Json
if($LASTEXITCODE){throw 'Cannot inspect Arduino cores'}
if(-not($cores.platforms | Where-Object {$_.id -eq 'esp32:esp32' -and $_.installed_version -eq '3.3.4'})){throw 'Install the approved ESP32 core 3.3.4 in the local tool directory before building.'}
$out=Join-Path $root 'artifacts/firmware'
$extra=@()
# Official core can also be installed manually from verified GitHub archives when Arduino CDN is unavailable.
if(Test-Path (Join-Path $root '.tools/arduino-user/hardware/local/esp32/platform.txt')){
 $Board=$Board -replace '^esp32:esp32:','local:esp32:'
 foreach($tool in @(@('esp-x32','2507'),@('esp32-arduino-libs','idf-release_v5.5-8410210c-v2'),@('esptool_py','5.1.0'))){
  $path=(Resolve-Path (Join-Path $root ('.tools/arduino-data/packages/esp32/tools/'+$tool[0]+'/'+$tool[1]))).Path
  $extra+=@('--build-property',('runtime.tools.'+$tool[0]+'.path='+$path))
 }
}
& $ArduinoCli --config-file $config compile --fqbn $Board --warnings all --build-path (Join-Path $root '.tools/build') --output-dir $out @extra (Join-Path $root 'firmware/Ambilight')
if($LASTEXITCODE){throw 'Firmware build failed'}
Get-ChildItem $out -Filter '*.bin' | ForEach-Object {[pscustomobject]@{Name=$_.Name;Size=$_.Length;SHA256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}} | ConvertTo-Json | Set-Content (Join-Path $out 'checksums.json') -Encoding utf8NoBOM
