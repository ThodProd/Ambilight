param([string]$HubRoot='D:/ThodPROD',[string]$Compiler='D:/ThodPROD/.tools/llvm/llvm-mingw-20260616-ucrt-x86_64/bin/x86_64-w64-mingw32-clang++.exe')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$manifest=Get-Content (Join-Path $root 'hub/manifest.json') -Raw | ConvertFrom-Json
$project=Join-Path $root 'src/Ambilight.Worker/Ambilight.Worker.csproj'
$projectVersion=([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version
if($projectVersion -ne $manifest.version){throw 'Project/manifest version mismatch'}
$stage=Join-Path $root ('artifacts/stage-'+$manifest.version+'-'+[guid]::NewGuid().ToString('N'))
$out=Join-Path $root ('artifacts/release-'+$manifest.version)
if(Test-Path (Join-Path $out ('ambilight-'+$manifest.version+'-win-x64.zip'))){throw 'Immutable package already exists. Increment the version.'}
New-Item -ItemType Directory -Force $stage,(Join-Path $stage 'worker') | Out-Null
& (Join-Path $PSScriptRoot 'make-icon.ps1')
& $Compiler -std=c++17 -O2 -shared -static -o (Join-Path $stage 'worker/Ambilight.Capture.dll') (Join-Path $root 'native/capture.cpp') -ld3d11 -ldxgi
if($LASTEXITCODE){throw 'Native capture build failed'}
dotnet publish $project -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.12 -o (Join-Path $stage 'worker') --ignore-failed-sources
if($LASTEXITCODE){throw 'Worker publish failed'}
Copy-Item (Join-Path $root 'hub/manifest.json') $stage
foreach($directory in @('ui','assets','docs','firmware','licenses')){Copy-Item (Join-Path $root $directory) (Join-Path $stage $directory) -Recurse}
Copy-Item (Join-Path $HubRoot 'sdk/thodhub.js') (Join-Path $stage 'ui/thodhub.js')
foreach($file in @('README.md','THIRD-PARTY-NOTICES.md')){Copy-Item (Join-Path $root $file) $stage}
Copy-Item $PSScriptRoot (Join-Path $stage 'scripts') -Recurse
if(Test-Path (Join-Path $root 'artifacts/firmware/Ambilight.ino.bin')){
 $bins=Join-Path $stage 'firmware/binaries';New-Item -ItemType Directory -Force $bins | Out-Null
 Get-ChildItem (Join-Path $root 'artifacts/firmware') -File | Where-Object {$_.Extension -eq '.bin' -or $_.Name -eq 'checksums.json'} | Copy-Item -Destination $bins
}
Get-ChildItem $stage -Filter '*.json' -Recurse | ForEach-Object {$b=[IO.File]::ReadAllBytes($_.FullName);if($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191){throw ('BOM: '+$_.FullName)}}
$notes='Видимая область захвата: при настройке глубины полосы подсвечиваются прямо на мониторе. Кадры уходят на ленту сразу после расчёта, опрос ESP32 больше не задерживает свет.'
& (Join-Path $HubRoot 'scripts/package-module.ps1') -PackageDirectory $stage -Repository 'ThodProd/Ambilight' -OutputDirectory $out -Notes $notes
if($LASTEXITCODE){throw 'Package validation failed'}
Write-Output ('Stage: '+$stage)
