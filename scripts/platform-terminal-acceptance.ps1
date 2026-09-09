[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Windows", "LinuxX11")]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string]$SystemName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string]$Observer,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string]$BuildLabel,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PackagePath,

    [string]$EvidenceDirectory = "artifacts/platform-acceptance"
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $scriptDirectory
$workspaceDotnetName = if ($IsWindows) { "dotnet.exe" } else { "dotnet" }
$workspaceDotnet = Join-Path $repositoryRoot ".dotnet/$workspaceDotnetName"
$dotnet = if (Test-Path -LiteralPath $workspaceDotnet -PathType Leaf) {
    $workspaceDotnet
} else {
    $command = Get-Command dotnet -ErrorAction Stop
    $command.Source
}
$runnerProject = Join-Path $repositoryRoot "tools/Asura.TerminalAcceptance/Asura.TerminalAcceptance.csproj"

& $dotnet run --project $runnerProject -- run `
    --platform $Platform `
    --system-name $SystemName `
    --observer $Observer `
    --build-label $BuildLabel `
    --package $PackagePath `
    --evidence-dir $EvidenceDirectory
exit $LASTEXITCODE
