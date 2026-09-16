$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw ".NET Framework x64 C# compiler not found: $compiler"
}

& $compiler /nologo /target:winexe /platform:x64 /optimize+ /debug- /checked+ `
    "/out:$projectDir\LiteDesktopSwitcher.exe" `
    "$projectDir\LiteDesktopSwitcher.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Get-FileHash -Algorithm SHA256 -LiteralPath "$projectDir\LiteDesktopSwitcher.exe"
