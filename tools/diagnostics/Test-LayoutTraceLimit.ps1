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
$proof=Join-Path $traceRoot ('collector-limit-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $proof | Out-Null
$pwshExe=(Get-Command pwsh).Source
$start=[Diagnostics.ProcessStartInfo]::new($pwshExe)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
foreach($arg in @('-NoProfile','-File',(Join-Path $toolRoot 'TraceLimitEmitter.ps1'),'-FinishedFile',(Join-Path $proof 'finished.txt'))){$start.ArgumentList.Add($arg)}
$child=[Diagnostics.Process]::Start($start)
try {
    $status=[LayoutTraceCollector]::Run($child.Id,$pwshExe,(Join-Path $proof 'layout.log'),20)
    $size=(Get-Item (Join-Path $proof 'layout.log')).Length
    if($status -ne 'size-limit' -or $size -gt 4000000 -or $size -lt 3900000){throw 'Actual 4 MB limit failed'}
    if($child.HasExited){throw 'Emitter did not survive size-limit detach'}
    if(!$child.WaitForExit(15000) -or !(Test-Path (Join-Path $proof 'finished.txt'))){throw 'Emitter failed managed-exception/normal-exit check'}
    'PASS: actual 4 MB bound; managed exception caught by target; target survived detach and exited normally'
} finally {
    try {if(!$child.HasExited -and !$child.WaitForExit(15000)){$child.Kill(); [void]$child.WaitForExit(5000)}} finally {$child.Dispose()}
    Compress-Archive -LiteralPath $proof -DestinationPath ($proof+'.zip')
    if((Get-Item -LiteralPath ($proof+'.zip')).Length -le 0){throw 'Empty evidence archive'}
    $resolved=(Resolve-Path -LiteralPath $proof).ProviderPath
    if([IO.Path]::GetDirectoryName($resolved) -ne $traceRoot -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected cleanup target'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
'Evidence: '+$proof+'.zip'
