[CmdletBinding()]
param(
    [string]$WslDistribution,
    [switch]$AskPass,
    [switch]$AskBecomePass,
    [switch]$AskVaultPass,
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw 'WSL is required. Install an Ubuntu WSL distribution, then run this command again.'
}

$wslOptions = @()
if ($WslDistribution) {
    $wslOptions += @('--distribution', $WslDistribution)
}

& wsl.exe @wslOptions --exec true 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'No usable WSL distribution was found. Install Ubuntu in WSL and ensure it starts successfully.'
}

$wslRepository = (& wsl.exe @wslOptions --exec wslpath -a $PSScriptRoot).Trim()
if ($LASTEXITCODE -ne 0 -or -not $wslRepository) {
    throw 'Could not translate the repository path into the selected WSL distribution.'
}

$forwardedArgs = @()
if ($AskPass) { $forwardedArgs += '--ask-pass' }
if ($AskBecomePass) { $forwardedArgs += '--ask-become-pass' }
if ($AskVaultPass) { $forwardedArgs += '--ask-vault-pass' }
$forwardedArgs += $ExtraArgs

Write-Host 'Starting the Commando deployment through WSL...'
& wsl.exe @wslOptions --exec bash "$wslRepository/ansible/deploy.sh" @forwardedArgs
if ($LASTEXITCODE -ne 0) {
    throw "Commando deployment failed with exit code $LASTEXITCODE."
}

Write-Host 'Commando deployment completed successfully.'
