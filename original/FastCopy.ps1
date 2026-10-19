# FastCopy - a GUI on top of robocopy's multi-threaded copy (/MT).
# Copyright (c) 2026 Jawad Hamza - https://github.com/jawad-hamza/fastcopy
# Free and open source under the MIT License (see LICENSE).
#
# This is the original PowerShell version. The portable FastCopy.exe is built
# from src\FastCopy.cs.
#
# Start it with FastCopy.bat (double-click), or:
#   powershell -STA -NoProfile -ExecutionPolicy Bypass -File FastCopy.ps1

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

# Robocopy runs as a child process; its stdout is collected on a background
# thread into a queue that the UI timer drains, so the window never freezes.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

public static class FcNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}

public class RoboRunner {
    public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    public Process Proc;

    public void Start(string args) {
        var psi = new ProcessStartInfo("robocopy.exe", args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // robocopy writes in the console (OEM) codepage when redirected
        var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        psi.StandardOutputEncoding = enc;
        psi.StandardErrorEncoding = enc;
        Proc = new Process();
        Proc.StartInfo = psi;
        Proc.OutputDataReceived += (s, e) => { if (e.Data != null) Lines.Enqueue(e.Data); };
        Proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Lines.Enqueue(e.Data); };
        Proc.Start();
        Proc.BeginOutputReadLine();
        Proc.BeginErrorReadLine();
    }

    // True once the process has exited AND all of its output has been read.
    public bool Finished() {
        if (Proc == null || !Proc.HasExited) return false;
        Proc.WaitForExit();
        return true;
    }

    public int ExitCode { get { return Proc.ExitCode; } }

    public void Kill() {
        try { if (Proc != null && !Proc.HasExited) Proc.Kill(); } catch { }
    }
}
'@

[void][FcNative]::SetProcessDPIAware()
[System.Windows.Forms.Application]::EnableVisualStyles()

$SettingsPath = Join-Path $env:APPDATA 'FastCopy\settings.json'

# "  New File    1002  C:\path\file.txt"  -> class, size, path
$FileLineRx = '^\s*(New File|Newer|Older|Changed|Tweaked|Modified|Same|\*EXTRA File)\s+(\d+)\s+(\S.*)$'
$Gray = [System.Drawing.Color]::Gray
$Red  = [System.Drawing.Color]::Firebrick

# ---------- helpers ----------

function Format-Bytes([double]$b) {
    if ($b -ge 1GB) { return '{0:N2} GB' -f ($b / 1GB) }
    if ($b -ge 1MB) { return '{0:N1} MB' -f ($b / 1MB) }
    if ($b -ge 1KB) { return '{0:N0} KB' -f ($b / 1KB) }
    return '{0:N0} B' -f $b
}

function Format-Time([double]$sec) {
    if ($sec -lt 0 -or [double]::IsInfinity($sec) -or [double]::IsNaN($sec)) { return '--:--' }
    $t = [TimeSpan]::FromSeconds([math]::Round($sec))
    if ($t.TotalHours -ge 1) { return '{0}:{1:mm\:ss}' -f [int][math]::Floor($t.TotalHours), $t }
    return $t.ToString('mm\:ss')
}

# A path ending in "\" would escape robocopy's closing quote ("E:\" -> "E:\.").
function Quote-Path([string]$p) {
    if ($p.EndsWith('\')) { $p += '.' }
    return '"' + $p + '"'
}

# "*.jpg; *.png" or "node_modules .git" or "\"My Folder\"" -> list of names
function Get-Tokens([string]$s) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($s, '"([^"]+)"|([^\s;,]+)')) {
        if ($m.Groups[1].Success) { $out.Add($m.Groups[1].Value.TrimEnd('\')) } else { $out.Add($m.Groups[2].Value.TrimEnd('\')) }
    }
    return $out.ToArray()
}

function Pick-Folder([System.Windows.Forms.TextBox]$box, [string]$title) {
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = $title
    $dlg.ShowNewFolderButton = $true
    if ($box.Text -and (Test-Path -LiteralPath $box.Text)) { $dlg.SelectedPath = $box.Text }
    if ($dlg.ShowDialog($form) -eq 'OK') { $box.Text = $dlg.SelectedPath }
}

function New-Ctl([string]$type, $parent, $x, $y, $w, $h, $text = $null, $anchor = 'Top,Left') {
    $c = New-Object "System.Windows.Forms.$type"
    $c.Location = New-Object System.Drawing.Point($x, $y)
    $c.Size = New-Object System.Drawing.Size($w, $h)
    $c.Anchor = $anchor
    if ($null -ne $text) { $c.Text = $text }
    if ($type -eq 'Label') { $c.AutoSize = $true }
    $parent.Controls.Add($c)
    return $c
}

function New-Num($parent, $x, $y, $w, $min, $max) {
    $n = New-Ctl 'NumericUpDown' $parent $x $y $w 26
    $n.Minimum = $min; $n.Maximum = $max
    $n.TextAlign = 'Center'
    return $n
}

# ---------- window ----------

$form = New-Object System.Windows.Forms.Form
$form.SuspendLayout()
$form.AutoScaleDimensions = New-Object System.Drawing.SizeF(96, 96)
$form.AutoScaleMode = 'Dpi'
$form.Text = 'FastCopy'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$form.ClientSize = New-Object System.Drawing.Size(740, 580)
$form.StartPosition = 'CenterScreen'
$form.Icon = [System.Drawing.SystemIcons]::Application
$form.Add_Shown({ $form.MinimumSize = $form.Size; Place-HelpMarks })

# The window is one 3-column table (label | stretch | button); only the tabs row
# grows, so DPI scaling and resizing can't make rows overlap.
$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = 'Fill'
$root.Padding = New-Object System.Windows.Forms.Padding(8)
$root.ColumnCount = 3
[void]$root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('AutoSize')))
[void]$root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('Percent', 100)))
[void]$root.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle('AutoSize')))
$form.Controls.Add($root)

$script:row = -1
function Next-Row([bool]$fill = $false) {
    $script:row++
    $style = if ($fill) { New-Object System.Windows.Forms.RowStyle('Percent', 100) } else { New-Object System.Windows.Forms.RowStyle('AutoSize') }
    [void]$root.RowStyles.Add($style)
}
function Put($ctl, [int]$col, [int]$span = 1) {
    $root.Controls.Add($ctl, $col, $script:row)
    if ($span -gt 1) { $root.SetColumnSpan($ctl, $span) }
    return $ctl
}
function Make([string]$type, $text = $null, $dock = 'Fill', $w = 0, $h = 0) {
    $c = New-Object "System.Windows.Forms.$type"
    if ($null -ne $text) { $c.Text = $text }
    if ($w) { $c.Size = New-Object System.Drawing.Size($w, $h) }
    $c.Dock = $dock
    if ($type -eq 'Label') { $c.AutoSize = $true; $c.TextAlign = 'MiddleLeft' }
    return $c
}

Next-Row
[void](Put (Make 'Label' 'From') 0)
$txtFrom = Put (Make 'TextBox') 1
$txtFrom.Name = 'txtFrom'
$txtFrom.Margin = New-Object System.Windows.Forms.Padding(3, 6, 3, 3)
$btnFrom = Put (Make 'Button' 'Browse...' 'None' 94 28) 2
Next-Row
[void](Put (Make 'Label' 'To') 0)
$txtTo = Put (Make 'TextBox') 1
$txtTo.Name = 'txtTo'
$txtTo.Margin = New-Object System.Windows.Forms.Padding(3, 6, 3, 3)
$btnTo = Put (Make 'Button' 'Browse...' 'None' 94 28) 2

Next-Row $true
$tabs = Put (Make 'TabControl') 0 3
$tabs.Margin = New-Object System.Windows.Forms.Padding(3, 8, 3, 6)
function New-Page([string]$title) {
    $p = New-Object System.Windows.Forms.TabPage
    $p.Text = $title
    $p.UseVisualStyleBackColor = $false
    $p.BackColor = [System.Drawing.Color]::White
    $tabs.TabPages.Add($p)
    return $p
}
$pgSpeed  = New-Page 'Speed'
$pgMode   = New-Page 'Mode'
$pgFilter = New-Page 'Filters'
$pgMore   = New-Page 'More'
$pgLog    = New-Page 'Log'

# Plain-English help: hover an option, or the blue "?" next to it.
$tips = New-Object System.Windows.Forms.ToolTip
$tips.AutoPopDelay = 30000; $tips.InitialDelay = 150; $tips.ReshowDelay = 50; $tips.ShowAlways = $true
$HelpMarks = New-Object System.Collections.Generic.List[object]

function Add-Help($ctl, [string]$text) {
    $tips.SetToolTip($ctl, $text)
    $q = New-Object System.Windows.Forms.Label
    $q.Text = '?'
    $q.AutoSize = $false
    $q.Size = New-Object System.Drawing.Size(16, 16)
    $q.TextAlign = 'MiddleCenter'
    $q.Font = New-Object System.Drawing.Font('Segoe UI', 7.5, [System.Drawing.FontStyle]::Bold)
    $q.ForeColor = [System.Drawing.Color]::White
    $q.BackColor = [System.Drawing.Color]::FromArgb(0, 103, 192)
    $q.Cursor = [System.Windows.Forms.Cursors]::Help
    $tips.SetToolTip($q, $text)
    $ctl.Parent.Controls.Add($q)
    $HelpMarks.Add(@($ctl, $q))
}

# AutoSize widths are only final after DPI scaling, so the marks are placed once shown.
function Place-HelpMarks {
    foreach ($pair in $HelpMarks) {
        $c = $pair[0]; $q = $pair[1]
        $q.Location = New-Object System.Drawing.Point(($c.Right + 3), ($c.Top + [int](($c.Height - $q.Height) / 2)))
        $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $gp.AddEllipse(0, 0, $q.Width, $q.Height)
        $q.Region = New-Object System.Drawing.Region($gp)
        $q.BringToFront()
    }
}

# --- Speed tab ---
$lblThreads = New-Ctl 'Label' $pgSpeed 12 12 0 0 'Parallel copies (threads)'
Add-Help $lblThreads ("How many files are copied at the same time.`n`n" +
    "Windows Explorer copies one file at a time, so folders with lots of small`n" +
    "files (photos, documents, code) crawl. More threads = more files moving at once.`n`n" +
    "Good starting points:`n" +
    "   USB pen drive / SD card:  8 - 32`n" +
    "   External SSD:  32 - 64`n" +
    "   Internal SSD / NVMe:  64 - 128`n`n" +
    "It can never beat the speed of the cable or the drive itself.`n" +
    "If raising it stops helping, you've hit that limit.")
$trkThreads = New-Ctl 'TrackBar' $pgSpeed 8 34 480 45
$trkThreads.Minimum = 1; $trkThreads.Maximum = 128
$trkThreads.TickFrequency = 8; $trkThreads.SmallChange = 1; $trkThreads.LargeChange = 8
$trkThreads.BackColor = [System.Drawing.Color]::White
$tips.SetToolTip($trkThreads, 'Drag to choose how many files are copied at the same time.')
$numThreads = New-Num $pgSpeed 498 38 70 1 128
$numThreads.Name = 'numThreads'
$tips.SetToolTip($numThreads, 'Type an exact number of threads (1 - 128).')
$lblThreadsNote = New-Ctl 'Label' $pgSpeed 576 41 0 0 ''
$lblThreadsNote.ForeColor = $Gray

$x = 12
foreach ($v in 1, 8, 16, 32, 64, 128) {
    $label = if ($v -eq 128) { 'MAX (128)' } else { "$v" }
    $w = if ($v -eq 128) { 92 } else { 52 }
    $b = New-Ctl 'Button' $pgSpeed $x 82 $w 28 $label
    $b.Tag = $v
    $b.Add_Click({ $numThreads.Value = [int]$this.Tag })
    $tip = switch ($v) {
        1   { '1 file at a time - same as Windows Explorer.' }
        128 { '128 is the most robocopy allows. Best for fast SSDs with huge numbers of small files.' }
        default { "Copy $v files at the same time." }
    }
    $tips.SetToolTip($b, $tip)
    $x += $w + 6
}
$hint = New-Ctl 'Label' $pgSpeed 12 118 0 0 ("How many files are copied at the same time. 1 = one by one, like Explorer.`n" +
    "Pen drives / SD cards: 8-32.  SSD or NVMe: 32-128.  The cable speed is still the ceiling.")
$hint.ForeColor = $Gray

$lblRetry = New-Ctl 'Label' $pgSpeed 12 172 0 0 'Retries'
Add-Help $lblRetry ("If a file can't be copied (for example it's open in another program),`n" +
    "how many times to try again before skipping it.`n`n" +
    "robocopy's own default is 1,000,000 retries, which looks like a freeze.`n" +
    "1 is a sensible choice.")
$numRetry = New-Num $pgSpeed 96 169 56 0 99
$lblWait = New-Ctl 'Label' $pgSpeed 176 172 0 0 'Wait (sec)'
Add-Help $lblWait 'How many seconds to wait before each retry.'
$numWait = New-Num $pgSpeed 270 169 56 0 600

$chkJ = New-Ctl 'CheckBox' $pgSpeed 12 204 0 0 'Unbuffered I/O (/J) - faster for very large files'
$chkJ.AutoSize = $true
Add-Help $chkJ ("Copies straight to the drive, skipping Windows' memory cache.`n`n" +
    "Turn ON for big files: videos, ISO images, backups of several GB.`n" +
    "Leave OFF for lots of small files (photos, documents) - it slows those down.")
$chkZ = New-Ctl 'CheckBox' $pgSpeed 12 230 0 0 'Restartable (/Z) - resumes mid-file after unplug, slower'
$chkZ.AutoSize = $true
Add-Help $chkZ ("If the drive gets unplugged halfway through a big file, the next run`n" +
    "continues that file where it stopped instead of starting it again.`n`n" +
    "Makes copying noticeably slower. Only worth it for huge files on a`n" +
    "loose cable or flaky connection. Normally leave it OFF.")

# --- Mode tab ---
$rbCopy = New-Ctl 'RadioButton' $pgMode 12 10 0 0 'Copy - add and update files, never delete anything'
$rbCopy.AutoSize = $true
Add-Help $rbCopy ("The safe choice.`n`n" +
    "Copies new and changed files into the destination.`n" +
    "Nothing is ever deleted - not in the source, not in the destination.")
$rbMirror = New-Ctl 'RadioButton' $pgMode 12 36 0 0 'Mirror - make the destination identical (DELETES extra files in destination)'
$rbMirror.AutoSize = $true
$rbMirror.ForeColor = $Red
Add-Help $rbMirror ("Makes the destination an exact copy of the source.`n`n" +
    "Files in the destination that are NOT in the source get DELETED.`n" +
    "Great for keeping a backup drive in sync - but if you pick the wrong`n" +
    "destination folder, its files will be removed. Double-check the To box.")
$rbMove = New-Ctl 'RadioButton' $pgMode 12 62 0 0 'Move - copy, then DELETE the originals from the source'
$rbMove.AutoSize = $true
$rbMove.ForeColor = $Red
Add-Help $rbMove ("Like Cut and Paste.`n`n" +
    "Each file is copied, then removed from the source.`n" +
    "When it finishes, the files only exist in the destination.")

$lblExisting = New-Ctl 'Label' $pgMode 12 100 0 0 'Files already in destination'
Add-Help $lblExisting ("What to do when a file with the same name is already in the destination:`n`n" +
    "Copy only if changed - skip files that are identical. Fastest when you`n" +
    "    copy the same folder again later (only the new stuff gets copied).`n" +
    "Only if source is newer - never replace a newer file in the destination.`n" +
    "Never touch existing - only add files that are missing.`n" +
    "Always overwrite - copy everything again, even identical files.")
$cmbExisting = New-Ctl 'ComboBox' $pgMode 230 96 380 26
$cmbExisting.DropDownStyle = 'DropDownList'
[void]$cmbExisting.Items.AddRange(@(
    'Copy only if changed (size or date differs)',
    'Copy only if source is newer (/XO)',
    'Never touch existing files, only add missing ones',
    'Always overwrite everything (/IS /IT)'))

$chkKeep = New-Ctl 'CheckBox' $pgMode 12 136 0 0 'Create the source folder inside the destination (D:\Photos -> E:\Photos)'
$chkKeep.AutoSize = $true
Add-Help $chkKeep ("ON:  copying D:\Photos to E:\ gives you E:\Photos\...`n" +
    "OFF: the files inside Photos go straight into E:\`n`n" +
    "ON is what Windows Explorer does when you drag a folder.")
$chkEmpty = New-Ctl 'CheckBox' $pgMode 12 162 0 0 'Copy empty folders too'
$chkEmpty.AutoSize = $true
Add-Help $chkEmpty 'Also create folders that have no files in them, so the folder structure matches exactly.'
$chkXJ = New-Ctl 'CheckBox' $pgMode 12 188 0 0 'Skip junctions / linked folders (prevents endless loops)'
$chkXJ.AutoSize = $true
Add-Help $chkXJ ("Some folders are really shortcuts pointing somewhere else (common inside`n" +
    "C:\Users). Following them can copy the same files twice or loop forever.`n`n" +
    "Leave this ON unless you know you need those links followed.")

# --- Filters tab ---
$lblOnly = New-Ctl 'Label' $pgFilter 12 15 0 0 'Only these files'
Add-Help $lblOnly ("Copy ONLY files whose names match. Everything else is skipped.`n`n" +
    "Examples:`n   *.jpg *.png       (only pictures)`n   *.mp4             (only videos)`n   report*.docx`n`n" +
    "* means 'anything'. Leave empty to copy all files.")
$txtOnly = New-Ctl 'TextBox' $pgFilter 150 12 520 24
$lblXF = New-Ctl 'Label' $pgFilter 12 47 0 0 'Exclude files'
Add-Help $lblXF ("Files to skip.`n`nExamples:`n   *.tmp  Thumbs.db  desktop.ini  *.log")
$txtXF = New-Ctl 'TextBox' $pgFilter 150 44 520 24
$lblXD = New-Ctl 'Label' $pgFilter 12 79 0 0 'Exclude folders'
Add-Help $lblXD ("Folders to skip, by name, wherever they appear inside the source.`n`n" +
    "Examples:`n   node_modules  .git  Cache  `"Old Backups`"")
$txtXD = New-Ctl 'TextBox' $pgFilter 150 76 520 24
$fh = New-Ctl 'Label' $pgFilter 150 104 0 0 'Separate with spaces or ;   Wildcards * ? work.   Quote names with spaces: "My Folder"'
$fh.ForeColor = $Gray

$lblMin = New-Ctl 'Label' $pgFilter 12 141 0 0 'Min size (KB)'
Add-Help $lblMin "Skip files smaller than this many KB.`n1024 KB = 1 MB.   0 = no minimum."
$numMin = New-Num $pgFilter 150 138 80 0 100000000
$lblMax = New-Ctl 'Label' $pgFilter 250 141 0 0 'Max size (MB)'
Add-Help $lblMax "Skip files bigger than this many MB.`n1024 MB = 1 GB.   0 = no maximum."
$numMax = New-Num $pgFilter 370 138 80 0 100000000
$lblAge = New-Ctl 'Label' $pgFilter 470 141 0 0 'Max age (days)'
Add-Help $lblAge "Only copy files changed in the last N days.`nExample: 7 = only this week's files.   0 = all files."
$numAge = New-Num $pgFilter 590 138 70 0 100000
$fh2 = New-Ctl 'Label' $pgFilter 150 170 0 0 '0 = no limit.'
$fh2.ForeColor = $Gray

# --- More tab ---
$lblExtra = New-Ctl 'Label' $pgMore 12 15 0 0 'Extra robocopy flags'
Add-Help $lblExtra ("For advanced users only - leave empty if unsure.`n`n" +
    "Any extra robocopy switches, added to the end of the command.`n" +
    "Open a terminal and run  robocopy /?  to see them all.")
$txtExtra = New-Ctl 'TextBox' $pgMore 180 12 490 24
$mh = New-Ctl 'Label' $pgMore 180 40 0 0 ("Added to the command as-is, e.g.  /COPY:DATS  /XA:H  /IPG:50`n" +
    "Avoid /NFL /NJS /QUIET /V - they break the progress display.")
$mh.ForeColor = $Gray
$lblDone = New-Ctl 'Label' $pgMore 12 93 0 0 'When finished'
Add-Help $lblDone 'What FastCopy should do after a copy finishes without errors.'
$cmbDone = New-Ctl 'ComboBox' $pgMore 180 89 260 26
$cmbDone.DropDownStyle = 'DropDownList'
[void]$cmbDone.Items.AddRange(@('Do nothing', 'Play a sound', 'Open the destination folder'))
$btnReset = New-Ctl 'Button' $pgMore 12 136 160 30 'Reset all options'
$tips.SetToolTip($btnReset, 'Put every option back to its default. Your From/To folders are kept.')

# --- Log tab ---
$logTable = New-Object System.Windows.Forms.TableLayoutPanel
$logTable.Dock = 'Fill'
$logTable.ColumnCount = 1
[void]$logTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('Percent', 100)))
[void]$logTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle('AutoSize')))
$pgLog.Controls.Add($logTable)
$log = Make 'RichTextBox'
$log.ReadOnly = $true
$log.Font = New-Object System.Drawing.Font('Consolas', 9)
$log.BackColor = [System.Drawing.Color]::White
$log.WordWrap = $false
$log.BorderStyle = 'None'
$logTable.Controls.Add($log, 0, 0)
$btnSaveLog = Make 'Button' 'Save log...' 'None' 110 28
$logTable.Controls.Add($btnSaveLog, 0, 1)

# --- bottom: command preview, actions, progress ---
Next-Row
[void](Put (Make 'Label' 'Command') 0)
$txtCmd = Put (Make 'TextBox') 1
$txtCmd.ReadOnly = $true
$txtCmd.Font = New-Object System.Drawing.Font('Consolas', 8.5)
$txtCmd.Margin = New-Object System.Windows.Forms.Padding(3, 6, 3, 3)
$btnCopyCmd = Put (Make 'Button' 'Copy cmd' 'None' 94 28) 2

Next-Row
$actions = Put (Make 'FlowLayoutPanel') 0 3
$actions.AutoSize = $true
$actions.WrapContents = $false
$actions.Margin = New-Object System.Windows.Forms.Padding(0, 6, 0, 4)
$btnStart = Make 'Button' 'Start copy' 'None' 150 38
$btnStart.Name = 'btnStart'
$btnStart.Font = New-Object System.Drawing.Font('Segoe UI', 9.5, [System.Drawing.FontStyle]::Bold)
$btnStop = Make 'Button' 'Stop' 'None' 90 38
$btnStop.Enabled = $false
$btnOpen = Make 'Button' 'Open destination' 'None' 140 38
$btnOpen.Enabled = $false
$btnAbout = Make 'Button' 'About' 'None' 90 38
$actions.Controls.AddRange(@($btnStart, $btnStop, $btnOpen, $btnAbout))

Next-Row
$bar = Put (Make 'ProgressBar' $null 'Fill' 100 20) 0 3
$bar.Maximum = 1000

function Add-StatLine([string]$text) {
    Next-Row
    $l = Put (Make 'Label' $text 'Fill' 100 22) 0 3
    $l.AutoSize = $false
    $l.AutoEllipsis = $true
    return $l
}
$lblStat1 = Add-StatLine 'Pick a source and a destination.'
$lblStat1.Font = New-Object System.Drawing.Font('Segoe UI', 9.5, [System.Drawing.FontStyle]::Bold)
$lblStat2 = Add-StatLine ''
$lblFile = Add-StatLine ''
$lblFile.ForeColor = $Gray

$tips.SetToolTip($txtFrom, 'The folder you want to copy. You can also drag a folder here from Explorer.')
$tips.SetToolTip($txtTo, 'Where the copy should go. Drag a folder here, or type a new folder name - it will be created.')
$tips.SetToolTip($btnFrom, 'Pick the folder to copy.')
$tips.SetToolTip($btnTo, 'Pick where to copy it.')
$tips.SetToolTip($txtCmd, 'The exact robocopy command FastCopy will run. Updates live as you change options.')
$tips.SetToolTip($btnCopyCmd, 'Copy the command to the clipboard, e.g. to reuse it in a script.')
$tips.SetToolTip($btnStart, 'Counts the files first (so progress and time-left are accurate), then copies.')
$tips.SetToolTip($btnStop, 'Stop right now. Press Start again later to continue - files already copied are skipped.')
$tips.SetToolTip($btnOpen, 'Open the destination folder in Explorer.')
$tips.SetToolTip($btnSaveLog, 'Save everything in the log to a text file.')

$form.ResumeLayout()

# ---------- options <-> command ----------

$OptionPages = $pgSpeed, $pgMode, $pgFilter, $pgMore

function Set-Defaults {
    $numThreads.Value = 16; $numRetry.Value = 1; $numWait.Value = 1
    $chkJ.Checked = $false; $chkZ.Checked = $false
    $rbCopy.Checked = $true; $cmbExisting.SelectedIndex = 0
    $chkKeep.Checked = $true; $chkEmpty.Checked = $true; $chkXJ.Checked = $true
    $txtOnly.Text = ''; $txtXF.Text = ''; $txtXD.Text = ''
    $numMin.Value = 0; $numMax.Value = 0; $numAge.Value = 0
    $txtExtra.Text = ''; $cmbDone.SelectedIndex = 0
}

function Get-Settings {
    [ordered]@{
        From = $txtFrom.Text; To = $txtTo.Text
        Threads = [int]$numThreads.Value; Retry = [int]$numRetry.Value; Wait = [int]$numWait.Value
        J = $chkJ.Checked; Z = $chkZ.Checked
        Mode = $(if ($rbMirror.Checked) { 'mirror' } elseif ($rbMove.Checked) { 'move' } else { 'copy' })
        Existing = $cmbExisting.SelectedIndex
        Keep = $chkKeep.Checked; Empty = $chkEmpty.Checked; XJ = $chkXJ.Checked
        Only = $txtOnly.Text; XF = $txtXF.Text; XD = $txtXD.Text
        MinKB = [double]$numMin.Value; MaxMB = [double]$numMax.Value; AgeDays = [int]$numAge.Value
        Extra = $txtExtra.Text; Done = $cmbDone.SelectedIndex
    }
}

function Save-Settings {
    try {
        [void](New-Item -ItemType Directory -Force (Split-Path $SettingsPath))
        Get-Settings | ConvertTo-Json | Set-Content -LiteralPath $SettingsPath -Encoding UTF8
    } catch { }
}

function Load-Settings {
    if (-not (Test-Path -LiteralPath $SettingsPath)) { return }
    try { $o = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json } catch { return }
    function Clamp($n, $v) { if ($null -ne $v) { $n.Value = [math]::Max($n.Minimum, [math]::Min($n.Maximum, [decimal]$v)) } }
    if ($o.From) { $txtFrom.Text = $o.From }
    if ($o.To) { $txtTo.Text = $o.To }
    Clamp $numThreads $o.Threads; Clamp $numRetry $o.Retry; Clamp $numWait $o.Wait
    $chkJ.Checked = [bool]$o.J; $chkZ.Checked = [bool]$o.Z
    switch ($o.Mode) { 'mirror' { $rbMirror.Checked = $true } 'move' { $rbMove.Checked = $true } default { $rbCopy.Checked = $true } }
    if ($o.Existing -ge 0 -and $o.Existing -lt $cmbExisting.Items.Count) { $cmbExisting.SelectedIndex = $o.Existing }
    if ($null -ne $o.Keep) { $chkKeep.Checked = [bool]$o.Keep }
    if ($null -ne $o.Empty) { $chkEmpty.Checked = [bool]$o.Empty }
    if ($null -ne $o.XJ) { $chkXJ.Checked = [bool]$o.XJ }
    $txtOnly.Text = [string]$o.Only; $txtXF.Text = [string]$o.XF; $txtXD.Text = [string]$o.XD
    Clamp $numMin $o.MinKB; Clamp $numMax $o.MaxMB; Clamp $numAge $o.AgeDays
    $txtExtra.Text = [string]$o.Extra
    if ($o.Done -ge 0 -and $o.Done -lt $cmbDone.Items.Count) { $cmbDone.SelectedIndex = $o.Done }
}

# Builds the robocopy arguments from the current options.
function Build-Job {
    $src = $txtFrom.Text.Trim().Trim('"')
    $dst = $txtTo.Text.Trim().Trim('"')
    try { if ($src) { $src = [IO.Path]::GetFullPath($src) } } catch { }
    try { if ($dst) { $dst = [IO.Path]::GetFullPath($dst) } } catch { }
    if ($dst -and $src -and $chkKeep.Checked) {
        $leaf = [IO.Path]::GetFileName($src.TrimEnd('\'))
        if ($leaf) { $dst = Join-Path $dst $leaf }
    }

    $a = New-Object System.Collections.Generic.List[string]
    $a.Add((Quote-Path $(if ($src) { $src } else { '<from>' })))
    $a.Add((Quote-Path $(if ($dst) { $dst } else { '<to>' })))
    foreach ($t in @(Get-Tokens $txtOnly.Text)) { $a.Add('"' + $t + '"') }

    if ($rbMirror.Checked) { $a.Add('/MIR') } elseif ($chkEmpty.Checked) { $a.Add('/E') } else { $a.Add('/S') }
    if ($rbMove.Checked) { $a.Add('/MOVE') }
    switch ($cmbExisting.SelectedIndex) {
        1 { $a.Add('/XO') }
        2 { $a.Add('/XC'); $a.Add('/XN'); $a.Add('/XO') }
        3 { $a.Add('/IS'); $a.Add('/IT') }
    }
    $a.Add("/R:$([int]$numRetry.Value)"); $a.Add("/W:$([int]$numWait.Value)")
    if ($chkJ.Checked) { $a.Add('/J') }
    if ($chkZ.Checked) { $a.Add('/Z') }
    if ($chkXJ.Checked) { $a.Add('/XJ') }

    $xf = @(Get-Tokens $txtXF.Text)
    if ($xf.Count) { $a.Add('/XF'); foreach ($t in $xf) { $a.Add('"' + $t + '"') } }
    $a.Add('/XD'); $a.Add('"System Volume Information"'); $a.Add('"$RECYCLE.BIN"')
    foreach ($t in @(Get-Tokens $txtXD.Text)) { $a.Add('"' + $t + '"') }

    if ($numMin.Value -gt 0) { $a.Add('/MIN:' + [int64]($numMin.Value * 1KB)) }
    if ($numMax.Value -gt 0) { $a.Add('/MAX:' + [int64]($numMax.Value * 1MB)) }
    if ($numAge.Value -gt 0) { $a.Add('/MAXAGE:' + [int]$numAge.Value) }

    $a.Add('/BYTES /FP /NDL /NP /NJH')   # output format the progress parser relies on
    if ($txtExtra.Text.Trim()) { $a.Add($txtExtra.Text.Trim()) }

    $base = $a -join ' '
    return @{ Src = $src; Dst = $dst; Base = $base; Threads = [int]$numThreads.Value
              Preview = "robocopy $base /MT:$([int]$numThreads.Value)" }
}

function Update-Preview {
    $txtCmd.Text = (Build-Job).Preview
    $chkEmpty.Enabled = -not $rbMirror.Checked -and -not $btnStop.Enabled
    $v = [int]$numThreads.Value
    $lblThreadsNote.Text = if ($v -eq 1) { 'one by one' } elseif ($v -eq 128) { 'maximum' } else { '' }
}

# ---------- events ----------

foreach ($box in $txtFrom, $txtTo) {
    $box.AllowDrop = $true
    $box.Add_DragEnter({ if ($_.Data.GetDataPresent('FileDrop')) { $_.Effect = 'Copy' } })
    $box.Add_DragDrop({
        $p = @($_.Data.GetData('FileDrop'))[0]
        if (Test-Path -LiteralPath $p -PathType Leaf) { $p = Split-Path -LiteralPath $p -Parent }
        $this.Text = $p
    })
}
$btnFrom.Add_Click({ Pick-Folder $txtFrom 'Copy FROM this folder' })
$btnTo.Add_Click({ Pick-Folder $txtTo 'Copy TO this folder' })

$trkThreads.Add_ValueChanged({ $numThreads.Value = $trkThreads.Value })
$numThreads.Add_ValueChanged({ $trkThreads.Value = [int]$numThreads.Value; Update-Preview })

foreach ($c in $txtFrom, $txtTo, $txtOnly, $txtXF, $txtXD, $txtExtra) { $c.Add_TextChanged({ Update-Preview }) }
foreach ($c in $numRetry, $numWait, $numMin, $numMax, $numAge) { $c.Add_ValueChanged({ Update-Preview }) }
foreach ($c in $chkJ, $chkZ, $chkKeep, $chkEmpty, $chkXJ, $rbCopy, $rbMirror, $rbMove) { $c.Add_CheckedChanged({ Update-Preview }) }
$cmbExisting.Add_SelectedIndexChanged({ Update-Preview })

$btnReset.Add_Click({ Set-Defaults; Update-Preview })
$btnCopyCmd.Add_Click({ [System.Windows.Forms.Clipboard]::SetText($txtCmd.Text) })

$btnSaveLog.Add_Click({
    $dlg = New-Object System.Windows.Forms.SaveFileDialog
    $dlg.Filter = 'Text file (*.txt)|*.txt'
    $dlg.FileName = 'FastCopy-log-{0:yyyyMMdd-HHmmss}.txt' -f (Get-Date)
    if ($dlg.ShowDialog($form) -eq 'OK') { [IO.File]::WriteAllText($dlg.FileName, $log.Text, [Text.Encoding]::UTF8) }
})

function Write-Log([string]$text, $color = [System.Drawing.Color]::Black) {
    $log.SelectionStart = $log.TextLength
    $log.SelectionLength = 0
    $log.SelectionColor = $color
    $log.AppendText($text + "`r`n")
    $log.ScrollToCaret()
}

# ---------- copy job ----------

$S = @{}   # job state, shared with the timer
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 250

function Set-Busy([bool]$busy) {
    foreach ($c in $txtFrom, $txtTo, $btnFrom, $btnTo, $btnStart, $btnReset) { $c.Enabled = -not $busy }
    foreach ($p in $OptionPages) { foreach ($c in $p.Controls) { $c.Enabled = -not $busy } }
    $btnStop.Enabled = $busy
    if (-not $busy) { Update-Preview }
}

function Start-Phase([string]$phase) {
    $S.Phase = $phase
    $S.Runner = New-Object RoboRunner
    $a = $S.Base
    if ($phase -eq 'scan') { $a += ' /L' } else { $a += " /MT:$($S.Threads)" }
    $S.Runner.Start($a)
}

function Finish-Job([string]$msg, $color, [bool]$success = $false) {
    $timer.Stop()
    Set-Busy $false
    $lblStat1.Text = $msg
    $lblFile.Text = ''
    Write-Log $msg $color
    $btnOpen.Enabled = Test-Path -LiteralPath $S.Dst
    if ($success) {
        switch ($cmbDone.SelectedIndex) {
            1 { [System.Media.SystemSounds]::Asterisk.Play() }
            2 { if ($btnOpen.Enabled) { Start-Process explorer.exe -ArgumentList ('"' + $S.Dst + '"') } }
        }
    }
}

function Ask([string]$text, [string]$title) {
    return [System.Windows.Forms.MessageBox]::Show($form, $text, $title, 'YesNo', 'Warning', 'Button2') -eq 'Yes'
}
function Warn([string]$text) {
    [void][System.Windows.Forms.MessageBox]::Show($form, $text, 'FastCopy', 'OK', 'Warning')
}

$btnStart.Add_Click({
    $j = Build-Job
    if (-not $j.Src -or -not (Test-Path -LiteralPath $j.Src -PathType Container)) { Warn 'The source folder does not exist.'; return }
    if (-not $j.Dst) { Warn 'Choose a destination folder.'; return }
    $srcK = $j.Src.TrimEnd('\') + '\'
    $dstK = $j.Dst.TrimEnd('\') + '\'
    if ($dstK.StartsWith($srcK, 'OrdinalIgnoreCase')) { Warn 'The destination is inside the source folder. Pick a different destination.'; return }
    if ($srcK.StartsWith($dstK, 'OrdinalIgnoreCase') -and $rbMirror.Checked) { Warn 'Mirroring into a parent of the source would delete the source. Pick a different destination.'; return }
    if ($rbMirror.Checked -and -not (Ask "Mirror mode will DELETE files in:`n$($j.Dst)`nthat do not exist in the source.`n`nContinue?" 'Confirm mirror')) { return }
    if ($rbMove.Checked -and -not (Ask "Move mode will DELETE the original files from:`n$($j.Src)`nafter they are copied.`n`nContinue?" 'Confirm move')) { return }

    Save-Settings
    $S.Clear()
    $S.Src = $j.Src; $S.Dst = $j.Dst; $S.Base = $j.Base; $S.Threads = $j.Threads
    $S.TotalFiles = 0; $S.TotalBytes = [double]0; $S.Extras = 0
    $S.DoneFiles = 0;  $S.DoneBytes = [double]0
    $S.Errors = New-Object 'System.Collections.Generic.HashSet[string]'
    $S.InSummary = $false
    $S.Samples = New-Object System.Collections.Generic.Queue[object]

    $log.Clear(); $bar.Value = 0; $lblStat2.Text = ''; $btnOpen.Enabled = $false
    $tabs.SelectedTab = $pgLog
    Write-Log "From: $($j.Src)"
    Write-Log "To:   $($j.Dst)"
    Write-Log $j.Preview $Gray
    $lblStat1.Text = 'Scanning...'
    Set-Busy $true
    Start-Phase 'scan'
    $S.Clock = [Diagnostics.Stopwatch]::StartNew()
    $timer.Start()
})

$btnStop.Add_Click({
    $S.Stopped = $true
    if ($S.Runner) { $S.Runner.Kill() }
})

$btnOpen.Add_Click({ if (Test-Path -LiteralPath $S.Dst) { Start-Process explorer.exe -ArgumentList ('"' + $S.Dst + '"') } })

$btnAbout.Add_Click({
    [void][System.Windows.Forms.MessageBox]::Show($form,
        ("FastCopy - fast multi-threaded file copier for Windows`n`n" +
         "Created by Jawad Hamza`nhttps://github.com/jawad-hamza/fastcopy`n`n" +
         "Free and open source under the MIT License - free for everyone, personal or commercial use. " +
         "You can share and modify it; please keep the credit to the author.`n`n" +
         "The copying itself is done by robocopy, which is built into Windows."),
        'About FastCopy', 'OK', 'Information')
})

$form.Add_FormClosing({
    if ($S.Runner -and $btnStop.Enabled) {
        if (-not (Ask 'A copy is running. Stop it and close?' 'FastCopy')) { $_.Cancel = $true; return }
        $S.Runner.Kill()
    }
    Save-Settings
})

$timer.Add_Tick({
    $r = $S.Runner
    $finished = $r.Finished()   # check before draining so no lines are missed
    $line = $null
    $lastPath = $null
    $n = 0
    while ($n -lt 20000 -and $r.Lines.TryDequeue([ref]$line)) {
        $n++
        if ($line -match $FileLineRx) {
            $isExtra = $Matches[1] -eq '*EXTRA File'
            if ($S.Phase -eq 'scan') {
                if ($isExtra) { $S.Extras++ } else { $S.TotalFiles++; $S.TotalBytes += [double]$Matches[2] }
            } elseif (-not $isExtra) {
                $S.DoneFiles++; $S.DoneBytes += [double]$Matches[2]; $lastPath = $Matches[3]
            }
            continue
        }
        if ($S.Phase -ne 'copy' -or -not $line.Trim()) { continue }
        if ($line -match '^-{20,}') { $S.InSummary = $true; Write-Log ''; continue }
        if ($line -match 'ERROR \d+ \(0x[0-9A-Fa-f]+\)\s+(.*)$') {
            [void]$S.Errors.Add($Matches[1])
            Write-Log $line.Trim() $Red
        } elseif ($S.InSummary) {
            Write-Log $line
        } else {
            Write-Log $line.Trim() ([System.Drawing.Color]::DimGray)
        }
    }
    if (-not $r.Lines.IsEmpty) { $finished = $false }   # backlog left for the next tick

    $elapsed = $S.Clock.Elapsed.TotalSeconds
    if ($S.Phase -eq 'scan') {
        $lblStat1.Text = 'Scanning... {0:N0} files to copy ({1})' -f $S.TotalFiles, (Format-Bytes $S.TotalBytes)
        if (-not $finished) { return }
        if ($S.Stopped) { Finish-Job 'Stopped.' ([System.Drawing.Color]::DarkOrange); return }
        if ($S.TotalFiles -eq 0 -and $S.Extras -eq 0) {
            $bar.Value = 1000
            Finish-Job 'Nothing to copy - the destination is already up to date.' ([System.Drawing.Color]::DarkGreen) $true; return
        }
        Write-Log ('Scan: {0:N0} files, {1} to copy. Copying with {2} threads...' -f $S.TotalFiles, (Format-Bytes $S.TotalBytes), $S.Threads)
        Start-Phase 'copy'
        $S.Clock.Restart()
        return
    }

    # copy phase: speed is averaged over the last ~3 seconds
    $S.Samples.Enqueue(@($elapsed, $S.DoneBytes, $S.DoneFiles))
    while ($S.Samples.Count -gt 1 -and ($elapsed - $S.Samples.Peek()[0]) -gt 3) { [void]$S.Samples.Dequeue() }
    $first = $S.Samples.Peek()
    $span = $elapsed - $first[0]
    if ($span -ge 0.5) {
        $bps = ($S.DoneBytes - $first[1]) / $span
        $fps = ($S.DoneFiles - $first[2]) / $span
    } else { $bps = 0; $fps = 0 }

    $frac = if ($S.TotalBytes -gt 0) { $S.DoneBytes / $S.TotalBytes } elseif ($S.TotalFiles -gt 0) { $S.DoneFiles / $S.TotalFiles } else { 1 }
    $frac = [math]::Min(1, $frac)
    $bar.Value = [int]($frac * 1000)
    $eta = if ($bps -gt 0) { ($S.TotalBytes - $S.DoneBytes) / $bps } else { -1 }

    $lblStat1.Text = '{0:N0} / {1:N0} files   {2} / {3}   ({4:N0}%)' -f $S.DoneFiles, $S.TotalFiles,
        (Format-Bytes $S.DoneBytes), (Format-Bytes $S.TotalBytes), ($frac * 100)
    $lblStat2.Text = '{0}/s   {1:N0} files/s   {2} threads   elapsed {3}   left {4}   errors {5}' -f (Format-Bytes $bps), $fps,
        $S.Threads, (Format-Time $elapsed), (Format-Time $eta), $S.Errors.Count
    if ($lastPath) { $lblFile.Text = $lastPath }

    if (-not $finished) { return }

    $code = $r.ExitCode
    $avg = if ($elapsed -gt 0) { $S.DoneBytes / $elapsed } else { 0 }
    $lblStat2.Text = 'Average {0}/s   {1:N0} files in {2}   {3} threads   errors {4}' -f (Format-Bytes $avg), $S.DoneFiles,
        (Format-Time $elapsed), $S.Threads, $S.Errors.Count
    if ($S.Stopped) {
        Finish-Job 'Stopped. Start again to resume - finished files are skipped.' ([System.Drawing.Color]::DarkOrange)
    } elseif ($code -ge 8) {
        Finish-Job ('Finished with errors: {0} file(s) could not be copied (see Log tab).' -f [math]::Max(1, $S.Errors.Count)) $Red
    } else {
        $bar.Value = 1000
        Finish-Job ('Done. {0:N0} files copied ({1}).' -f $S.DoneFiles, (Format-Bytes $S.DoneBytes)) ([System.Drawing.Color]::DarkGreen) $true
    }
})

Set-Defaults
Load-Settings
Update-Preview
[void]$form.ShowDialog()
$timer.Dispose()
$form.Dispose()
