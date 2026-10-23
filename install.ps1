<#
    install.ps1 - install Gloam from a release package, update an installation in
    place, or remove one.

    INSTALL, from an extracted package (Gloam-X.Y.Z.zip, unzipped):

      powershell -ExecutionPolicy Bypass -File install.ps1 [-InstallDir <dir>] [-NoShortcut] [-Force] [-Launch]

      Copies the package into -InstallDir - by default %LOCALAPPDATA%\Programs\Gloam -
      and puts a "Gloam" shortcut in the Start menu. Run it again from a newer
      package to update the same folder; nothing outside the package's own files is
      touched.

    UPDATE, from a downloaded release zip - what the tray's "Check for Updates"
    runs, from the installation it is updating:

      install.ps1 -Package <zip> -Sha256 <hex> [-DeletePackage] [-WaitPid <pid>] [-Launch]

      Verifies the zip against -Sha256, unzips it, and hands over to THE NEW
      VERSION'S install.ps1 with -InstallDir set to the folder this script is in -
      so an update always lands where the installation already is, and the Start
      with Windows registration and the shortcut never need repointing.
      -DeletePackage removes the zip once it has been copied for checking: Gloam
      downloaded it for this and nothing else.

    UNINSTALL:

      install.ps1 -Uninstall [-InstallDir <dir>]

      Deletes the package's own files, the shortcut, and the Start with Windows
      registration when it points into the installation. What Gloam wrote there
      itself is left, and so is %LOCALAPPDATA%\Gloam (settings, calibration
      reports, logs), which is the user's.

    Common: -WaitPid waits (up to 30s) for that process to exit first - the Gloam
    that asked for the update; -Launch starts Gloam.exe from the installation
    afterwards, and when it asks, a failure is also shown in a timed message box
    (the console is hidden then) unless -Quiet. Every decision goes to -LogFile,
    %TEMP%\Gloam-install.log by default. Exit codes: 0 done, 1 failed, 2 refused
    (bad arguments, a folder this will not write into, or Gloam still running
    from it).

    WHICH FILES ARE ITS OWN. The package's manifest.txt lists every file in it.
    An install copies exactly those, and deletes a file the PREVIOUS install's
    manifest listed and this one does not (a file a release dropped). Nothing else
    in the folder is touched, ever. For the same reason a folder that is not empty
    and holds no manifest.txt - a checkout, Documents - is refused unless -Force.

    HOW AN UPDATE LANDS. Every new file is copied into a staging folder inside the
    installation first, so a failed copy changes nothing; then each one is moved
    into place, the old one renamed aside rather than overwritten - a loaded DLL
    can be renamed but not replaced - and a failure part-way puts back every file
    already moved. Gloam itself must not be running from the folder (the update
    waits for it to exit), or the old process would keep its old files while the
    new ones land.

    This file is read by Windows PowerShell 5.1 in the ANSI codepage: keep it
    ASCII.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Uninstall')]
    [string] $InstallDir = '',

    [Parameter(ParameterSetName = 'Install')] [switch] $NoShortcut,
    [Parameter(ParameterSetName = 'Install')] [switch] $Force,

    [Parameter(ParameterSetName = 'Package', Mandatory = $true)] [string] $Package,
    [Parameter(ParameterSetName = 'Package', Mandatory = $true)] [string] $Sha256,
    [Parameter(ParameterSetName = 'Package')] [switch] $DeletePackage,

    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Package')] [int] $WaitPid = 0,
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'Package')] [switch] $Launch,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory = $true)] [switch] $Uninstall,

    [string] $LogFile = '',
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$Self       = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$DefaultDir = Join-Path $env:LOCALAPPDATA 'Programs\Gloam'
$Shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) 'Gloam.lnk'
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue   = 'Gloam'          # StartupManager.AppName
$AppExe     = 'Gloam.exe'
$OldSuffix  = '.gloam-old'
if (-not $LogFile) { $LogFile = Join-Path $env:TEMP 'Gloam-install.log' }

# ---- saying things ---------------------------------------------------------
function Log([string]$msg) {
    $line = '{0} [install {1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $PID, $msg
    for ($i = 0; $i -lt 4; $i++) {
        try { [IO.File]::AppendAllText($LogFile, "$line`r`n", (New-Object Text.UTF8Encoding($false))); return }
        catch [IO.IOException] { Start-Sleep -Milliseconds 20 }
        catch { return }
    }
}

function Say([string]$msg) { Log $msg; Write-Host $msg }

# A refusal or a failure: logged, printed, and - when Gloam started this with its
# console hidden - shown in a timed box. Then Gloam comes back, on whatever
# version is installed now, so asking for an update never leaves the user
# without one.
function Fail([string]$msg, [int]$code = 1) {
    Log "FAILED: $msg"
    Write-Host "install.ps1: $msg" -ForegroundColor Red
    if ($Launch -and -not $Quiet) {
        try { [void](New-Object -ComObject WScript.Shell).Popup("The update was not installed:`n`n$msg", 15, 'Gloam', 16) } catch {}
    }
    if ($Launch -and $script:Target) { Wait-ForPid; Start-App $script:Target }
    exit $code
}

function Start-App([string]$dir) {
    $exe = Join-Path $dir $AppExe
    if (-not (Test-Path -LiteralPath $exe)) {
        Log "cannot start $exe again: it is not there"
        return
    }
    Start-Process -FilePath $exe -WorkingDirectory $dir | Out-Null
    Log "started $exe"
}

# ---- paths -----------------------------------------------------------------
function Full([string]$path) { return [IO.Path]::GetFullPath($path).TrimEnd('\') }

function Test-Inside([string]$inner, [string]$outer) {
    return ($inner -eq $outer) -or $inner.StartsWith("$outer\", [StringComparison]::OrdinalIgnoreCase)
}

# A manifest's lines, each a relative path that stays inside the folder. Anything
# else in one - rooted, "..", a drive - is refused rather than skipped: it would
# be a file this script deletes or overwrites outside the installation.
function Read-Manifest([string]$dir) {
    $file = Join-Path $dir 'manifest.txt'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in [IO.File]::ReadAllLines($file)) {
        $rel = $line.Trim() -replace '/', '\'
        if (-not $rel) { continue }
        if ([IO.Path]::IsPathRooted($rel) -or $rel -match '(^|\\)\.\.(\\|$)|:' -or $rel -ieq 'manifest.txt') {
            throw "$file lists '$line', which is not a file inside the package"
        }
        $out.Add($rel)
    }
    return ,$out
}

function Wait-ForPid() {
    if (-not $WaitPid) { return }
    $p = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
    if (-not $p) { return }
    Log "waiting for process $WaitPid to exit"
    if (-not $p.WaitForExit(30000)) { Log "process $WaitPid is still running after 30s" }
}

# Every process whose executable is inside $dir: Gloam itself, or a bundled
# ArgyllCMS tool it started (spotread holding a colorimeter).
function Get-RunningFrom([string]$dir) {
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
             Where-Object { $_.ExecutablePath -and (Test-Inside $_.ExecutablePath $dir) -and $_.ProcessId -ne $PID })
}

function Assert-NotRunning([string]$dir) {
    $running = Get-RunningFrom $dir
    if ($running.Count -gt 0) {
        Fail ("Gloam is running from $dir - exit it first (tray icon -> Exit):`n" +
              (($running | ForEach-Object { "  $($_.ProcessId)  $($_.ExecutablePath)" }) -join "`n")) 2
    }
}

# The Run value's executable, quotes and arguments stripped - StartupManager's
# ExtractExePath.
function Get-RunTarget() {
    try { $v = (Get-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction Stop).$RunValue } catch { return $null }
    if (-not $v) { return $null }
    $v = $v.Trim()
    if ($v.StartsWith('"')) {
        $end = $v.IndexOf('"', 1)
        if ($end -gt 1) { return $v.Substring(1, $end - 1).Trim() }
        return $v.Trim('"').Trim()
    }
    return $v
}

# Every empty folder under $root, deepest first; $root itself too when $andRoot.
function Remove-EmptyDirs([string]$root, [bool]$andRoot) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    $dirs = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($d in $dirs) {
        if (-not (Get-ChildItem -LiteralPath $d.FullName -Force)) { Remove-Item -LiteralPath $d.FullName -Force }
    }
    if ($andRoot -and -not (Get-ChildItem -LiteralPath $root -Force)) { Remove-Item -LiteralPath $root -Force }
}

# ============================================================================
# UNINSTALL
# ============================================================================
function Invoke-Uninstall() {
    $target = Full $(if ($InstallDir) { $InstallDir } else { $DefaultDir })
    $files = Read-Manifest $target
    if ($null -eq $files) { Fail "$target is not an installation (it has no manifest.txt)" 2 }
    Assert-NotRunning $target

    foreach ($rel in @($files) + 'manifest.txt') {
        Remove-Item -LiteralPath (Join-Path $target $rel) -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem -LiteralPath $target -Recurse -Force -Filter "*$OldSuffix*" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-EmptyDirs $target $true

    if (Test-Path -LiteralPath $Shortcut) {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
        if (Test-Inside $s.TargetPath $target) { Remove-Item -LiteralPath $Shortcut -Force }
    }
    $run = Get-RunTarget
    if ($run -and (Test-Inside $run $target)) {
        Remove-ItemProperty -LiteralPath $RunKey -Name $RunValue -ErrorAction SilentlyContinue
        Log "removed the Start with Windows registration ($run)"
    }

    Say "uninstalled Gloam from $target"
    if (Test-Path -LiteralPath $target) {
        $left = @(Get-ChildItem -LiteralPath $target -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($target.Length + 1) })
        Say "left in place - written there by Gloam, not by the installer: $($left -join ', ')"
    }
    exit 0
}

# ============================================================================
# UPDATE FROM A ZIP: verify, unzip, hand over to the new version's installer
# ============================================================================
function Invoke-Package() {
    $script:Target = $Self      # what Fail starts again: this installation, as it is
    Log "update of $Self from $Package requested"
    if ($null -eq (Read-Manifest $Self)) { Fail "$Self is not an installation (it has no manifest.txt)" 2 }
    if ($Sha256 -notmatch '^[0-9A-Fa-f]{64}$') { Fail "-Sha256 '$Sha256' is not a SHA-256" 2 }
    if (-not (Test-Path -LiteralPath $Package)) { Fail "there is no $Package" }

    # Copied before it is checked, and the COPY is what is checked and unzipped:
    # the download sits in a folder the user's own programs can write to.
    $work = Join-Path $env:TEMP ('Gloam.Update-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $zip = Join-Path $work 'package.zip'
        Copy-Item -LiteralPath $Package -Destination $zip
        if ($DeletePackage) { Remove-Item -LiteralPath $Package -Force -ErrorAction SilentlyContinue }
        $got = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        if ($got -ne $Sha256.ToUpperInvariant()) {
            Fail "the package's SHA-256 is $($got.ToLowerInvariant()), not $($Sha256.ToLowerInvariant())"
        }
        $pkg = Join-Path $work 'package'
        # Not Expand-Archive: Windows PowerShell's takes ~7s over this package's ~850 files.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $pkg)
        foreach ($f in 'install.ps1', 'manifest.txt', 'VERSION', $AppExe) {
            if (-not (Test-Path -LiteralPath (Join-Path $pkg $f))) { Fail "the package has no $f" }
        }

        # The NEW installer does the install: it is the one that knows the new layout.
        $ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$pkg\install.ps1`"",
               '-InstallDir', "`"$Self`"", '-NoShortcut', '-WaitPid', $WaitPid,
               '-LogFile', "`"$LogFile`"")
        if ($Launch) { $a += '-Launch' }
        if ($Quiet) { $a += '-Quiet' }
        Log "handing over to the package's install.ps1 ($((Get-Content -LiteralPath (Join-Path $pkg 'VERSION') -TotalCount 1).Trim()))"
        # Waited on as ONE process: Windows PowerShell's -Wait also waits for every
        # descendant, and the new installer's last act is to start Gloam again.
        $p = Start-Process -FilePath $ps -ArgumentList $a -PassThru -NoNewWindow
        $null = $p.Handle                       # what makes ExitCode readable afterwards
        $p.WaitForExit()
        exit $p.ExitCode
    } catch {
        Fail $_.Exception.Message
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ============================================================================
# INSTALL: this package into a folder
# ============================================================================
function Invoke-Install() {
    $target = Full $(if ($InstallDir) { $InstallDir } else { $DefaultDir })
    $old = $null
    if (Test-Path -LiteralPath $target) { $old = Read-Manifest $target }
    $script:Target = $(if ($null -ne $old) { $target })      # Fail starts again only what was there

    $files = Read-Manifest $Self
    if ($null -eq $files -or -not (Test-Path -LiteralPath (Join-Path $Self 'VERSION'))) {
        Fail "$Self is not a package (no manifest.txt or VERSION) - build one with package.ps1, or unzip a release" 2
    }
    foreach ($rel in $files) {
        if (-not (Test-Path -LiteralPath (Join-Path $Self $rel) -PathType Leaf)) { Fail "the package is missing $rel" 2 }
    }
    $version = (Get-Content -LiteralPath (Join-Path $Self 'VERSION') -TotalCount 1).Trim()
    if ((Test-Inside $target $Self) -or (Test-Inside $Self $target)) {
        Fail "cannot install into $target from $Self - one is inside the other" 2
    }
    if ($null -eq $old -and (Test-Path -LiteralPath $target) -and (Get-ChildItem -LiteralPath $target -Force) -and -not $Force) {
        Fail "$target is not empty and is not an installation (no manifest.txt) - pass -Force to install into it anyway" 2
    }
    $from = ''
    try { $from = (Get-Content -LiteralPath (Join-Path $target 'VERSION') -TotalCount 1 -ErrorAction Stop).Trim() } catch {}
    Log "installing $version into $target$(if ($from) { " over $from" })"

    Wait-ForPid
    if (Test-Path -LiteralPath $target) { Assert-NotRunning $target }
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # Left by an earlier install that could not delete them (a DLL still loaded).
    Get-ChildItem -LiteralPath $target -Recurse -Force -Filter "*$OldSuffix*" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # 1) Stage: every file copied in, before anything already there is touched.
    $stage = Join-Path $target ('.install-' + [Guid]::NewGuid().ToString('N'))
    $order = @($files) + 'manifest.txt'          # manifest.txt last: it says the install is whole
    try {
        foreach ($rel in $order) {
            $dst = Join-Path $stage $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $Self $rel) -Destination $dst
        }
    } catch {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Fail "could not copy the package into $target ($($_.Exception.Message)) - nothing was changed"
    }

    # 2) Swap: each file moved into place, the old one renamed aside; undone on failure.
    $done = New-Object System.Collections.ArrayList
    try {
        foreach ($rel in $order) {
            $dst = Join-Path $target $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
            $aside = $null
            if (Test-Path -LiteralPath $dst) {
                $aside = "$dst$OldSuffix-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
                [IO.File]::Move($dst, $aside)
            }
            [void]$done.Add(@{ dst = $dst; aside = $aside })
            [IO.File]::Move((Join-Path $stage $rel), $dst)
        }
    } catch {
        $why = $_.Exception.Message
        for ($i = $done.Count - 1; $i -ge 0; $i--) {
            $d = $done[$i]
            try {
                if (Test-Path -LiteralPath $d.dst) { Remove-Item -LiteralPath $d.dst -Force }
                if ($d.aside) { [IO.File]::Move($d.aside, $d.dst) }
            } catch { Log "rollback: could not put back $($d.dst): $($_.Exception.Message)" }
        }
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Fail "could not move the new files into place ($why) - the previous files were put back"
    }
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($d in $done) {
        if ($d.aside) { Remove-Item -LiteralPath $d.aside -Force -ErrorAction SilentlyContinue }
    }

    # 3) What the previous version shipped and this one does not.
    if ($old) {
        $now = @{}
        foreach ($rel in $files) { $now[$rel.ToLowerInvariant()] = $true }
        foreach ($rel in $old) {
            if ($now.ContainsKey($rel.ToLowerInvariant())) { continue }
            $f = Join-Path $target $rel
            if (Test-Path -LiteralPath $f -PathType Leaf) {
                Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
                Log "removed $rel - no longer part of the package"
            }
        }
        Remove-EmptyDirs $target $false
    }

    # A package downloaded through a browser carries the mark of the web onto every
    # file unzipped from it, and SmartScreen would ask about Gloam.exe and every
    # bundled ArgyllCMS tool it starts.
    foreach ($rel in $order) { Unblock-File -LiteralPath (Join-Path $target $rel) -ErrorAction SilentlyContinue }

    if (-not $NoShortcut) {
        $s = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
        $s.TargetPath = Join-Path $target $AppExe
        $s.WorkingDirectory = $target
        $s.IconLocation = "$(Join-Path $target $AppExe),0"
        $s.Description = 'Gamma correction for SDR content in Windows HDR mode'
        $s.Save()
        Log "start menu shortcut: $Shortcut"
    }

    Say "installed Gloam $version into $target$(if ($from) { " (was $from)" })"
    if ($Launch) { Start-App $target }
    exit 0
}

$script:Target = $null
try {
    switch ($PSCmdlet.ParameterSetName) {
        'Uninstall' { Invoke-Uninstall }
        'Package'   { Invoke-Package }
        default     { Invoke-Install }
    }
} catch {
    Fail $_.Exception.Message
}
