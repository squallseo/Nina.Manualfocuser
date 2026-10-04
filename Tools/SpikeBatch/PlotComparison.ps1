param(
    [Parameter(Mandatory)][string[]]$Original,
    [Parameter(Mandatory)][string[]]$Improved,
    [Parameter(Mandatory)][string[]]$Labels,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if ($Original.Count -ne $Improved.Count -or $Original.Count -ne $Labels.Count -or $Labels.Count -ne 2) {
    throw 'Supply two original CSVs, two improved CSVs, and two dataset labels.'
}
Add-Type -AssemblyName System.Windows.Forms.DataVisualization
Add-Type -AssemblyName System.Drawing
$chart = New-Object System.Windows.Forms.DataVisualization.Charting.Chart
$chart.Width = 1600
$chart.Height = 1050
$chart.BackColor = [Drawing.Color]::White
$chart.Font = New-Object Drawing.Font('Segoe UI', 10)
$null = $chart.Titles.Add('Autofocus replay: medians of 3 frames per position')

function Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
function AddCurve($Rows, [string]$AreaName, [string]$LegendName, [string]$Label,
    [string]$Variant, [string]$Column, [Drawing.Color]$Color, [bool]$Secondary, [bool]$Dashed) {
    $series = New-Object System.Windows.Forms.DataVisualization.Charting.Series("$AreaName $Label")
    $series.LegendText = $Label
    $series.ChartArea = $AreaName
    $series.Legend = $LegendName
    $series.ChartType = 'Line'
    $series.Color = $Color
    $series.BorderWidth = 3
    $series.MarkerStyle = 'Circle'
    $series.MarkerSize = 6
    if ($Dashed) { $series.BorderDashStyle = 'Dash' }
    if ($Secondary) { $series.YAxisType = 'Secondary' }
    $groups = $Rows | Where-Object { $_.variant -eq $Variant -and $_.$Column -ne '' } | Group-Object focpos
    foreach ($group in ($groups | Sort-Object { [int]$_.Name })) {
        [double[]]$values = @($group.Group | ForEach-Object { [double]::Parse($_.$Column, [Globalization.CultureInfo]::InvariantCulture) })
        $null = $series.Points.AddXY([double]$group.Name, [double](Median $values))
    }
    $chart.Series.Add($series)
}
try {
    for ($dataset = 0; $dataset -lt 2; $dataset++) {
        $before = Import-Csv -LiteralPath $Original[$dataset]
        $after = Import-Csv -LiteralPath $Improved[$dataset]
        for ($metric = 0; $metric -lt 2; $metric++) {
            $name = "d${dataset}m${metric}"
            $kind = @('fwhm', 'legacy')[$metric]
            $area = New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea($name)
            $area.Position = New-Object System.Windows.Forms.DataVisualization.Charting.ElementPosition(
                (2 + 50 * $dataset), (7 + 46 * $metric), 46, 44)
            $area.AxisX.Title = 'Focuser position [steps]'
            $area.AxisX.LabelStyle.Format = '0'
            $area.AxisX.Interval = 5000
            $area.AxisX.LabelStyle.IsStaggered = $false
            $area.AxisX.MajorGrid.LineColor = [Drawing.Color]::Gainsboro
            $area.AxisY.Title = @('Profile FWHM [px]', 'Legacy J [formula units]')[$metric]
            $area.AxisY.IsStartedFromZero = $false
            $area.AxisY.MajorGrid.LineColor = [Drawing.Color]::Gainsboro
            $area.AxisY2.Enabled = 'True'
            $area.AxisY2.Title = 'Saved field HFR [px]'
            $area.AxisY2.MajorGrid.Enabled = $false
            $chart.ChartAreas.Add($area)
            $legend = New-Object System.Windows.Forms.DataVisualization.Charting.Legend($name)
            $legend.DockedToChartArea = $name
            $legend.IsDockedInsideChartArea = $false
            $legend.Docking = 'Top'
            $chart.Legends.Add($legend)
            AddCurve $before $name $name "$($Labels[$dataset]) original" $kind 'J' ([Drawing.Color]::DarkOrange) $false $true
            AddCurve $after $name $name 'updated' $kind 'J' ([Drawing.Color]::RoyalBlue) $false $false
            AddCurve $after $name $name 'Hocus Focus HFR' $kind 'hfr' ([Drawing.Color]::Gray) $true $false
        }
    }
    $chart.SaveImage([IO.Path]::GetFullPath($OutputPath), 'Png')
    Write-Output ([IO.Path]::GetFullPath($OutputPath))
} finally {
    $chart.Dispose()
}
