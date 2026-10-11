$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$nativeOutput = Join-Path $taskRoot 'artifacts/native'
$taskProject = Join-Path $taskRoot 'native/Ucl/Ucl.csproj'
if ($env:VCINSTALLDIR) {
    & dotnet publish $taskProject -r win-x64 -c Release -o $nativeOutput --nologo
} else {
    $taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $taskVsRoot = & $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $taskVsRoot) { throw 'Install the Visual Studio C++ build tools for Windows NativeAOT.' }
    New-Item -ItemType Directory -Path $nativeOutput -Force | Out-Null
    $taskBatch = Join-Path $nativeOutput 'build.cmd'
    @(
        '@echo off',
        ('call "{0}" -arch=x64 -host_arch=x64 >nul' -f (Join-Path $taskVsRoot 'Common7/Tools/VsDevCmd.bat')),
        'if errorlevel 1 exit /b %errorlevel%',
        ('dotnet publish "{0}" -r win-x64 -c Release -o "{1}" --nologo' -f $taskProject, $nativeOutput),
        'exit /b %errorlevel%'
    ) | Set-Content -LiteralPath $taskBatch -Encoding ascii
    & $env:ComSpec /d /c $taskBatch
}
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
$packageBin = Join-Path $taskRoot 'src/unity_compat/bin/win-x64'
New-Item -ItemType Directory -Path $packageBin -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeOutput 'ucl.exe') -Destination (Join-Path $packageBin 'ucl.exe') -Force
Get-Item -LiteralPath (Join-Path $packageBin 'ucl.exe') | Select-Object FullName,Length
