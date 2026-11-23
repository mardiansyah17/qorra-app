$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 not found at $iscc" }

Remove-Item artifacts/publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/DbClient.App -c Release -o artifacts/publish
if ($LASTEXITCODE) { throw 'publish failed' }

& $iscc installer/windows/qorra.iss
if ($LASTEXITCODE) { throw 'ISCC failed' }

Get-ChildItem artifacts/installer/*.exe | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { "`nDone: $($_.FullName)" }
