<#
FastCopy benchmark - compares Windows' own copy engines with robocopy at
different thread counts, on the same generated test data.
Part of FastCopy by Jawad Hamza - https://github.com/jawad-hamza/fastcopy (MIT License)

  powershell -ExecutionPolicy Bypass -File benchmark\bench.ps1                  # copy within this PC
  powershell -ExecutionPolicy Bypass -File benchmark\bench.ps1 -Target E:\      # copy to a USB drive
  powershell -ExecutionPolicy Bypass -File benchmark\bench.ps1 -Scenarios small # just one workload

Test data goes to <Work>\FastCopy-bench-src and copies to <Target>\FastCopy-bench-dst.
Both folders are deleted at the end. Results are written to benchmark\results-<drive>.md.
#>
param(
    [string]$Target = $env:TEMP,
    [string]$Work = $env:TEMP,
    [int]$Runs = 3,
    [ValidateSet('small', 'photos', 'large')][string[]]$Scenarios = @('small', 'photos', 'large')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.VisualBasic

$Workloads = [ordered]@{
    small  = @{ Title = 'Many small files (like a code / documents folder)'; Count = 10000; Size = 4KB }
    photos = @{ Title = 'Photo-sized files'; Count = 300; Size = 4MB }
    large  = @{ Title = 'Large files (like videos)'; Count = 2; Size = 1GB }
}

$srcRoot = Join-Path $Work 'FastCopy-bench-src'
$dst = Join-Path $Target 'FastCopy-bench-dst'

# ---------- helpers ----------

function Remove-Dir([string]$path) {
    if ([IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path, $true) }
}

# Incompressible data, 20 sub-folders, same bytes every time (fixed seed).
function New-TestData([string]$dir, [int]$count, [long]$size) {
    Remove-Dir $dir
    $rng = New-Object Random 42
    $buf = New-Object byte[] ([int][math]::Min($size, 4MB))
    for ($i = 0; $i -lt $count; $i++) {
        $sub = Join-Path $dir ('folder{0:D2}' -f ($i % 20))
        [void][IO.Directory]::CreateDirectory($sub)
        $fs = [IO.File]::Create((Join-Path $sub ('file{0:D5}.bin' -f $i)))
        $left = $size
        while ($left -gt 0) {
            $n = [int][math]::Min($left, $buf.Length)
            $rng.NextBytes($buf)
            $fs.Write($buf, 0, $n)
            $left -= $n
        }
        $fs.Close()
    }
}

# Flush Windows' write cache and wait until the disks are idle, so every timed copy
# starts from the same state (SSDs - especially QLC ones - slow down while they
# move a burst of writes out of their fast cache).
$WriteCounter = $null
try { $WriteCounter = New-Object Diagnostics.PerformanceCounter('PhysicalDisk', 'Disk Write Bytes/sec', '_Total'); [void]$WriteCounter.NextValue() } catch { }

function Wait-DiskIdle {
    foreach ($l in ($Work, $Target)) {
        try { Write-VolumeCache -DriveLetter ([IO.Path]::GetPathRoot((Resolve-Path $l).Path)).Substring(0, 1) -ErrorAction Stop } catch { }
    }
    if (-not $WriteCounter) { Start-Sleep -Seconds 5; return }
    $quiet = 0
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($quiet -lt 4 -and $sw.Elapsed.TotalSeconds -lt 90) {
        Start-Sleep -Milliseconds 500
        if ($WriteCounter.NextValue() -lt 2MB) { $quiet++ } else { $quiet = 0 }
    }
}

function Measure-Tree([string]$dir) {
    $files = 0; $bytes = [long]0
    foreach ($f in (New-Object IO.DirectoryInfo $dir).EnumerateFiles('*', 'AllDirectories')) { $files++; $bytes += $f.Length }
    return @($files, $bytes)
}

# Runs robocopy the way FastCopy does: output read through a pipe (and discarded here).
function Invoke-Robocopy([string[]]$argList) {
    $psi = New-Object Diagnostics.ProcessStartInfo 'robocopy.exe'
    $psi.Arguments = ($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    [void]$p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    if ($p.ExitCode -ge 8) { throw "robocopy failed with exit code $($p.ExitCode)" }
}

$quiet = @('/E', '/R:0', '/W:0', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
$Methods = [ordered]@{
    'Windows Explorer engine'    = { param($s, $d) [Microsoft.VisualBasic.FileIO.FileSystem]::CopyDirectory($s, $d, [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs) }
    'PowerShell Copy-Item'       = { param($s, $d) Copy-Item -LiteralPath $s -Destination $d -Recurse }
    'robocopy (single-threaded)' = { param($s, $d) Invoke-Robocopy (@($s, $d) + $quiet) }
    'robocopy /MT:8'             = { param($s, $d) Invoke-Robocopy (@($s, $d) + $quiet + '/MT:8') }
    'robocopy /MT:32'            = { param($s, $d) Invoke-Robocopy (@($s, $d) + $quiet + '/MT:32') }
    'robocopy /MT:128'           = { param($s, $d) Invoke-Robocopy (@($s, $d) + $quiet + '/MT:128') }
    # FastCopy's exact command line: a scan pass (/L) for progress, then the copy.
    'FastCopy, 16 threads (incl. scan)' = {
        param($s, $d)
        $a = @($s, $d, '/E', '/R:1', '/W:1', '/XJ', '/XD', 'System Volume Information', '$RECYCLE.BIN', '/BYTES', '/FP', '/NDL', '/NP', '/NJH')
        Invoke-Robocopy ($a + '/L')
        Invoke-Robocopy ($a + '/MT:16')
    }
}

function Format-Bytes([double]$b) {
    if ($b -ge 1GB) { return '{0:N2} GB' -f ($b / 1GB) }
    if ($b -ge 1MB) { return '{0:N0} MB' -f ($b / 1MB) }
    return '{0:N0} KB' -f ($b / 1KB)
}

function Get-Median([double[]]$v) {
    $s = $v | Sort-Object
    if ($s.Count % 2) { return $s[[int][math]::Floor($s.Count / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}

function Get-DriveInfoText([string]$path) {
    try {
        $letter = ([IO.Path]::GetPathRoot((Resolve-Path $path).Path)).Substring(0, 1)
        $part = Get-Partition -DriveLetter $letter
        $disk = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq [string]$part.DiskNumber }
        $vol = Get-Volume -DriveLetter $letter
        return '{0}: {1} ({2}, {3}, {4})' -f $letter, $disk.FriendlyName, $disk.MediaType, $disk.BusType, $vol.FileSystem
    } catch { return $path }
}

# ---------- run ----------

[void][IO.Directory]::CreateDirectory($Target)
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$ram = '{0:N0} GB RAM' -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
$os = (Get-CimInstance Win32_OperatingSystem)
$srcInfo = Get-DriveInfoText $Work
$dstInfo = Get-DriveInfoText $Target

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine('# FastCopy benchmark results')
[void]$md.AppendLine()
[void]$md.AppendLine("- Date: $(Get-Date -Format 'yyyy-MM-dd')")
[void]$md.AppendLine("- PC: $cpu, $ram, $($os.Caption) (build $($os.BuildNumber))")
[void]$md.AppendLine("- From: $srcInfo")
[void]$md.AppendLine("- To: $dstInfo")
[void]$md.AppendLine("- Each time is the median of $Runs runs. Methods are interleaved run by run. Every copy is verified (file count + bytes).")
[void]$md.AppendLine('- Before every timed copy the write cache is flushed and the script waits until the disks are idle.')
[void]$md.AppendLine('- Time = until the tool reports it is done (like the moment a copy dialog closes). The source is read from a warm cache for every method.')
[void]$md.AppendLine()

foreach ($key in $Scenarios) {
    $w = $Workloads[$key]
    $total = [long]$w.Count * $w.Size
    $title = '{0} - {1:N0} x {2} = {3}' -f $w.Title, $w.Count, (Format-Bytes $w.Size), (Format-Bytes $total)
    Write-Host "`n== $title ==" -ForegroundColor Cyan
    Write-Host '  generating test data...'
    New-TestData $srcRoot $w.Count $w.Size

    # warm-up (fills the read cache so every method reads the source the same way)
    Remove-Dir $dst
    & $Methods['robocopy /MT:32'] $srcRoot $dst
    Remove-Dir $dst

    $times = @{}
    foreach ($m in $Methods.Keys) { $times[$m] = New-Object System.Collections.Generic.List[double] }
    for ($run = 1; $run -le $Runs; $run++) {
        foreach ($m in $Methods.Keys) {
            Remove-Dir $dst
            Wait-DiskIdle
            $sw = [Diagnostics.Stopwatch]::StartNew()
            & $Methods[$m] $srcRoot $dst
            $sw.Stop()
            $check = Measure-Tree $dst
            if ($check[0] -ne $w.Count -or $check[1] -ne $total) { throw "$m produced an incomplete copy ($($check[0]) files, $($check[1]) bytes)" }
            $times[$m].Add($sw.Elapsed.TotalSeconds)
            Write-Host ('  run {0}  {1,-36} {2,8:N2} s' -f $run, $m, $sw.Elapsed.TotalSeconds)
        }
    }
    Remove-Dir $dst

    $base = Get-Median $times['Windows Explorer engine']
    [void]$md.AppendLine("## $title")
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Method | Time (median) | Fastest - slowest run | Speed | Files/s | vs Explorer |')
    [void]$md.AppendLine('|---|---:|---:|---:|---:|---:|')
    foreach ($m in $Methods.Keys) {
        $t = Get-Median $times[$m]
        $lo = ($times[$m] | Measure-Object -Minimum).Minimum
        $hi = ($times[$m] | Measure-Object -Maximum).Maximum
        $name = if ($m -like 'FastCopy*') { "**$m**" } else { $m }
        [void]$md.AppendLine(('| {0} | {1:N2} s | {2:N2} - {3:N2} s | {4}/s | {5:N0} | {6:N1}x |' -f $name, $t, $lo, $hi, (Format-Bytes ($total / $t)), ($w.Count / $t), ($base / $t)))
    }
    [void]$md.AppendLine()
}
Remove-Dir $srcRoot

$letter = ([IO.Path]::GetPathRoot((Resolve-Path $Target).Path)).Substring(0, 1)
$outFile = Join-Path $PSScriptRoot "results-$letter.md"
[IO.File]::WriteAllText($outFile, $md.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "`n$($md.ToString())"
Write-Host "Saved to $outFile" -ForegroundColor Green
