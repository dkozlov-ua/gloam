<#
    package.ps1 - build the Gloam setup.

      .\package.ps1                     the <Version> in src\Gloam\Gloam.csproj
      .\package.ps1 -Version 1.2.0      another one (publish.ps1 passes it)
      ... -OutDir <dir>                 somewhere other than Releases\ (inside the repo)

    Writes, under OutDir (Releases\ by default, gitignored):

      Gloam-X.Y.Z\              the package, staged: what the setup installs
      Gloam-X.Y.Z-setup.exe     the setup - the one asset a GitHub release carries

    WHAT GOES IN is a self-contained, multi-file `dotnet publish` of the app, the
    bundled ArgyllCMS (downloaded and checked against a pinned SHA-256), README,
    LICENSE and THIRD_PARTY_NOTICES, compiled into a setup by Inno Setup 6 from
    installer\Gloam.iss.

    The staged package is checked by scripts\Test-PublishOutput.ps1 (required
    files, then a launch smoke test against an isolated data folder) before the
    setup is compiled, so a setup whose Gloam cannot start is never built.

    NEEDS the .NET SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#>
param(
    [string]$Version = "",
    [string]$OutDir = "Releases"
)

$ErrorActionPreference = "Stop"

$projectPath = "src\Gloam\Gloam.csproj"
$argyllVersion = "Argyll_V3.5.0"
$argyllUrl = "https://www.argyllcms.com/${argyllVersion}_win64_exe.zip"
$argyllZip = "argyll_cache_3.5.0.zip"
$argyllExtractDir = "argyll_cache_3.5.0"
$argyllSha256 = "C80F78BA30E715079A00A14C2E7C9F533C58E6C8FDBC27F2D32B3D7813F9D132"

# --- Helper Functions ---

function Resolve-RepoChildPath([string]$Path, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Description path is required."
    }

    $repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
    $combined = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    } else {
        Join-Path $repoRoot $Path
    }
    $fullPath = [System.IO.Path]::GetFullPath($combined)

    $trimChars = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $repoTrimmed = $repoRoot.TrimEnd($trimChars)
    $pathTrimmed = $fullPath.TrimEnd($trimChars)
    $repoPrefix = $repoTrimmed + [System.IO.Path]::DirectorySeparatorChar

    if ($pathTrimmed -eq $repoTrimmed) {
        throw "$Description path must not be the repository root."
    }

    if (-not $fullPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description path must stay inside the repository: $fullPath"
    }

    return $fullPath
}

function Remove-RepoChildDirectoryIfExists([string]$Path, [string]$Description) {
    $fullPath = Resolve-RepoChildPath $Path $Description
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    } elseif (Test-Path -LiteralPath $fullPath) {
        throw "$Description path exists but is not a directory: $fullPath"
    }

    return $fullPath
}

function Download-Argyll {
    if (-not (Test-Path -LiteralPath $argyllZip -PathType Leaf)) {
        Write-Host "Downloading ArgyllCMS..."
        Invoke-WebRequest -Uri $argyllUrl -OutFile $argyllZip
    }

    # Verify integrity BEFORE extracting. Never run Expand-Archive on unverified content,
    # and never trust a pre-existing cached zip or extract dir - a poisoned cache in the
    # build workspace would otherwise be bundled into a release. Fail closed.
    $actualHash = (Get-FileHash -LiteralPath $argyllZip -Algorithm SHA256).Hash
    if ($actualHash -ne $argyllSha256) {
        throw "Argyll archive integrity check failed (SHA-256 $actualHash). Delete '$argyllZip' and retry."
    }

    # Re-extract from scratch so a stale/tampered extract directory is never reused.
    Remove-RepoChildDirectoryIfExists $argyllExtractDir "Argyll extract directory" | Out-Null
    Write-Host "Extracting ArgyllCMS..."
    Expand-Archive -LiteralPath $argyllZip -DestinationPath $argyllExtractDir

    $root = Join-Path $argyllExtractDir $argyllVersion
    if (-not (Test-Path -LiteralPath (Join-Path $root "bin\dispwin.exe") -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $root "bin\spotread.exe") -PathType Leaf)) {
        throw "Argyll archive is missing dispwin.exe or spotread.exe"
    }
    return $root
}

# The Inno Setup compiler: on PATH, or where its installer put it (per user or for everyone).
function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $keys = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    foreach ($key in $keys) {
        $dir = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir 'ISCC.exe'))) { return (Join-Path $dir 'ISCC.exe') }
    }
    throw 'Inno Setup 6 is not installed - winget install JRSoftware.InnoSetup'
}

function Get-PackageFiles([string]$Root) {
    $prefix = $Root.TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
             ForEach-Object { $_.FullName.Substring($prefix.Length) } |
             Sort-Object)
}

# --- Execution ---

$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$projectPath = Join-Path $repoRoot $projectPath
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -Raw -LiteralPath $projectPath
    $Version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version was not provided and no <Version> was found in $projectPath."
    }
}
$Version = $Version.Trim() -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' is not X.Y.Z - Check for Updates compares releases by those three numbers and nothing else."
}

$iscc = Find-Iscc
$OutDir = Resolve-RepoChildPath $OutDir "Output directory"
$name = "Gloam-$Version"
$stage = Remove-RepoChildDirectoryIfExists (Join-Path $OutDir $name) "Package staging directory"
$setup = Join-Path $OutDir "$name-setup.exe"
$argyllZip = Resolve-RepoChildPath $argyllZip "Argyll cache archive"
$argyllExtractDir = Resolve-RepoChildPath $argyllExtractDir "Argyll extract directory"

# 1. Publish self-contained and MULTI-FILE, straight into the staging folder. Multi-file
#    rather than PublishSingleFile: the bundled ArgyllCMS tools and the ICM templates sit
#    beside Gloam.exe anyway, and a single-file host extracts its native parts to a temp
#    folder on every launch.
Write-Host "--- Publishing (self-contained, multi-file) v$Version ---"
dotnet publish $projectPath -c Release -r win-x64 --self-contained true --output $stage `
    /p:Version=$Version `
    /p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2. Bundle docs and ArgyllCMS. (ICM templates are already copied to the output by the
#    csproj.)
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE.txt") -Destination $stage
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.txt") -Destination $stage

$argyllRoot = Download-Argyll
$bundleRoot = Join-Path $stage "argyll_cache\$argyllVersion"
Write-Host "Bundling $argyllVersion from $argyllRoot"
New-Item -ItemType Directory -Path (Split-Path $bundleRoot) -Force | Out-Null
Copy-Item -LiteralPath $argyllRoot -Destination $bundleRoot -Recurse

# 3. Does it start. The launch smoke runs Gloam.exe from the staging folder, so the
#    file list is taken before it and must be the same after: anything the smoke
#    leaves there would otherwise ship.
$files = Get-PackageFiles $stage
& (Join-Path $repoRoot "scripts\Test-PublishOutput.ps1") -PublishDir $stage -ArgyllVersion $argyllVersion
$after = Get-PackageFiles $stage
$extra = @(Compare-Object -ReferenceObject $files -DifferenceObject $after | ForEach-Object { $_.InputObject })
if ($extra.Count -gt 0) {
    throw "The launch smoke changed the package's files:`n$($extra -join "`n")"
}

# 4. The setup.
Write-Host "--- Compiling $name-setup.exe ---"
if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }
& $iscc /Q "/DVersion=$Version" "/DSourceDir=$stage" "/O$OutDir" (Join-Path $repoRoot "installer\Gloam.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "ISCC did not write $setup" }
$sha = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "`nPackaged $name ($($files.Count) files)"
Write-Host "  $setup"
Write-Host "  sha256 $sha"
