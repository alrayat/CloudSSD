<#
.SYNOPSIS
  Builds, checks or runs CloudSSD from the source checkout using the existing .NET 10 SDK.
#>
param(
    [ValidateSet('Build', 'Test', 'Run', 'DesktopChecks')]
    [string]$Action = 'Build',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetExe = if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    Join-Path $env:DOTNET_ROOT 'dotnet.exe'
} elseif (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) {
    Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
} else { 'dotnet' }
if (-not ((& $dotnetExe --list-sdks) -match '^10\.')) { throw 'CloudSSD requires an existing .NET 10 SDK. Select it with DOTNET_ROOT.' }
function Get-SingleProject([string]$folder, [string]$namePattern) {
    $projectMatches = @(Get-ChildItem -LiteralPath (Join-Path $repoPath $folder) -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File
    } | Where-Object { $_.BaseName -match $namePattern })
    if ($projectMatches.Count -ne 1) { throw "Expected exactly one $Action project; found $($projectMatches.Count)." }
    return $projectMatches[0].FullName
}
Push-Location $repoPath
try {
    switch ($Action) {
        'Build' {
            $solutions = @(Get-ChildItem -LiteralPath $repoPath -Filter '*.slnx' -File)
            if ($solutions.Count -ne 1) { throw 'Expected exactly one solution in the source checkout.' }
            & $dotnetExe build $solutions[0].FullName -c $Configuration
        }
        'Test' {
            $projectPath = Get-SingleProject 'tests' '\.Tests$'
            & $dotnetExe test $projectPath -c $Configuration
        }
        'Run' {
            $projectPath = Get-SingleProject 'src' '\.Desktop$'
            & $dotnetExe run --project $projectPath -c $Configuration
        }
        'DesktopChecks' {
            $projectPath = Get-SingleProject 'tests' '\.DesktopChecks$'
            & $dotnetExe run --project $projectPath -c $Configuration -- '.work/desktop-checks'
        }
    }
    $commandExitCode = $LASTEXITCODE
} finally { Pop-Location }
exit $commandExitCode
