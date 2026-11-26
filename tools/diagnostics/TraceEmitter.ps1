param([Parameter(Mandatory)][string]$FinishedFile)
Add-Type @'
using System.Runtime.InteropServices;
public static class SyntheticDebugOutput {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern void OutputDebugStringW(string value);
}
'@
for($index=0;$index -lt 60;$index++){
    [SyntheticDebugOutput]::OutputDebugStringW('ordinary log: SyntheticSensitiveValue')
    [SyntheticDebugOutput]::OutputDebugStringW('[LayoutCycleTracing] "SetValue(Text=SyntheticSensitiveValue)","LayoutCycleCountdown: 3","ClassName: Microsoft.UI.Xaml.Controls.TextBlock","Name: TitleText","Instance: 0x1234"')
    [SyntheticDebugOutput]::OutputDebugStringW('[LayoutCycleTracing] "SetValue(Text="Name: SyntheticSensitiveValue")","ClassName: Microsoft.UI.Xaml.Controls.TextBlock"')
    [SyntheticDebugOutput]::OutputDebugStringW('[LayoutCycleTracing] "SetValue(Margin=0,329.821,0,0)","LayoutCycleCountdown: 3","ClassName: Microsoft.UI.Xaml.Controls.Grid","Name: VerticalPanningRoot","Instance: 0x1234"')
    Start-Sleep -Milliseconds 200
}
Set-Content -LiteralPath $FinishedFile -Value 'emitter-finished-normally'
