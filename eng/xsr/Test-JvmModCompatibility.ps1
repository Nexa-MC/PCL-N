param(
    [Parameter(Mandatory = $true)][string]$HostExecutable,
    [Parameter(Mandatory = $true)][string]$JavaHome
)
$ErrorActionPreference = 'Stop'
$hostPath = (Resolve-Path -LiteralPath $HostExecutable).Path
$suffix = if ($IsWindows) { '.exe' } else { '' }
$javaPath = (Resolve-Path -LiteralPath (Join-Path $JavaHome "bin/java$suffix")).Path
$javacPath = Join-Path $JavaHome "bin/javac$suffix"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("nexa-mod-compat-" + [Guid]::NewGuid().ToString('N') + ' 中文 with space')
[IO.Directory]::CreateDirectory($scratch) | Out-Null
try {
    & $javacPath -encoding UTF-8 -d $scratch (Join-Path $PSScriptRoot 'fixtures/JvmHostModCompatibility.java')
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility fixture javac failed' }
    function Write-ModField($writer, [string]$value) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($value)
        $writer.Write([int]$bytes.Length)
        $writer.Write($bytes)
    }
    foreach ($mode in @('normal', 'exit', 'wait', 'orphan')) {
        $modeDirectory = Join-Path $scratch $mode
        [IO.Directory]::CreateDirectory($modeDirectory) | Out-Null
        $payload = [IO.MemoryStream]::new()
        $writer = [IO.BinaryWriter]::new($payload)
        $writer.Write([int]0x4E4A564D)
        $writer.Write([int]1)
        Write-ModField $writer $javaPath
        Write-ModField $writer $modeDirectory
        Write-ModField $writer 'JvmHostModCompatibility'
        $jvm = @('-Xmx64m', '-Xcheck:jni', '-cp', $scratch)
        $writer.Write([int]$jvm.Length)
        foreach ($value in $jvm) { Write-ModField $writer $value }
        $game = @($mode, $hostPath, $javaPath, $modeDirectory, 'private-mod-compat-token-fixture')
        $writer.Write([int]$game.Length)
        foreach ($value in $game) { Write-ModField $writer $value }
        $bytes = $payload.ToArray()
        $writer.Dispose(); $payload.Dispose()
        $start = [Diagnostics.ProcessStartInfo]::new($hostPath)
        $start.ArgumentList.Add('--jvm-host')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $parent = [Diagnostics.Process]::Start($start)
        $descendantId = $null
        try {
            $stdout = $parent.StandardOutput.ReadToEndAsync()
            $stderr = $parent.StandardError.ReadToEndAsync()
            $pipe = [IO.BinaryWriter]::new($parent.StandardInput.BaseStream)
            $pipe.Write([int]$bytes.Length); $pipe.Write($bytes); $pipe.Flush()
            $parent.StandardInput.Close()
            if ($mode -eq 'wait') {
                $pidFile = Join-Path $modeDirectory 'child-pid.txt'
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                while (!(Test-Path -LiteralPath $pidFile) -and !$parent.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                    Start-Sleep -Milliseconds 50
                }
                if (!(Test-Path -LiteralPath $pidFile)) { throw 'CA-like Java child did not start' }
                $descendantId = [int](Get-Content -LiteralPath $pidFile -Raw)
                if ($parent.WaitForExit(200)) { throw 'Waiting parent exited early' }
                $parent.Kill($true)
            }
            if (!$parent.WaitForExit(15000)) { throw 'CA-like host process timed out' }
            $output = $stdout.GetAwaiter().GetResult()
            $errorOutput = $stderr.GetAwaiter().GetResult()
            if ($output -match 'private-mod-compat-token-fixture' -or $errorOutput -match 'private-mod-compat-token-fixture') { throw 'Private game token leaked' }
            $expectedCode = if ($mode -eq 'exit') { 7 } else { 0 }
            if ($mode -ne 'wait' -and $parent.ExitCode -ne $expectedCode) { throw "CA-like $mode exit=$($parent.ExitCode): $errorOutput" }
            if ($mode -eq 'normal' -and ($output -notmatch 'NEXA_CA_CHILD_STDOUT' -or $errorOutput -notmatch 'NEXA_CA_CHILD_STDERR' -or $output -notmatch 'NEXA_CA_PARENT_RETURNED')) { throw "CA-like output missing: $output $errorOutput" }
            if ($mode -eq 'exit' -and $output -notmatch 'NEXA_CA_CHILD_EXIT_13') { throw 'Java child exit was not forwarded' }
            if ($mode -eq 'wait') {
                if ($output -notmatch 'NEXA_CA_CHILD_WAITING') { throw 'CA-like wait never reached child' }
                # Unix may briefly retain a killed zombie until the platform reaps it.
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                $alive = $true
                do {
                    $alive = $false
                    try {
                        $descendant = [Diagnostics.Process]::GetProcessById($descendantId)
                        try {
                            $alive = !$descendant.HasExited
                            if ($IsLinux -and $alive) {
                                $stat = Get-Content -LiteralPath "/proc/$descendantId/stat" -Raw -ErrorAction SilentlyContinue
                                if ($stat -match '^\d+ \(.+\) Z ') { $alive = $false }
                            }
                        } finally { $descendant.Dispose() }
                    } catch [ArgumentException] { }
                    if ($alive) { Start-Sleep -Milliseconds 50 }
                } while ($alive -and [DateTime]::UtcNow -lt $deadline)
                if ($alive) { throw 'Tree stop left a running CA-like Java child' }
            }
            if ($mode -eq 'orphan') {
                $survival = Join-Path $modeDirectory 'child-survived.txt'
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                while (!(Test-Path -LiteralPath $survival) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
                if (!(Test-Path -LiteralPath $survival) -or (Get-Content -LiteralPath $survival -Raw) -ne 'NEXA_CA_CHILD_SURVIVED') { throw 'Natural game exit prevented the CA-like analysis child' }
            }
            Write-Output "PASS: CA-like executable child $mode"
        }
        finally {
            [Array]::Clear($bytes, 0, $bytes.Length)
            if (!$parent.HasExited) { $parent.Kill($true); $parent.WaitForExit() }
            $pidFile = Join-Path $modeDirectory 'child-pid.txt'
            if (!$descendantId -and (Test-Path -LiteralPath $pidFile)) { $descendantId = [int](Get-Content -LiteralPath $pidFile -Raw) }
            if ($descendantId) {
                try {
                    $remaining = [Diagnostics.Process]::GetProcessById($descendantId)
                    try { if (!$remaining.HasExited) { $remaining.Kill($true) } } finally { $remaining.Dispose() }
                } catch [ArgumentException] { }
            }
            $parent.Dispose()
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('nexa-mod-compat-')) { throw 'Invalid compatibility cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
