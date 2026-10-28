param([Parameter(Mandatory)][string]$InstallDirectory, [int]$ParentProcessId)
$ErrorActionPreference='Stop'
$target=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')) { throw 'Refusing a drive root.' }
$exe=Join-Path $target 'CloudSSD.exe'
if (-not (Test-Path -LiteralPath $exe) -or (Get-Item -LiteralPath $exe).VersionInfo.ProductName -ne 'CloudSSD') { throw 'Target is not a verified CloudSSD installation.' }
foreach ($protected in @($env:WINDIR,$env:ProgramFiles,${env:ProgramFiles(x86)},$env:USERPROFILE,$env:LOCALAPPDATA)) {
    if ($protected -and $target.Equals([IO.Path]::GetFullPath($protected).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing a protected directory.' }
}
if ($ParentProcessId -gt 0) { Wait-Process -Id $ParentProcessId -Timeout 90 -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $target -Recurse -Force
