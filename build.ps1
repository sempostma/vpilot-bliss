param([string]$VpilotPath = "$env:LOCALAPPDATA\vPilot", [switch]$Install)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & dotnet build .\vPilot.Bliss.csproj -c Release --nologo "-p:VpilotPath=$VpilotPath"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $package = Join-Path $PSScriptRoot 'dist\vPilot-Bliss'
    New-Item -ItemType Directory -Force -Path "$package\Bliss.lib" | Out-Null
    Copy-Item -LiteralPath '.\bin\Release\net472\vPilot.Bliss.dll' -Destination $package
    Copy-Item -Path '.\bin\Release\net472\NAudio.*.dll' -Destination "$package\Bliss.lib"
    Copy-Item -LiteralPath '.\bin\Release\net472\QRCoder.dll' -Destination "$package\Bliss.lib"
    Copy-Item -LiteralPath '.\README.md', '.\LICENSE', '.\THIRD-PARTY-NOTICES.md' -Destination $package
    Copy-Item -LiteralPath '.\assets' -Destination $package -Recurse -Force
    Compress-Archive -Path "$package\vPilot.Bliss.dll", "$package\Bliss.lib", "$package\README.md", "$package\LICENSE", "$package\THIRD-PARTY-NOTICES.md", "$package\assets" -DestinationPath '.\dist\vPilot-Bliss.zip' -Force
    if ($Install) {
        if (Get-Process vPilot -ErrorAction SilentlyContinue) { throw 'Close vPilot before installing. The package is ready in dist.' }
        $plugins = Join-Path $VpilotPath 'Plugins'
        New-Item -ItemType Directory -Force -Path $plugins | Out-Null
        Copy-Item -LiteralPath "$package\vPilot.Bliss.dll" -Destination $plugins
        Copy-Item -LiteralPath "$package\Bliss.lib" -Destination $plugins -Recurse -Force
        Write-Host "Installed to $plugins. Start vPilot to use Bliss."
    }
} finally { Pop-Location }
