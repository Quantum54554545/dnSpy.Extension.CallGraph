param([Parameter(Mandatory = $true)][string]$plan_path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($plan_path))
$result_path = Join-Path $stage 'result.json'
$cancel_path = Join-Path $stage 'cancel'
$incoming = $null
$backup = $null
$guard = $null
$installed = $false
$parent_exited = $false
$plan = $null
$child = $null
$restart_error = $null
$confirmed = $false

function write_result([string]$status, [string]$message) {
    @{ Status = $status; Message = $message; Time = [DateTime]::UtcNow.ToString('o'); Backup = $backup } | ConvertTo-Json | Set-Content -LiteralPath $result_path -Encoding UTF8
}

function quote_argument([string]$value) {
    '"' + [regex]::Replace([regex]::Replace($value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function get_hash([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function restart_host {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $plan.Executable
    $start.WorkingDirectory = $plan.WorkingDirectory
    $start.UseShellExecute = $false
    if ($installed) { $start.EnvironmentVariables['CALLGRAPH_UPDATE_PLAN'] = $plan_path }
    else { $start.EnvironmentVariables.Remove('CALLGRAPH_UPDATE_PLAN') }
    if ($null -ne $restart_error) { $start.EnvironmentVariables['CALLGRAPH_UPDATE_ERROR'] = $restart_error }
    $start.Arguments = (@($plan.Arguments) | ForEach-Object { quote_argument ([string]$_) }) -join ' '
    $child = [Diagnostics.Process]::Start($start)
    if ($null -eq $child) { throw 'Could not restart dnSpy.' }
    return $child
}

try {
    $plan = Get-Content -LiteralPath $plan_path -Raw | ConvertFrom-Json
    $candidate = [IO.Path]::GetFullPath($plan.Candidate)
    $destination = [IO.Path]::GetFullPath($plan.Destination)
    if ($candidate -ne (Join-Path $stage 'candidate.dll') -or [IO.Path]::GetFileName($destination) -ne 'CallGraph.Extension.x.dll') { throw 'Invalid installer paths.' }
    if (-not (Test-Path -LiteralPath $plan.Executable -PathType Leaf) -or -not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw 'The original installation is missing.' }
    if (([IO.File]::GetAttributes($destination) -band [IO.FileAttributes]::ReadOnly) -ne 0) { throw 'The installed DLL is read-only; dnSpy has not been closed.' }
    if ((get_hash $candidate) -ne $plan.Sha256 -or (get_hash $destination) -ne $plan.OriginalSha256) { throw 'The downloaded or installed DLL changed.' }
    $directory = [IO.Path]::GetDirectoryName($destination)
    $guard = [IO.File]::Open((Join-Path $directory '.callgraph-update.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $incoming = Join-Path $directory ('.callgraph-' + [Guid]::NewGuid().ToString('N') + '.new')
    [IO.File]::Copy($candidate, $incoming, $false)
    if ((get_hash $incoming) -ne $plan.Sha256) { throw 'Staged DLL hash mismatch.' }
    $parent = $null
    try { $parent = [Diagnostics.Process]::GetProcessById([int]$plan.ProcessId) } catch [ArgumentException] { }
    if ($null -ne $parent -and $parent.StartTime.ToUniversalTime().Ticks -ne [long]$plan.ProcessStartTicks) { throw 'The dnSpy process identity changed.' }
    [IO.File]::WriteAllText((Join-Path $stage 'ready'), 'ready')
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ($null -ne $parent -and -not $parent.HasExited) {
        if ((Test-Path -LiteralPath $cancel_path) -or [DateTime]::UtcNow -ge $deadline) { write_result 'Cancelled' 'dnSpy did not close; the plugin was not replaced.'; exit 2 }
        Start-Sleep -Milliseconds 150
        $parent.Refresh()
    }
    $parent_exited = $true
    if (Test-Path -LiteralPath $cancel_path) { write_result 'Cancelled' 'Update cancelled.'; exit 2 }
    if ((get_hash $destination) -ne $plan.OriginalSha256 -or (get_hash $incoming) -ne $plan.Sha256) { throw 'The installation changed while dnSpy was closing.' }
    $backup = $destination + '.before-' + $plan.Version + '.' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.bak'
    $attempt = 0
    while ($true) {
        try { [IO.File]::Replace($incoming, $destination, $backup); break }
        catch [IO.IOException] { if (++$attempt -ge 20) { throw }; Start-Sleep -Milliseconds 250 }
    }
    $installed = $true
    if ((get_hash $destination) -ne $plan.Sha256) { throw 'Installed DLL hash mismatch.' }
    $child = restart_host
    $ack = Join-Path $stage 'ack'
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(2, [Math]::Min(120, [int]$plan.AcknowledgeSeconds)))
    while (-not (Test-Path -LiteralPath $ack) -and -not $child.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100; $child.Refresh() }
    if (-not (Test-Path -LiteralPath $ack) -or [IO.File]::ReadAllText($ack) -ne $plan.Nonce) { throw 'The updated plugin did not confirm successful loading.' }
    [IO.File]::Delete($backup)
    $confirmed = $true
    write_result 'Installed' ('Installed v' + $plan.Version)
} catch {
    $message = $_.Exception.Message
    if ($installed -and ($null -eq $child -or $child.HasExited) -and $null -ne $backup -and (Test-Path -LiteralPath $backup)) {
        try { $rejected = $destination + '.rejected-' + [Guid]::NewGuid().ToString('N') + '.tmp'; [IO.File]::Replace($backup, $destination, $rejected); $installed = $false; [IO.File]::Delete($rejected) }
        catch { $message += ' Rollback failed: ' + $_.Exception.Message }
    }
    try { write_result 'Failed' $message } catch { }
    $restart_error = $message
    if ($parent_exited -and -not $installed -and $null -ne $plan) { try { $reopened = restart_host; $reopened.Dispose() } catch { } }
    exit 1
} finally {
    if ($null -ne $child) { $child.Dispose() }
    if ($null -ne $guard) { $guard.Dispose(); try { [IO.File]::Delete((Join-Path $directory '.callgraph-update.lock')) } catch { } }
    if ($null -ne $incoming -and (Test-Path -LiteralPath $incoming)) { Remove-Item -LiteralPath $incoming -ErrorAction SilentlyContinue }
    foreach ($name in @('candidate.dll', 'plan.json', 'ready', 'cancel', 'ack')) {
        $temporary = Join-Path $stage $name
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue }
    }
    if ($confirmed -and (Test-Path -LiteralPath $result_path)) { Remove-Item -LiteralPath $result_path -ErrorAction SilentlyContinue }
}
