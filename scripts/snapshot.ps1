<#
.SYNOPSIS
  Runs the published DownloadDock.exe in its headless diagnostic modes against a folder of sample
  downloads: renders every file's thumbnail and a light/dark snapshot of the fan.
  Fails if any document type that should have a real preview falls back to an icon.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Samples,
    [Parameter(Mandatory)] [string] $Out,
    [switch] $PrintPreview
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Exe = (Resolve-Path $Exe).Path

function Invoke-Dock([string[]] $Arguments) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

$failures = @()
foreach ($file in Get-ChildItem -File $Samples) {
    $png = Join-Path $Out ("thumb-" + $file.Name + ".png")
    $code = Invoke-Dock @('--thumbnail', "`"$($file.FullName)`"", "`"$png`"", '256')
    if ($code -ne 0) { $failures += "$($file.Name): exit $code"; continue }
    $report = Get-Content "$png.json" | ConvertFrom-Json
    Write-Host ("{0,-28} source={1,-12} icon={2,-5} {3}x{4} {5:N0} ms" -f $file.Name, $report.source, $report.isIcon, $report.width, $report.height, $report.milliseconds)
    $mustPreview = @('.pdf', '.png', '.jpg', '.docx', '.pptx', '.xlsx', '.md')
    if ($mustPreview -contains $file.Extension.ToLowerInvariant() -and $report.isIcon) {
        $failures += "$($file.Name): expected a content preview but got an icon ($($report.source))"
    }
}

foreach ($theme in 'light', 'dark') {
    $png = Join-Path $Out "fan-$theme.png"
    $code = Invoke-Dock @('--fan-snapshot', "`"$Samples`"", "`"$png`"", $theme, '9', '96')
    if ($code -ne 0) {
        $err = "$png.error.txt"
        if (Test-Path $err) { Get-Content $err | Write-Host }
        $failures += "fan-$theme snapshot: exit $code"
    } else {
        Get-Content "$png.json" | Write-Host
    }
}

# Floating screenshot thumbnail, using one of the sample images as "the screenshot".
$shot = Get-ChildItem -File $Samples -Filter *.jpg | Select-Object -First 1
if ($shot) {
    foreach ($theme in 'light', 'dark') {
        $png = Join-Path $Out "toast-$theme.png"
        $code = Invoke-Dock @('--toast-snapshot', "`"$($shot.FullName)`"", "`"$png`"", $theme)
        if ($code -ne 0) { $failures += "toast-$theme snapshot: exit $code" } else { Get-Content "$png.json" | Write-Host }
    }
}

if ($PrintPreview -or $env:GITHUB_ACTIONS) {
    # Emit small JPEGs of the snapshots as base64 so they can be inspected from the job log.
    Add-Type -AssemblyName System.Drawing
    foreach ($theme in 'light', 'dark', 'toast-light', 'toast-dark') {
        $src = if ($theme -like 'toast-*') { Join-Path $Out "$theme.png" } else { Join-Path $Out "fan-$theme.png" }
        if (-not (Test-Path $src)) { continue }
        $img = [System.Drawing.Image]::FromFile($src)
        $scale = [Math]::Min(1.0, 560.0 / $img.Width)
        $bmp = New-Object System.Drawing.Bitmap ([int]($img.Width * $scale)), ([int]($img.Height * $scale))
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.DrawImage($img, 0, 0, $bmp.Width, $bmp.Height)
        $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
        $params = New-Object System.Drawing.Imaging.EncoderParameters 1
        $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 85L
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, $codec, $params)
        $b64 = [Convert]::ToBase64String($ms.ToArray())
        $tag = $theme.ToUpperInvariant()
        Write-Host "FAN-SNAPSHOT-$tag-BEGIN"
        for ($i = 0; $i -lt $b64.Length; $i += 3000) { Write-Host $b64.Substring($i, [Math]::Min(3000, $b64.Length - $i)) }
        Write-Host "FAN-SNAPSHOT-$tag-END"
        $g.Dispose(); $bmp.Dispose(); $img.Dispose()
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host "All snapshot checks passed."
