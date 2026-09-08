# Renders the Docs HTML sources to PDFs in the project root.
#
# Headless Chrome, not a Markdown converter: these pages use real layout -- SVG
# diagrams, tables, print-specific page-break rules -- and the browser is the only
# thing that agrees with what the published artifact shows.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$docs = Join-Path $root "Docs"

$chrome = @(
    "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $chrome) {
    Write-Error "Chrome not found. Install it, or render the pages from any browser with Ctrl+P."
}

$pages = @(
    @{ Source = "groundworks-playtest.html";              Output = "Groundworks Playtest.pdf" },
    @{ Source = "groundworks-terrain-architecture.html";  Output = "Groundworks Terrain Architecture.pdf" }
)

foreach ($page in $pages) {
    $source = Join-Path $docs $page.Source
    $output = Join-Path $root $page.Output

    if (-not (Test-Path $source)) {
        Write-Warning "Missing $source; skipped."
        continue
    }

    # virtual-time-budget gives the Google Fonts stylesheet time to land. Without it
    # the PDF renders in the fallback face.
    & $chrome --headless=new --disable-gpu --no-sandbox `
        --no-pdf-header-footer --print-to-pdf-no-header `
        --virtual-time-budget=10000 `
        "--print-to-pdf=$output" ("file:///" + $source.Replace("\", "/")) | Out-Null

    if (Test-Path $output) {
        $kb = [math]::Round((Get-Item $output).Length / 1KB)
        Write-Host ("  {0}  ({1} KB)" -f $page.Output, $kb)
    } else {
        Write-Warning ("Failed to render " + $page.Output)
    }
}
