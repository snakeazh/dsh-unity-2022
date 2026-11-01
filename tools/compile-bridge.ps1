# Compiles the editor bridge against the assemblies shipped with Unity 2022.3.62f3.
# Usage: pwsh -File compile-bridge.ps1 [-EditorRoot <path to ...\Editor>]
param(
    [string]$EditorRoot = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor',
    [string]$Source = (Join-Path $PSScriptRoot '..\unity-package\Editor'),
    [string]$Out = (Join-Path $env:TEMP 'dsh-bridge-check\Dsh.UnityBridge.dll')
)

$ErrorActionPreference = 'Stop'

$data = Join-Path $EditorRoot 'Data'
$csc = Join-Path $data 'DotNetSdkRoslyn\csc.dll'
$dotnet = Join-Path $data 'NetCoreRuntime\dotnet.exe'

if (-not (Test-Path $csc)) { throw "Roslyn compiler not found: $csc" }
if (-not (Test-Path $dotnet)) { throw "dotnet not found: $dotnet" }

$outDir = Split-Path -Parent $Out
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$refs = New-Object System.Collections.Generic.List[string]
$refs.Add((Join-Path $data 'NetStandard\ref\2.1.0\netstandard.dll'))
$refs.Add((Join-Path $data 'Managed\UnityEngine.dll'))
$refs.Add((Join-Path $data 'Managed\UnityEditor.dll'))
Get-ChildItem (Join-Path $data 'Managed\UnityEngine') -Filter *.dll -ErrorAction SilentlyContinue |
    ForEach-Object { $refs.Add($_.FullName) }

$sources = @(Get-ChildItem $Source -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "No .cs files under $Source" }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('-nologo')
$lines.Add('-target:library')
$lines.Add('-langversion:9')
$lines.Add('-nostdlib+')
$lines.Add('-nowarn:0618,1701,1702,0649')
$lines.Add('-define:UNITY_EDITOR;UNITY_EDITOR_WIN;UNITY_2022_3_OR_NEWER;UNITY_STANDALONE_WIN')
$lines.Add("-out:`"$Out`"")
foreach ($reference in ($refs | Select-Object -Unique)) { $lines.Add("-r:`"$reference`"") }
foreach ($file in $sources) { $lines.Add("`"$file`"") }

$rsp = Join-Path $outDir 'bridge.rsp'
Set-Content -Path $rsp -Value $lines -Encoding utf8

Write-Host "Compiling $($sources.Count) file(s) with $csc"
& $dotnet $csc "@$rsp"
$code = $LASTEXITCODE
if ($code -eq 0) { Write-Host "OK -> $Out" } else { Write-Host "FAILED ($code)" }
exit $code
