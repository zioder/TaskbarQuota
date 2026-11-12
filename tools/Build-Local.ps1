#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$projectPath = Join-Path $repoRoot 'src\TaskbarQuota.App\TaskbarQuota.App.csproj'
$localDirectory = Join-Path $artifactsRoot 'local'
$previousDirectory = Join-Path $artifactsRoot 'local.previous'
$stagingDirectory = Join-Path $artifactsRoot ('local-staging-' + [Guid]::NewGuid().ToString('N'))
$localExecutable = Join-Path $localDirectory 'TaskbarQuota.exe'

function Assert-OrdinaryAncestors([string]$Path) {
    $current = [System.IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (!$item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                throw "Expected ordinary build path ancestors: $current"
            }
        }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }
}

function Assert-ArtifactDirectory([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ([System.IO.Path]::GetDirectoryName($fullPath) -ne $artifactsRoot) {
        throw "Path is outside the build directory: $fullPath"
    }
    Assert-OrdinaryAncestors $fullPath
    if (Test-Path -LiteralPath $fullPath) {
        $item = Get-Item -LiteralPath $fullPath -Force
        if (!$item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Expected an ordinary build directory: $fullPath"
        }
        if ((Resolve-Path -LiteralPath $fullPath).ProviderPath -ne $fullPath) {
            throw "Unexpected resolved build path: $fullPath"
        }
    }
}

Assert-OrdinaryAncestors $artifactsRoot
Assert-ArtifactDirectory $stagingDirectory
Assert-ArtifactDirectory $localDirectory
Assert-ArtifactDirectory $previousDirectory
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$buildLock = [System.IO.File]::Open(
    (Join-Path $artifactsRoot 'local-build.lock'),
    [System.IO.FileMode]::OpenOrCreate,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None)
$instances = @()

try {
    Assert-ArtifactDirectory $stagingDirectory
    Assert-ArtifactDirectory $localDirectory
    Assert-ArtifactDirectory $previousDirectory
    Write-Host 'Building TaskbarQuota...'
    & dotnet publish $projectPath -c Release -p:Platform=x64 -r win-x64 -o $stagingDirectory --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'Build failed. The current local build has been left untouched.'
    }
    if (!(Test-Path -LiteralPath (Join-Path $stagingDirectory 'TaskbarQuota.exe') -PathType Leaf)) {
        throw 'Publish did not produce TaskbarQuota.exe.'
    }

    Assert-ArtifactDirectory $stagingDirectory
    Assert-ArtifactDirectory $localDirectory
    Assert-ArtifactDirectory $previousDirectory

    $instances = @(Get-Process -Name TaskbarQuota -ErrorAction SilentlyContinue)
    foreach ($instance in $instances) {
        # Keep the process identity alive before validating its executable. Termination below
        # must use this same handle, even if the process exits while output is being staged.
        [void]$instance.Handle
        if ($instance.HasExited) {
            continue
        }
        if ($instance.Path -ne $localExecutable) {
            throw "A different TaskbarQuota copy is running. Close it first: $($instance.Path)"
        }
    }

    # Only generated build output is replaced; application settings remain in LocalAppData.
    if (Test-Path -LiteralPath $previousDirectory) {
        if (!(Test-Path -LiteralPath (Join-Path $previousDirectory 'TaskbarQuota.exe') -PathType Leaf)) {
            throw "Refusing to remove an unrecognized backup directory: $previousDirectory"
        }
        Remove-Item -LiteralPath $previousDirectory -Recurse -Force
    }
    foreach ($instance in $instances) {
        if ($instance.HasExited) {
            continue
        }
        $instance.Kill()
        if (!$instance.WaitForExit(10000)) {
            throw 'The running local copy did not exit within 10 seconds.'
        }
    }

    $movedPrevious = $false
    try {
        if (Test-Path -LiteralPath $localDirectory) {
            Move-Item -LiteralPath $localDirectory -Destination $previousDirectory
            $movedPrevious = $true
        }
        Move-Item -LiteralPath $stagingDirectory -Destination $localDirectory
    }
    catch {
        if ($movedPrevious -and !(Test-Path -LiteralPath $localDirectory)) {
            Move-Item -LiteralPath $previousDirectory -Destination $localDirectory
        }
        if (!$NoStart -and (Test-Path -LiteralPath $localExecutable)) {
            Start-Process -FilePath $localExecutable -ArgumentList '--startup-widget' -WorkingDirectory $localDirectory -WindowStyle Hidden
        }
        throw
    }

    if (!$NoStart) {
        Start-Process -FilePath $localExecutable -ArgumentList '--startup-widget' -WorkingDirectory $localDirectory -WindowStyle Hidden
    }
    Write-Host "Local build: $localExecutable"
}
finally {
    foreach ($instance in $instances) {
        $instance.Dispose()
    }
    $buildLock.Dispose()
}
