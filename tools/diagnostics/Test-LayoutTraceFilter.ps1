$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'LayoutTraceCollector.cs')
$invalid=@(
    '[LayoutCycleTracing] "SetValue(Text=SyntheticSensitiveValue)","Name: TitleText"',
    '[LayoutCycleTracing] "SetValue(Text="Name: PrivateProject")","ClassName: Microsoft.UI.Xaml.Controls.TextBlock"',
    '[LayoutCycleTracing] "SetValue(Width=SyntheticSensitiveValue)"',
    'app-log [LayoutCycleTracing] "Name: PrivateProject"',
    '[LayoutCycleTracing] "SetValue(Content="SetValue(Width=PrivateProject)")"',
    '[LayoutCycleTracing] "minimum: SensitiveValue, maximum: 17"'
)
foreach($line in $invalid){if([LayoutTraceCollector]::Sanitize($line) -ne ''){throw 'Unsupported or private field accepted'}}
$valid=@(
    '[LayoutCycleTracing] "Measure(700.666687x488.000000)","ClassName: Microsoft.UI.Xaml.Controls.ListView"',
    '[LayoutCycleTracing] "Old ScrollData: Viewport: 700.666687x488.000000, Extent: 700.666687x1522.666626","New ScrollData: Viewport: 700.666687x488.000000, Extent: 700.666687x1522.666626","Offset: 0.000000x1034.105713"',
    '[LayoutCycleTracing] "SetValue(Margin=0,329.821,0,0)","LayoutCycleCountdown: 6","ClassName: Microsoft.UI.Xaml.Controls.Grid","Name: VerticalPanningRoot"',
    '[LayoutCycleTracing] "minimum: 0.000000, maximum: 1034.666626, value: 1034.105713, viewport: 488.000000"',
    '[LayoutCycleTracing] Layout Iteration Countdown: 6. Launching Arrange Pass.',
    '[LayoutCycleTracing] "DesiredSize changed, old: 865.599976x0.000000 new: 866.400024x0.000000","LayoutCycleCountdown: 6","ClassName: Microsoft.UI.Xaml.Controls.Grid"'
)
foreach($line in $valid){if([LayoutTraceCollector]::Sanitize($line) -eq ''){throw 'Known layout geometry discarded'}}
'PASS: 6 adversarial records rejected, 6 native-format geometry records preserved'
