$ErrorActionPreference='Stop'
$toolRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$traceRoot=[IO.Path]::GetFullPath((Join-Path $toolRoot '../../artifacts/layout-trace-tests'))
$ancestor=$traceRoot
while($ancestor){
    if(Test-Path -LiteralPath $ancestor){$item=Get-Item -LiteralPath $ancestor; if(!$item.PSIsContainer -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected test path'}}
    $ancestor=[IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Path $traceRoot -Force | Out-Null
Add-Type -Path (Join-Path $toolRoot 'LayoutTraceCollector.cs')
$proof=Join-Path $traceRoot ('collector-selftest-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $proof | Out-Null
$pwshExe=(Get-Command pwsh).Source
$start=[Diagnostics.ProcessStartInfo]::new($pwshExe)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($arg in @('-NoProfile','-File',(Join-Path $toolRoot 'TraceEmitter.ps1'),'-FinishedFile',(Join-Path $proof 'finished.txt'))){$start.ArgumentList.Add($arg)}
$child=[Diagnostics.Process]::Start($start)
try {
    $status=[LayoutTraceCollector]::Run($child.Id,$pwshExe,(Join-Path $proof 'layout.log'),5)
    if($status -ne 'timeout' -or $child.HasExited){throw 'Emitter failed detach-survival check'}
    $text=[IO.File]::ReadAllText((Join-Path $proof 'layout.log'))
    if($text.Contains('SyntheticSensitiveValue') -or !$text.Contains('VerticalPanningRoot')){throw 'Filtering/capture failed'}
    $stopFile=Join-Path $proof 'stop'
    Set-Content -LiteralPath $stopFile -Value 'stop'
    if([LayoutTraceCollector]::Run($child.Id,$pwshExe,(Join-Path $proof 'stopped.log'),5,$stopFile) -ne 'stopped'){throw 'Stop request failed'}
    $rejected=$false
    try {[LayoutTraceCollector]::Run($child.Id,(Join-Path $proof 'different.exe'),(Join-Path $proof 'must-not-exist.log'),1)} catch {$rejected=$true}
    if(!$rejected -or (Test-Path (Join-Path $proof 'must-not-exist.log'))){throw 'Wrong target path accepted'}
    $liveArchive=& (Join-Path $toolRoot 'Run-LiveLayoutTrace.ps1') -TargetId $child.Id -MaximumSeconds 1 -ExpectedExecutable $pwshExe
    if($LASTEXITCODE -and $LASTEXITCODE -ne 0){throw 'Archive runner failed'}
    if(!(Test-Path -LiteralPath $liveArchive) -or (Test-Path -LiteralPath ($liveArchive -replace '\.zip$',''))){throw 'Archive/cleanup failed'}
    if(!$child.WaitForExit(15000)){throw 'Emitter failed to exit on time'}
    if(!(Test-Path (Join-Path $proof 'finished.txt'))){throw 'Emitter did not finish normally'}
    'PASS: filtered native output, detach survival, stop request, wrong-path rejection, archived live-runner output, temporary cleanup, normal emitter exit'
} finally {
    try {if(!$child.HasExited -and !$child.WaitForExit(15000)){$child.Kill(); [void]$child.WaitForExit(5000)}} finally {$child.Dispose()}
    Compress-Archive -LiteralPath $proof -DestinationPath ($proof+'.zip')
    if((Get-Item -LiteralPath ($proof+'.zip')).Length -le 0){throw 'Empty evidence archive'}
    $resolved=(Resolve-Path -LiteralPath $proof).ProviderPath
    if([IO.Path]::GetDirectoryName($resolved) -ne $traceRoot -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected cleanup target'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
'Evidence: '+$proof+'.zip'
