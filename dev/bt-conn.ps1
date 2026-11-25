Add-Type -AssemblyName System.Runtime.WindowsRuntime
$ErrorActionPreference = 'Continue'

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($WinRtTask, $ResultType) {
    if ($null -eq $WinRtTask) { return $null }
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    return $netTask.Result
}

$null = [Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime]
$null = [Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType = WindowsRuntime]

$targets = @(
    @('QCY 耳机', 0x84AC60112BD4),
    @('AK820 键盘', 0xE6BD813A9447),
    @('Pro Controller', 0x98B6E91CF14D)
)

foreach ($t in $targets) {
    $name = $t[0]
    $addr = [uint64]$t[1]
    $le = 'n/a'
    $cl = 'n/a'
    try {
        $d = Await ([Windows.Devices.Bluetooth.BluetoothLEDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothLEDevice])
        if ($d) { $le = $d.ConnectionStatus.ToString() } else { $le = 'null' }
    } catch { $le = 'ERR:' + $_.Exception.Message }
    try {
        $d2 = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
        if ($d2) { $cl = $d2.ConnectionStatus.ToString() } else { $cl = 'null' }
    } catch { $cl = 'ERR:' + $_.Exception.Message }
    '{0,-16} LE={1,-14} Classic={2}' -f $name, $le, $cl
}
