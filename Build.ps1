[CmdletBinding()]
param(
    [ValidateSet('Debug Moonlight', 'Release Moonlight', 'Debug Spotlight', 'Release Spotlight')]
    [string]$Configuration = 'Debug Moonlight',
    [switch]$Restore,
    [string]$MSBuildPath,
    [string]$BuildRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath)) {
    throw 'Install Visual Studio with .NET desktop development, or pass -MSBuildPath.'
}
$frameworkProject = Join-Path (Split-Path $repoRoot -Parent) 'GL_EditorFramework\Gl_EditorFramework\GL_EditorFramework.csproj'
if (-not (Test-Path -LiteralPath $frameworkProject)) {
    throw "Missing sibling dependency: $frameworkProject. See README.md."
}

if (-not $BuildRoot) { $BuildRoot = Join-Path $repoRoot ('artifacts\' + $Configuration.Replace(' ', '-')) }
$buildOutputRoot = [IO.Path]::GetFullPath($BuildRoot).TrimEnd('\', '/') + '\'
$dotnetDirectory = Join-Path $env:ProgramFiles 'dotnet'
if (Test-Path -LiteralPath (Join-Path $dotnetDirectory 'dotnet.exe')) {
    $env:PATH = $dotnetDirectory + [IO.Path]::PathSeparator + $env:PATH
}
$buildArgs = @(
    (Join-Path $repoRoot 'Spotlight.sln'), '/m', '/nologo', '/verbosity:minimal',
    "/p:Configuration=$Configuration", '/p:Platform=Any CPU',
    "/p:SpotlightBuildRoot=$buildOutputRoot",
    ('/p:DirectoryBuildPropsPath=' + (Join-Path $repoRoot 'build\Isolated.props'))
)
if ($Restore) {
    # Restores packages.config packages to the solution packages folder, and SDK references
    # to the user's NuGet cache. No dependency repository is pulled or upgraded.
    & $MSBuildPath @buildArgs '/t:Restore' '/p:RestorePackagesConfig=true'
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed ($LASTEXITCODE)." }
}
& $MSBuildPath @buildArgs '/t:Build'
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
Write-Output ('Built: ' + (Join-Path $buildOutputRoot 'bin\Spotlight\Spotlight.exe'))
