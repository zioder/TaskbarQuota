#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Installer and assembly versions require a numeric core, even for prerelease builds.
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "Unsupported build version: $Version"
}
$core = @($Matches[1], $Matches[2], $Matches[3]) | ForEach-Object {
    $part = 0
    if (![int]::TryParse($_, [ref]$part) -or $part -gt 65534) {
        throw "Version component is outside the assembly version range: $_"
    }
    $part
}
[pscustomobject]@{
    AssemblyVersion = "$($core[0]).$($core[1]).$($core[2]).0"
    ApplicationVersion = $core[2]
}
