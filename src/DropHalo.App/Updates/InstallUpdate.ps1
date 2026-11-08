param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference = 'Stop'
$r = Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
$directory = Split-Path -Parent $Request
$ready = Join-Path $directory 'ready'
$result = Join-Path $r.DataDirectory 'update-result.json'
$lock = $null
$restart = $false
$installed = $false
function Quote([string]$value) { '"' + (($value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
function Property($database, [string]$name) {
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$name'")
    $view.Execute()
    $record = $view.Fetch()
    if ($null -eq $record) { throw "Missing MSI property $name" }
    $record.StringData(1)
}
try {
    # Hold a read-only handle through installation so verified bytes cannot be replaced.
    $lock = [IO.File]::Open($r.Installer, 'Open', 'Read', 'Read')
    if ($lock.Length -ne $r.Size -or (Get-FileHash -LiteralPath $r.Installer -Algorithm SHA256).Hash -ne $r.Sha256) { throw 'Installer checksum mismatch' }
    $signature = Get-AuthenticodeSignature -LiteralPath $r.Installer
    $publisher = 'CN="Alphanutix, LLC", O="Alphanutix, LLC", L=Middletown, S=Delaware, C=US'
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -cne $publisher) { throw 'Installer publisher is not trusted' }
    if ($r.Packaged) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($r.Installer)
        try {
            $reader = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $identity = $manifest.Package.Identity
            if ($identity.Name -cne 'GavinLee.DropHalo' -or $identity.Publisher -cne $publisher -or $identity.Version -ne ($r.Version + '.0') -or $identity.ProcessorArchitecture -ne $r.Architecture) { throw 'MSIX identity mismatch' }
        } finally { $zip.Dispose() }
    } else {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($r.Installer, 0)
        try {
            if ((Property $database 'UpgradeCode') -ne '{844E4C2C-7018-4368-A79A-002B046C41B8}' -or (Property $database 'ProductVersion') -ne $r.Version -or (Property $database 'ProductName') -cne 'DropHalo') { throw 'MSI identity mismatch' }
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
    Set-Content -LiteralPath $ready -Value 'verified' -Encoding ASCII
    $previous = Get-Process -Id $r.ParentId -ErrorAction SilentlyContinue
    if ($previous -and -not $previous.WaitForExit(30000)) { throw 'DropHalo did not exit; installation canceled' }
    $restart = $true
    $reboot = $false
    if ($r.Packaged) {
        Add-AppxPackage -Path $r.Installer -ErrorAction Stop
    } else {
        $arguments = '/i ' + (Quote $r.Installer) + ' /passive /norestart /log ' + (Quote (Join-Path $directory 'install.log'))
        $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
        if ($process.ExitCode -notin @(0,3010)) { throw "Windows Installer returned $($process.ExitCode)" }
        $reboot = $process.ExitCode -eq 3010
    }
    $installed = $true
    @{ Success=$true; Version=$r.Version; RebootRequired=$reboot } | ConvertTo-Json | Set-Content -LiteralPath $result -Encoding UTF8
} catch {
    @{ Success=$false; Version=$r.Version; Error=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $result -Encoding UTF8
} finally {
    if ($lock) { $lock.Dispose() }
    if ($restart) {
        $executable = $r.Executable
        if ($r.Packaged) {
            $package = Get-AppxPackage -Name 'GavinLee.DropHalo'
            if ($package) { $executable = Join-Path $package.InstallLocation 'DropHalo.exe' }
        } elseif ($installed) {
            $executable = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'DropHalo\DropHalo.exe'
        }
        Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -ArgumentList ('--data-dir ' + (Quote $r.DataDirectory) + ' --settings')
    }
    Remove-Item -LiteralPath $r.Installer -Force -ErrorAction SilentlyContinue
}
