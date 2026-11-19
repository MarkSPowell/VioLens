$ErrorActionPreference = 'Stop'

# Runs VioLens's own Inno Setup uninstaller silently.
[array]$keys = Get-UninstallRegistryKey -SoftwareName 'VioLens*'

if ($keys.Count -eq 1) {
  $packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileType       = 'exe'
    silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    file           = ($keys[0].UninstallString -replace '"', '')
    validExitCodes = @(0)
  }
  Uninstall-ChocolateyPackage @packageArgs
} elseif ($keys.Count -eq 0) {
  Write-Warning "$env:ChocolateyPackageName was already uninstalled by other means."
} else {
  Write-Warning "$($keys.Count) matches found for VioLens. Nothing was uninstalled; remove it from Apps and Features instead."
  $keys | ForEach-Object { Write-Warning "- $($_.DisplayName)" }
}
