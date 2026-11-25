Add-Type -AssemblyName System.Runtime.WindowsRuntime
$a = [System.WindowsRuntimeSystemExtensions].Assembly
'FullName : ' + $a.FullName
'Location : ' + $a.Location
'InGAC    : ' + $a.GlobalAssemblyCache
'CLR      : ' + [System.Environment]::Version.ToString()
try {
    $t = [Type]::GetType('System.WindowsRuntimeSystemExtensions', $false)
    'GetType(simple) : ' + $t
} catch { 'GetType(simple) ERR ' + $_.Exception.Message }
