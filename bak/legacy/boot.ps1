$ErrorActionPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'BatteryFloat.exe'
$ps1  = Join-Path $root 'BatteryFloat.ps1'
$log  = Join-Path $root 'battery-float.log'

function Write-BootLog([string]$m) {
    try {
        Add-Content -Path $log -Value ('[{0}] {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $m) -Encoding UTF8
    } catch { }
}

if (Test-Path $exe) {
    Start-Process -FilePath $exe -WorkingDirectory $root
    Write-BootLog 'boot: started BatteryFloat.exe'
}
elseif (Test-Path $ps1) {
    Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', $ps1 `
        -WorkingDirectory $root
    Write-BootLog 'boot: started BatteryFloat.ps1 (fallback, exe not found)'
}
