param([string]$Request)
$ErrorActionPreference = 'Stop'
$installerScript = Join-Path $PSScriptRoot '..\..\src\DropHalo.App\Updates\InstallUpdate.ps1'
$publisher = 'CN="Alphanutix, LLC", O="Alphanutix, LLC", L=Middletown, S=Delaware, C=US'

if ($Request) {
    $dir = Split-Path -Parent $Request
    function Get-AuthenticodeSignature([string]$LiteralPath) {
        @{ Status = 'Valid'; SignerCertificate = @{ Subject = $publisher } }
    }
    function Add-AppxPackage {
        [CmdletBinding()] param([string]$Path)
        [IO.File]::WriteAllText((Join-Path $dir 'installed'), $Path)
    }
    function Get-AppxPackage([string]$Name) { }
    function Start-Process([string]$FilePath, [string]$WorkingDirectory, [string]$ArgumentList) {
        [IO.File]::WriteAllText((Join-Path $dir 'restarted'), $FilePath)
    }
    & $installerScript -Request $Request
    exit 0
}

function Start-TestProcess([string]$argsText) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $PSHOME 'powershell.exe'
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass ' + $argsText
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    [Diagnostics.Process]::Start($start)
}

function Verify([bool]$approve) {
    $dir = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('drophalo-handoff-' + [Guid]::NewGuid())))
    $parent = $helper = $null
    try {
        $installer = Join-Path $dir.FullName 'package.msix'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::Open($installer, 'Create')
        try {
            $identity = $zip.CreateEntry('AppxManifest.xml')
            $writer = [IO.StreamWriter]::new($identity.Open())
            try {
                $escaped = [Security.SecurityElement]::Escape($publisher)
                $writer.Write("<Package><Identity Name='GavinLee.DropHalo' Publisher='$escaped' Version='0.1.7.0' ProcessorArchitecture='arm64'/></Package>")
            } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }

        $parent = Start-TestProcess '-Command "Start-Sleep -Seconds 60"'
        $requestPath = Join-Path $dir.FullName 'request.json'
        @{
            ParentId = $parent.Id; Installer = $installer; Size = (Get-Item $installer).Length
            Sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash; Version = '0.1.7'
            Architecture = 'arm64'; Packaged = $true; DataDirectory = $dir.FullName; Executable = 'DropHalo.exe'
        } | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding UTF8
        $helper = Start-TestProcess ('-File "' + $PSCommandPath + '" -Request "' + $requestPath + '"')
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath (Join-Path $dir.FullName 'ready'))) {
            if ($helper.HasExited -or $deadline.Elapsed.TotalSeconds -ge 15) { throw 'Helper did not become ready' }
            Start-Sleep -Milliseconds 50
        }

        if ($approve) { [IO.File]::WriteAllText((Join-Path $dir.FullName 'install'), '') }
        $finishedWhileParentLived = $helper.WaitForExit(250)
        if ($finishedWhileParentLived -or (Test-Path -LiteralPath (Join-Path $dir.FullName 'installed'))) { throw 'Helper installed before parent exit' }
        $parent.Kill()
        if (!$helper.WaitForExit(10000)) { throw 'Helper did not settle after parent exit' }
        $result = Get-Content -LiteralPath (Join-Path $dir.FullName 'update-result.json') -Raw | ConvertFrom-Json
        $didInstall = Test-Path -LiteralPath (Join-Path $dir.FullName 'installed')
        $didRestart = Test-Path -LiteralPath (Join-Path $dir.FullName 'restarted')
        if ($result.Success -ne $approve -or $didInstall -ne $approve -or $didRestart -ne $approve) {
            throw "Unexpected handoff outcome. Approved=$approve Installed=$didInstall Restarted=$didRestart Result=$($result | ConvertTo-Json -Compress)"
        }
        "PASS approve=$approve installed=$didInstall restarted=$didRestart"
    } finally {
        foreach ($process in @($helper, $parent)) {
            if ($process) {
                if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
                $process.Dispose()
            }
        }
        $dir.Delete($true)
    }
}

Verify $false
Verify $true
