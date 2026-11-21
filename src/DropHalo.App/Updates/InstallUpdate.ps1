param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference = 'Stop'
$r = Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
$directory = Split-Path -Parent $Request
$ready = Join-Path $directory 'ready'
$approval = Join-Path $directory 'install'
$result = Join-Path $r.DataDirectory 'update-result.json'
$lock = $null
$previous = $null
$restart = $false
$installed = $false
function Quote([string]$value) { '"' + (($value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' }
function Property($database, [string]$name) {
    $view = $null
    $record = $null
    try {
        $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$name'")
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "Missing MSI property $name" }
        $record.StringData(1)
    } finally {
        if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
        if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    }
}
try {
    $previous = [Diagnostics.Process]::GetProcessById([int]$r.ParentId)
    $null = $previous.Handle # Keep the original process identity through the handoff.
    # Hold a read-only handle through installation so verified bytes cannot be replaced.
    $lock = [IO.File]::Open($r.Installer, 'Open', 'Read', 'Read')
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $actualHash = ([BitConverter]::ToString($sha.ComputeHash($lock)) -replace '-', '')
    } finally {
        $sha.Dispose()
    }
    $lock.Position = 0
    if ($lock.Length -ne $r.Size -or $actualHash -ne $r.Sha256) { throw 'Installer checksum mismatch' }
    $signature = Get-AuthenticodeSignature -LiteralPath $r.Installer
    $publisher = 'CN="Alphanutix, LLC", O="Alphanutix, LLC", L=Middletown, S=Delaware, C=US'
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -cne $publisher) { throw 'Installer publisher is not trusted' }
    if ($r.Packaged) {
        Add-Type -AssemblyName System.IO.Compression
        $zip = [IO.Compression.ZipArchive]::new($lock, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            $manifestEntry = $zip.GetEntry('AppxManifest.xml')
            if ($null -eq $manifestEntry) { throw 'MSIX manifest is missing' }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $identity = $manifest.Package.Identity
            if ($identity.Name -cne 'GavinLee.DropHalo' -or $identity.Publisher -cne $publisher -or $identity.Version -ne ($r.Version + '.0') -or $identity.ProcessorArchitecture -ne $r.Architecture) { throw 'MSIX identity mismatch' }
        } finally { $zip.Dispose(); $lock.Position = 0 }
    } else {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $null
        try {
            $database = $installer.OpenDatabase($r.Installer, 0)
            if ((Property $database 'UpgradeCode') -ne '{844E4C2C-7018-4368-A79A-002B046C41B8}' -or (Property $database 'ProductVersion') -ne $r.Version -or (Property $database 'ProductName') -cne 'DropHalo') { throw 'MSI identity mismatch' }
        } finally {
            if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
            if ($null -ne $installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
        }
    }
    [IO.File]::WriteAllText("$ready.tmp", 'verified')
    [IO.File]::Move("$ready.tmp", $ready)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($approval)) {
        if ($previous.HasExited) {
            if ([IO.File]::Exists($approval)) { break }
            throw 'DropHalo exited without approving installation'
        }
        if ($deadline.Elapsed.TotalSeconds -ge 30) { throw 'DropHalo did not approve installation' }
        Start-Sleep -Milliseconds 100
    }
    if (-not $previous.WaitForExit(30000)) { throw 'DropHalo did not exit; installation canceled' }
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
    if ($previous) { $previous.Dispose() }
    if ($restart) {
        $executable = $r.Executable
        if ($r.Packaged) {
            $package = @(Get-AppxPackage -Name 'GavinLee.DropHalo') | Where-Object { $_ -and $_.Publisher -ceq $publisher } | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
            if ($package) {
                $appsRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\WindowsApps'
                $familyAlias = if ($package.PackageFamilyName) { Join-Path (Join-Path $appsRoot $package.PackageFamilyName) 'drophalo.exe' } else { $null }
                $globalAlias = Join-Path $appsRoot 'drophalo.exe'
                if ($familyAlias -and [IO.File]::Exists($familyAlias)) {
                    $executable = $familyAlias
                } elseif ([IO.File]::Exists($globalAlias)) {
                    $executable = $globalAlias
                } elseif ($package.InstallLocation) {
                    $executable = Join-Path $package.InstallLocation 'DropHalo.exe'
                }
            }
        } elseif ($installed) {
            $executable = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'DropHalo\DropHalo.exe'
        }
        try {
            $workDir = if ($r.Packaged) { [IO.Path]::GetTempPath() } else { Split-Path -Parent $executable }
            if (-not $workDir -or -not [IO.Directory]::Exists($workDir)) { $workDir = [IO.Path]::GetTempPath() }
            Start-Process -FilePath $executable -WorkingDirectory $workDir -ArgumentList ('--data-dir ' + (Quote $r.DataDirectory) + ' --settings')
        } catch {
            try {
                [IO.File]::AppendAllText((Join-Path $r.DataDirectory 'update-restart-error.log'), "$([DateTimeOffset]::Now.ToString('o')) $($_.Exception.Message)`r`n")
            } catch { }
        }
    }
    foreach ($tempFile in @($r.Installer, $Request, $ready, "$ready.tmp", $approval)) {
        if ($tempFile) { Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue }
    }
    if ($installed) {
        Remove-Item -LiteralPath (Join-Path $directory 'install.log') -Force -ErrorAction SilentlyContinue
        if (@(Get-ChildItem -LiteralPath $directory -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue
        }
    }
}
