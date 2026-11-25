$log = "C:\Projects\battery-float\dev\cap-sample.txt"
"start $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $log -Encoding utf8
for ($i = 0; $i -lt 40; $i++) {
    try {
        $b = Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus -ErrorAction Stop | Select-Object -First 1
        $t = Get-Date -Format 'HH:mm:ss'
        $rc = $b.RemainingCapacity
        $v  = $b.Voltage
        $dr = $b.DischargeRate
        $cr = $b.ChargeRate
        $on = $b.PowerOnline
        "$t rem=$rc volt=$v dis=$dr chg=$cr online=$on" | Out-File -FilePath $log -Append -Encoding utf8
    } catch {
        "$(Get-Date -Format 'HH:mm:ss') ERR $_" | Out-File -FilePath $log -Append -Encoding utf8
    }
    Start-Sleep -Seconds 3
}
"end $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $log -Append -Encoding utf8
