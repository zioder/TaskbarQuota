param(
    [Parameter(Mandatory)][int]$TargetId,
    [ValidateRange(1,1800)][int]$MaximumSeconds=1800,
    [string]$ExpectedExecutable
)
$ErrorActionPreference='Stop'
$toolRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$workspaceRoot=[IO.Path]::GetFullPath((Join-Path $toolRoot '../..'))
$traceRoot=Join-Path $workspaceRoot 'artifacts/layout-traces'
$ancestor=$traceRoot
while($ancestor){
    if(Test-Path -LiteralPath $ancestor){
        $item=Get-Item -LiteralPath $ancestor
        if(!$item.PSIsContainer -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected trace path'}
    }
    $ancestor=[IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Path $traceRoot -Force | Out-Null
$executable=if($ExpectedExecutable){[IO.Path]::GetFullPath($ExpectedExecutable)}else{Join-Path $workspaceRoot 'artifacts/local/TaskbarQuota.exe'}
$capture=Join-Path $traceRoot ('live-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $capture | Out-Null
$result='not-attached'
try {
    Add-Type -Path (Join-Path $toolRoot 'LayoutTraceCollector.cs')
    @{collectorPid=$PID;targetPid=$TargetId;maximumSeconds=$MaximumSeconds} | ConvertTo-Json | Set-Content (Join-Path $capture 'capture.json')
    $result=[LayoutTraceCollector]::Run($TargetId,$executable,(Join-Path $capture 'layout.log'),$MaximumSeconds,(Join-Path $capture 'stop'))
} catch {
    $result='collector-error'
    @{errorType=$_.Exception.GetType().FullName;hresult=$_.Exception.HResult} | ConvertTo-Json | Set-Content (Join-Path $capture 'error.json')
} finally {
    @{status=$result;finishedUtc=[datetime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content (Join-Path $capture 'result.json')
    $archive=$capture+'.zip'
    Compress-Archive -LiteralPath $capture -DestinationPath $archive
    if((Get-Item -LiteralPath $archive).Length -le 0){throw 'Trace archive is empty'}
    $resolved=(Resolve-Path -LiteralPath $capture).ProviderPath
    if([IO.Path]::GetDirectoryName($resolved) -ne $traceRoot -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Unexpected cleanup target'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Write-Output $archive
}
if($result -eq 'collector-error' -or $result -eq 'detach-error'){exit 1}
