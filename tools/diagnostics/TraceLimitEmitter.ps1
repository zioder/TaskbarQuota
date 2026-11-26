param([Parameter(Mandatory)][string]$FinishedFile)
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LimitDebugOutput {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern void OutputDebugStringW(string value);
    [DllImport("kernel32.dll")] public static extern bool IsDebuggerPresent();
    public static bool RaiseAndCatch() { try {throw new InvalidOperationException("SyntheticSensitiveValue");} catch(InvalidOperationException) {return true;} }
}
'@
$deadline=[datetime]::UtcNow.AddSeconds(5)
while(![LimitDebugOutput]::IsDebuggerPresent() -and [datetime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 10}
if(![LimitDebugOutput]::IsDebuggerPresent()){throw 'No collector attached'}
if(![LimitDebugOutput]::RaiseAndCatch()){throw 'Collector intercepted the managed exception'}
$payload='[LayoutCycleTracing] "SetValue(Width=42)"'+(',"Name: '+('A'*64)+'"')*64
for($n=0;$n -lt 2000;$n++){[LimitDebugOutput]::OutputDebugStringW($payload)}
Start-Sleep -Seconds 5
Set-Content -LiteralPath $FinishedFile -Value 'managed-exception-caught-and-emitter-finished'
