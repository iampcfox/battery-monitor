$ErrorActionPreference = 'Continue'

# 完全复刻 C# 里的反射链：Type.GetType(ContentType=WindowsRuntime) → FromBluetoothAddressAsync
#   → System.WindowsRuntimeSystemExtensions.AsTask<T> → Task.Wait → Result.ConnectionStatus
$tLe = [Type]::GetType('Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime')
$tCl = [Type]::GetType('Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime')
"tLe = $tLe"
"tCl = $tCl"

$asm = [Reflection.Assembly]::Load('System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
$ext = $asm.GetType('System.WindowsRuntimeSystemExtensions')
"ext = $ext"

$asTask = $null
foreach ($m in $ext.GetMethods([Reflection.BindingFlags]'Public,Static')) {
    if (-not $m.IsGenericMethodDefinition) { continue }
    if ($m.Name -ne 'AsTask') { continue }
    $ps = $m.GetParameters()
    if ($ps.Length -ne 1) { continue }
    if ($ps[0].ParameterType.Name -like 'IAsyncOperation*') { $asTask = $m; break }
}
"asTask = $asTask"

function Query-Link($type, [uint64]$addr) {
    if ($null -eq $type -or $null -eq $asTask) { return @{ known = $false; on = $false } }
    $from = $type.GetMethod('FromBluetoothAddressAsync', [Type[]]@([uint64]))
    if ($null -eq $from) { return @{ known = $false; on = $false } }
    $op = $from.Invoke($null, @($addr))
    if ($null -eq $op) { return @{ known = $false; on = $false } }
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
    if ($null -eq $task) { return @{ known = $false; on = $false } }
    $null = $task.Wait(400)
    if ([int]$task.Status -ne 5) { return @{ known = $false; on = $false; status = $task.Status } }
    $dev = $task.Result
    if ($null -eq $dev) { return @{ known = $false; on = $false; note = 'null-result' } }
    $cs = $dev.GetType().GetProperty('ConnectionStatus').GetValue($dev, $null)
    $closable = $dev -as [IDisposable]
    if ($closable) { $closable.Dispose() }
    return @{ known = $true; on = ([int]$cs -eq 1); raw = "$cs"; disposable = [bool]$closable }
}

$targets = @(
    @('QCY 耳机', 0x84AC60112BD4),
    @('AK820 键盘', 0xE6BD813A9447),
    @('Pro Controller', 0x98B6E91CF14D)
)
foreach ($t in $targets) {
    $addr = [uint64]$t[1]
    $a = Query-Link $tLe $addr
    $b = Query-Link $tCl $addr
    $final = 'null(不知道)'
    if ($a.known -and $a.on) { $final = 'true(连着)' }
    elseif ($b.known -and $b.on) { $final = 'true(连着)' }
    elseif ($a.known -or $b.known) { $final = 'false(断开)' }
    '{0,-16} LE known={1,-5} on={2,-5} {3} | CL known={4,-5} on={5,-5} {6} => {7}' -f `
        $t[0], $a.known, $a.on, $a.raw, $b.known, $b.on, $b.raw, $final
}
