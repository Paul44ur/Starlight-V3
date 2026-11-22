# Builds Starlight-Setup.exe on the PC (Windows, .NET 10 SDK).
#   .\build\pack.ps1 -Version 0.1.0 -Llama D:\path\to\llama-bXXXX-bin-win-vulkan-x64.zip
# The llama.cpp zip and the Python embeddable zip are checked against the SHA-256 values below.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Llama,
    [string]$LlamaSha256 = "",
    [string]$Python = "",
    [string]$PythonSha256 = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"
$publish = Join-Path $out "publish"
$work = Join-Path $out "work"
Remove-Item $publish, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $publish, $work | Out-Null

function Check($file, $sha) {
    if ($sha -eq "") { Write-Warning "No SHA-256 given for $file; not checked."; return }
    $have = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($have -ne $sha.ToUpper()) { throw "$file does not match its SHA-256 ($have)." }
}

dotnet publish (Join-Path $root "src\Starlight.App\Starlight.App.csproj") -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# llama-server (Vulkan build) into runtime\llama
Check $Llama $LlamaSha256
$llamaDir = Join-Path $publish "runtime\llama"
Expand-Archive $Llama -DestinationPath $llamaDir -Force
if (-not (Test-Path (Join-Path $llamaDir "llama-server.exe"))) {
    $inner = Get-ChildItem $llamaDir -Recurse -Filter llama-server.exe | Select-Object -First 1
    if (-not $inner) { throw "llama-server.exe is not in $Llama" }
    Get-ChildItem $inner.DirectoryName | Move-Item -Destination $llamaDir -Force
}

# Embeddable Python for the code sandbox into runtime\python
if ($Python -eq "") {
    $Python = Join-Path $work "python-embed.zip"
    Invoke-WebRequest "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip" -OutFile $Python
}
Check $Python $PythonSha256
Expand-Archive $Python -DestinationPath (Join-Path $publish "runtime\python") -Force

Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $publish

dotnet tool restore
dotnet vpk pack --packId Starlight --packVersion $Version --packDir $publish --mainExe Starlight.exe `
    --packTitle Starlight --icon (Join-Path $root "src\Starlight.App\Assets\starlight.ico") --outputDir (Join-Path $out "release")
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
Write-Host "Done: $(Join-Path $out 'release')"
