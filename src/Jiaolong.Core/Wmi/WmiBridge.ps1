# Persistent WMI bridge for TDPC.
# Speaks a trivial ASCII line protocol on stdin/stdout:
#   PING              -> OK PONG
#   GET <hexcmd>      -> OK <32 bytes as hex>            e.g.  "GET 0D"  -> "OK 0080000D..."
#   SET <hexcmd> <b..>-> OK <reply hex>
#   QUIT              -> (closes)
# Any failure -> ERR <message>  (single line, no CR/LF inside)
#
# Uses System.Management, which works on this machine while raw COM IWbemLocator does not.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference  = 'SilentlyContinue'
$VerbosePreference  = 'SilentlyContinue'
$NS   = 'root\WMI'
$INST = "MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'"
$METH = 'MiInterface'
$GET  = [byte]0xFA
$SET  = [byte]0xFB

$script:miObj = $null
$script:miInParams = $null
$script:miOpts = New-Object System.Management.InvokeMethodOptions
$script:miOpts.Timeout = [TimeSpan]::FromSeconds(5)

function Reset-MiObject {
    if ($null -ne $script:miInParams) {
        try { $script:miInParams.Dispose() } catch { }
        $script:miInParams = $null
    }
    if ($null -ne $script:miObj) {
        try { $script:miObj.Dispose() } catch { }
        $script:miObj = $null
    }
}

function Send-Packet([byte]$type, [byte]$cmd, [byte[]]$payload) {
    $pkt = New-Object byte[] 32
    $pkt[1] = $type
    $pkt[3] = $cmd
    if ($payload) {
        for ($i = 0; $i -lt $payload.Length -and $i -lt 28; $i++) { $pkt[4 + $i] = $payload[$i] }
    }

    if ($null -eq $script:miObj -or $null -eq $script:miInParams) {
        Reset-MiObject
        $script:miObj = New-Object System.Management.ManagementObject($NS, $INST, $null)
        $script:miInParams = $script:miObj.GetMethodParameters($METH)
    }

    $res = $null
    try {
        $script:miInParams['InData'] = [byte[]]$pkt
        $res = $script:miObj.InvokeMethod($METH, [System.Management.ManagementBaseObject]$script:miInParams, $script:miOpts)
        if ($null -ne $res) {
            foreach ($p in $res.Properties) {
                if ($p.Name -eq 'OutData' -and $p.Value -is [byte[]]) { return [byte[]]$p.Value }
            }
        }
        return $null
    }
    catch {
        Reset-MiObject
        throw
    }
    finally {
        if ($null -ne $res) {
            try { $res.Dispose() } catch { }
        }
    }
}

function Reply([string]$s) {
    [Console]::Out.WriteLine($s)
    [Console]::Out.Flush()
}

Reply 'OK READY'
$reader = [Console]::In
while (($line = $reader.ReadLine()) -ne $null) {
    $line = $line.Trim()
    if ($line.Length -eq 0) { continue }
    $parts = $line.Split(' ')
    $verb  = $parts[0].ToUpperInvariant()
    try {
        switch ($verb) {
            'PING' { Reply 'OK PONG' }
            'QUIT' {
                Reset-MiObject
                Reply 'OK BYE'
                exit 0
            }
            'GET'  {
                if ($parts.Length -lt 2) { throw 'GET needs a command byte' }
                $cmd  = [Convert]::ToByte($parts[1], 16)
                $data = Send-Packet $GET $cmd $null
                if (-not $data) { throw 'no OutData returned' }
                Reply ('OK ' + [BitConverter]::ToString($data).Replace('-', ''))
            }
            'SET'  {
                if ($parts.Length -lt 3) { throw 'SET needs command + payload' }
                $cmd = [Convert]::ToByte($parts[1], 16)
                [byte[]]$pay = New-Object byte[] ($parts.Length - 2)
                for ($i = 2; $i -lt $parts.Length; $i++) { $pay[$i - 2] = [Convert]::ToByte($parts[$i], 16) }
                $data = Send-Packet $SET $cmd $pay
                if (-not $data) { throw 'no OutData returned' }
                Reply ('OK ' + [BitConverter]::ToString($data).Replace('-', ''))
            }
            # Arbitrary WQL.  Query is URI-escaped so it survives as a single token.
            # Reply: OK <escaped rows>   rows separated by ';', fields by '~', each field "Name=Value"
            # (names are included because WMI returns properties in ALPHABETICAL order,
            #  which is NOT the SELECT order - relying on index silently scrambles the data)
            'WQL' {
                if ($parts.Length -lt 2) { throw 'WQL needs a query' }
                $q = [Uri]::UnescapeDataString($parts[1])
                $objs = Get-WmiObject -Query $q -ErrorAction Stop
                $rows = @()
                try {
                    foreach ($o in @($objs)) {
                        if ($null -eq $o) { continue }
                        $fields = @()
                        foreach ($p in $o.Properties) { $fields += ($p.Name + '=' + [string]$p.Value) }
                        $rows += ($fields -join '~')
                    }
                }
                finally {
                    foreach ($o in @($objs)) {
                        if ($o -is [IDisposable]) {
                            try { $o.Dispose() } catch { }
                        }
                    }
                }
                Reply ('OK ' + [Uri]::EscapeDataString(($rows -join ';')))
            }
            default { Reply ('ERR unknown verb ' + $verb) }
        }
    } catch {
        $m = ($_.Exception.Message -replace '[\r\n]+', ' ').Trim()
        Reply ('ERR ' + $m)
    }
}
