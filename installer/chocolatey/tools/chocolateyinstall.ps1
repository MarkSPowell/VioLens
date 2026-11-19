$ErrorActionPreference = 'Stop'

# Downloads the signed installer from the GitHub release and runs it
# silently for all users. Inno Setup's default tasks apply: the NetLog
# importer and CSP Rule Collector extensions, no desktop shortcut.
$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  fileType       = 'exe'
  url64bit       = 'https://github.com/MarkSPowell/VioLens/releases/download/v1.0.0-rc.3/VioLens-Setup-1.0.0-rc.3.exe'
  checksum64     = '2acf13fe589f0869d7e712e82ee1a886f20c2329bc2df27e26fa06367a2b135b'
  checksumType64 = 'sha256'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /ALLUSERS'
  validExitCodes = @(0)
  softwareName   = 'VioLens*'
}

Install-ChocolateyPackage @packageArgs
