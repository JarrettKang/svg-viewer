param([switch]$Test)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish src/SvgViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist/SvgViewer
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath README.md -Destination dist/SvgViewer/README.md -Force
Copy-Item -LiteralPath LICENSE -Destination dist/SvgViewer/LICENSE -Force
Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination dist/SvgViewer/THIRD-PARTY-NOTICES.md -Force
Copy-Item -LiteralPath samples -Destination dist/SvgViewer -Recurse -Force
$null = New-Item -ItemType Directory -Force dist/SvgViewer/licenses
$assets = Get-Content src/obj/project.assets.json -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Name -notmatch '^Microsoft\.(NETCore\.App\.Runtime|WindowsDesktop\.App\.Runtime|Web\.WebView2)/?') { continue }
    foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
        $packageDirectory = Join-Path $packageRoot $library.Value.path
        if (!(Test-Path -LiteralPath $packageDirectory)) { continue }
        $component = $library.Name.Replace('/', '-')
        foreach ($notice in 'LICENSE', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'NOTICE.txt') {
            $noticePath = Join-Path $packageDirectory $notice
            if (Test-Path -LiteralPath $noticePath) {
                Copy-Item -LiteralPath $noticePath -Destination (Join-Path 'dist/SvgViewer/licenses' ($component + '-' + $notice)) -Force
            }
        }
        break
    }
}
foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
    foreach ($download in $framework.downloadDependencies) {
        if ($download.name -notmatch '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.') { continue }
        $runtimeVersion = $download.version.Trim('[', ']').Split(',')[0].Trim()
        foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
            $runtimeDirectory = Join-Path $packageRoot ($download.name.ToLowerInvariant() + '/' + $runtimeVersion)
            if (!(Test-Path -LiteralPath $runtimeDirectory)) { continue }
            foreach ($notice in 'LICENSE', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt') {
                $noticePath = Join-Path $runtimeDirectory $notice
                if (Test-Path -LiteralPath $noticePath) {
                    Copy-Item -LiteralPath $noticePath -Destination (Join-Path 'dist/SvgViewer/licenses' ($download.name + '-' + $runtimeVersion + '-' + $notice)) -Force
                }
            }
            break
        }
    }
}
if ($Test) {
    & (Join-Path $PSScriptRoot 'tests/test.ps1') -Executable (Join-Path $PSScriptRoot 'dist/SvgViewer/SvgViewer.exe')
}
Compress-Archive -Path dist/SvgViewer -DestinationPath dist/SvgViewer-win-x64.zip -Force
Write-Host 'Ready: dist/SvgViewer/SvgViewer.exe'
