# Runs the published desktop app against a real hub with fake upstreams and captures the screen.
# Usage: pwsh tests/windows_smoke.ps1 <publish\app folder>
param([string]$App)
$ErrorActionPreference = 'Continue'
$root = (Get-Location).Path
New-Item -ItemType Directory -Force shots-win, "_media\Movies", "_media\TV\Breaking Bad\Season 1" | Out-Null
$failed = $false
function Fail($m) { Write-Host "SMOKE FAIL: $m"; $script:failed = $true }

# ---- media, upstream, hub -------------------------------------------------------------------
$ffmpeg = python -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())"
& $ffmpeg -loglevel error -y -f lavfi -i "testsrc=duration=90:size=1280x720:rate=24" -f lavfi -i "sine=frequency=440:duration=90" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest _media\base.mp4
foreach ($n in 'Inception (2010).mp4', 'Interstellar.2014.1080p.mp4', 'Toy Story (1995).mp4', 'The.Dark.Knight.2008.mp4') { Copy-Item _media\base.mp4 "_media\Movies\$n" }
1..3 | ForEach-Object { Copy-Item _media\base.mp4 "_media\TV\Breaking Bad\Season 1\Breaking.Bad.S01E0$_.mp4" }
Remove-Item _media\base.mp4

$env:DJFLIX_DATA = "$root\_data"; $env:DJFLIX_PORT = '8674'
$env:DJFLIX_TMDB_BASE = 'http://127.0.0.1:19001/3'; $env:DJFLIX_RD_BASE = 'http://127.0.0.1:19001/rd'; $env:DJFLIX_TMDB_IMAGE_BASE = 'http://127.0.0.1:19001/img'
$up = Start-Process python -ArgumentList 'tests/fake_upstream.py', '19001' -PassThru -WindowStyle Hidden
$hub = Start-Process "$App\server\DJFlix.Server.exe" -PassThru -WindowStyle Hidden
foreach ($i in 1..60) { try { Invoke-RestMethod http://127.0.0.1:8674/api/v1/health | Out-Null; break } catch { Start-Sleep -Milliseconds 500 } }
$admin = (Get-Content "$root\_data\admin.token").Trim()
function Api($method, $path, $body) {
    $p = @{ Method = $method; Uri = "http://127.0.0.1:8674/api/v1$path"; Headers = @{ Authorization = "Bearer $admin" }; ContentType = 'application/json' }
    if ($null -ne $body) { $p.Body = ($body | ConvertTo-Json -Compress) } elseif ($method -ne 'GET') { $p.Body = '{}' }
    Invoke-RestMethod @p
}
Api PUT /admin/providers/metadata @{ apiKey = 'tmdbgood' } | Out-Null
Api POST /admin/libraries @{ path = "$root\_media" } | Out-Null
Api POST '/admin/library/scan?wait=true' | Out-Null
Api POST /profiles @{ name = 'DG'; prefs = @{ favouriteGenres = @('Science Fiction') } } | Out-Null
Api POST /admin/setup/complete | Out-Null
Write-Host "library: $((Api GET '/movies?pageSize=1').total) movies"

# ---- app ------------------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function Shot($name) {
    try {
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $bmp.Save("$root\shots-win\$name.png"); $g.Dispose(); $bmp.Dispose(); Write-Host "shot $name"
    } catch { Write-Host "shot $name failed: $_" }
}
$env:DJFLIX_AUTOPILOT = '1'
$appProc = Start-Process "$App\DJFlix.exe" -PassThru
Start-Sleep 4; Shot '01-start'
if ($appProc.HasExited) { Fail "app exited at launch (code $($appProc.ExitCode))" }
# The autopilot walks: profiles → home → movies → details → player → server → settings → home.
$plan = @(@(4, '02-profiles'), @(8, '03-home'), @(8, '04-movies'), @(7, '05-details'), @(9, '06-player'), @(10, '07-player-later'), @(12, '08-server'), @(8, '09-settings'), @(7, '10-player-again'), @(9, '11-home-after'))
foreach ($s in $plan) { Start-Sleep $s[0]; Shot $s[1]; if ($appProc.HasExited) { Fail "app exited during $($s[1]) (code $($appProc.ExitCode))"; break } }

$cw = Api GET /profiles/1/continue-watching
$pos = if ($cw -and $cw.Count -gt 0) { $cw[0].progress.positionMs } else { 0 }
Write-Host "progress reported by the desktop player: $pos ms"
if ($pos -lt 5000) { Fail "desktop playback progress was not reported (got $pos)" }

Write-Host '---- desktop log ----'; Get-Content "$env:LOCALAPPDATA\DJFlix\desktop.log" -ErrorAction SilentlyContinue | Select-Object -Last 30
Write-Host '---- hub log ----'; (Api GET '/admin/logs?limit=40') | ForEach-Object { "$($_.level) $($_.message)" } | Select-Object -First 40
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 2; StartTime = (Get-Date).AddMinutes(-10) } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'DJFlix' } | Select-Object -First 3 | ForEach-Object { Write-Host "EVENTLOG: $($_.Message.Substring(0, [Math]::Min(1500, $_.Message.Length)))" }
if (-not $appProc.HasExited) { Stop-Process -Id $appProc.Id -Force }
Stop-Process -Id $hub.Id, $up.Id -Force -ErrorAction SilentlyContinue
Remove-Item Env:DJFLIX_DATA, Env:DJFLIX_PORT, Env:DJFLIX_AUTOPILOT
if ($failed) { Write-Host 'SMOKE FAILED'; exit 1 } else { Write-Host 'SMOKE OK'; exit 0 }
