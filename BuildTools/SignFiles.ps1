<#
.SYNOPSIS
    Signs Genesis's own executables and libraries in a published folder, when signing is set up.

.DESCRIPTION
    Signing is off until GENESIS_SIGN_COMMAND is set. Its value is the command that signs one
    file, with $f where the file goes; the same command signs the installer and its
    uninstaller (Installer/CompileInstaller.ps1 passes it to Inno Setup). For example, with
    Microsoft's Trusted Signing:

        signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib "C:\Tools\Azure.CodeSigning.Dlib.dll" /dmdf "C:\Tools\metadata.json" $f

    or with a certificate installed in the Windows certificate store:

        signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 <thumbprint> $f

    Only Genesis's own files are signed: the libraries by others are signed by their publishers
    or not at all, and re-signing them would claim them as Genesis's.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$command = $env:GENESIS_SIGN_COMMAND
if ([string]::IsNullOrWhiteSpace($command)) {
    Write-Host 'Signing is not set up (GENESIS_SIGN_COMMAND is empty); nothing signed.'
    exit 0
}
if (-not $command.Contains('$f')) {
    throw 'GENESIS_SIGN_COMMAND must contain $f where the file to sign goes.'
}

$own = @()
foreach ($folder in @($PublishDirectory, (Join-Path $PublishDirectory 'Player'))) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
    $own += @(Get-ChildItem -LiteralPath $folder -File | Where-Object {
        $_.Name -in @('Genesis Application.exe', 'Genesis Application.dll', 'GenesisEngine.exe', 'GenesisEngine.dll') -or
        ($_.Name -like 'Genesis.*.dll')
    })
}
if ($own.Count -eq 0) { throw "No Genesis executables or libraries found under $PublishDirectory." }

foreach ($file in $own) {
    $line = $command.Replace('$f', '"' + $file.FullName + '"')
    & cmd.exe /d /s /c $line
    if ($LASTEXITCODE -ne 0) { throw "Signing failed (exit $LASTEXITCODE) for $($file.FullName)" }
}
Write-Host "Signed $($own.Count) Genesis files."
exit 0
