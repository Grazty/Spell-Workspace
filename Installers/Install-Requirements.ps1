param([switch]$CheckOnly, [switch]$VerifyOnly, [switch]$Json)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installers.json') -Raw | ConvertFrom-Json

function Refresh-ToolsPath {
    $paths = @($env:Path, [Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User'))
    $env:Path = ($paths | Where-Object { $_ }) -join ';'
}
function Get-BuildRequirements {
    Refresh-ToolsPath
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    $sdk = $false; $core = $false; $aspnet = $false
    if ($dotnet) {
        $sdk = @(& $dotnet.Source --list-sdks 2>$null | Where-Object { $_ -match '^10\.0\.\d+\s' }).Count -gt 0
        $runtimes = @(& $dotnet.Source --list-runtimes 2>$null)
        foreach ($line in $runtimes) {
            if ($line -match '^Microsoft\.NETCore\.App (10\.0\.\d+)\s' -and [version]$Matches[1] -ge [version]'10.0.8') { $core = $true }
            if ($line -match '^Microsoft\.AspNetCore\.App (10\.0\.\d+)\s' -and [version]$Matches[1] -ge [version]'10.0.8') { $aspnet = $true }
        }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $compiler = $false
    if (Test-Path -LiteralPath $vswhere) {
        $locations = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
        foreach ($location in $locations) {
            $compiler = (Test-Path -LiteralPath (Join-Path $location 'VC\Auxiliary\Build\vcvars64.bat')) -and
                @(Get-ChildItem -Path (Join-Path $location 'VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe') -ErrorAction SilentlyContinue).Count -gt 0
        }
    }
    $kits = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
    $windowsSdk = $false
    if ($kits -and $kits.KitsRoot10) {
        foreach ($include in @(Get-ChildItem -LiteralPath (Join-Path $kits.KitsRoot10 'Include') -Directory -ErrorAction SilentlyContinue)) {
            if ((Test-Path -LiteralPath (Join-Path $include.FullName 'um\Windows.h')) -and
                (Test-Path -LiteralPath (Join-Path $include.FullName 'ucrt\stdio.h')) -and
                (Test-Path -LiteralPath (Join-Path $kits.KitsRoot10 ('Lib\' + $include.Name + '\um\x64\kernel32.lib')))) { $windowsSdk = $true; break }
        }
    }
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    @(
        [pscustomobject]@{ Id = 'dotnet'; Name = '.NET 10 SDK and server runtimes'; Ready = [bool]($sdk -and $core -and $aspnet) }
        [pscustomobject]@{ Id = 'cpp'; Name = 'C++ x64 compiler and Windows SDK'; Ready = [bool]($compiler -and $windowsSdk) }
        [pscustomobject]@{ Id = 'git'; Name = 'Git (for source and world-data setup)'; Ready = [bool]$git }
    )
}
function Test-Installer($item) {
    if ([IO.Path]::GetFileName($item.File) -ne $item.File) { throw 'Invalid installer filename in manifest.' }
    $file = Join-Path $PSScriptRoot $item.File
    if (-not (Test-Path -LiteralPath $file)) { throw ('Missing installer: ' + $item.File + '. Use the App ZIP with installers, or download this file from ' + $item.Source) }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.Sha256) { throw ('Installer checksum failed: ' + $item.File) }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -ne $item.Publisher) { throw ('Installer signature failed: ' + $item.File) }
    return $file
}
function Install-Requirement([string]$id) {
    $item = $manifest.Installers | Where-Object { $_.Id -eq $id } | Select-Object -First 1
    if (-not $item) { throw 'Unknown installer choice.' }
    if ((Get-BuildRequirements | Where-Object { $_.Id -eq $id }).Ready) { Write-Host ($item.Name + ' is already ready. Skipping.') -ForegroundColor Green; return }
    $file = Test-Installer $item
    Write-Host ('Opening ' + $item.Name + '. Follow its installer, then return here.') -ForegroundColor Cyan
    if ($id -eq 'cpp') { Write-Host 'C++ setup downloads several GB from Microsoft. The x64 compiler and Windows SDK are preselected.' }
    $start = @{ FilePath = $file; PassThru = $true; Wait = $true }
    if ($item.Arguments.Count -gt 0) { $start.ArgumentList = @($item.Arguments) }
    $process = Start-Process @start
    if ($process.ExitCode -eq 3010 -or $process.ExitCode -eq 1641) { Write-Host 'Installation requires a Windows restart. Restart before using Build All.' -ForegroundColor Yellow }
    elseif ($process.ExitCode -ne 0) { throw ($item.Name + ' did not finish. Installer exit code: ' + $process.ExitCode) }
    Refresh-ToolsPath
}
try {
    if ($VerifyOnly) {
        $results = @($manifest.Installers | ForEach-Object { $null = Test-Installer $_; [pscustomobject]@{ File = $_.File; Verified = $true } })
        if ($Json) { $results | ConvertTo-Json } else { $results | Format-Table -AutoSize }
        exit 0
    }
    if ($CheckOnly) {
        $results = @(Get-BuildRequirements)
        if ($Json) { $results | ConvertTo-Json } else { $results | Select-Object Name, Ready | Format-Table -AutoSize }
        exit 0
    }
    do {
        Write-Host "`nSpell Workspace - Build Requirements" -ForegroundColor Cyan
        Write-Host 'Opening the editor needs no installation. These tools are for Build All.'
        Write-Host 'The editor stays portable. Microsoft/Git installers install Windows tools normally.'
        $requirements = @(Get-BuildRequirements)
        $requirements | Select-Object Name, @{ Name = 'Status'; Expression = { if ($_.Ready) { 'Installed - skipped' } else { 'Missing' } } } | Format-Table -AutoSize
        if (@($requirements | Where-Object { -not $_.Ready }).Count -eq 0) {
            Write-Host 'All build requirements are installed. Nothing needs to be downloaded or installed.' -ForegroundColor Green
            $null = Read-Host 'Press Enter to close'
            break
        }
        Write-Host '1. Install missing build requirements (recommended)'
        Write-Host '2. Check/install .NET 10 SDK only'
        Write-Host '3. Check/install C++ compiler + Windows SDK only (internet required)'
        Write-Host '4. Check/install Git only'
        Write-Host '5. Check again'
        Write-Host '0. Exit'
        $choice = Read-Host 'Choose a number'
        switch ($choice) {
            '1' { foreach ($id in @('dotnet', 'cpp', 'git')) { Install-Requirement $id } }
            '2' { Install-Requirement 'dotnet' }
            '3' { Install-Requirement 'cpp' }
            '4' { Install-Requirement 'git' }
            '0' { break }
            '5' { }
            default { Write-Host 'Choose one of the numbers above.' }
        }
    } while ($choice -ne '0')
    Write-Host 'Close and reopen Spell Workspace after installing tools so it can see them.'
    exit 0
} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
