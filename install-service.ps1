param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)

    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $shellPath = Join-Path $PSHOME "pwsh.exe"

    if (-not (Test-Path $shellPath)) {
        $shellPath = Join-Path $PSHOME "powershell.exe"
    }

    $argumentText = (
        "-NoProfile -ExecutionPolicy Bypass " +
        "-File `"$PSCommandPath`" " +
        "-Configuration $Configuration"
    )

    $elevated = Start-Process `
        -FilePath $shellPath `
        -Verb RunAs `
        -ArgumentList $argumentText `
        -Wait `
        -PassThru

    if ($elevated.ExitCode -ne 0) {
        throw "Elevated service installation failed."
    }

    return
}

$serviceName = "WheelercodeDirectorySearch"
$projectRoot = $PSScriptRoot
$projectPath = Join-Path `
    $projectRoot `
    "DirectorySearchIndexer\DirectorySearchIndexer.csproj"

$publishDirectory = Join-Path `
    $projectRoot `
    "artifacts\DirectorySearchService"

$installDirectory = Join-Path `
    $env:ProgramFiles `
    "Wheelercode\DirectorySearchPlugin"

$dataDirectory = Join-Path `
    $env:ProgramData `
    "Wheelercode\DirectorySearchPlugin"

$serviceExecutable = Join-Path `
    $installDirectory `
    "DirectorySearchIndexer.exe"

if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}

dotnet publish `
    $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "The directory index service failed to publish."
}

$existingService = Get-Service `
    -Name $serviceName `
    -ErrorAction SilentlyContinue

if ($null -ne $existingService) {
    if ($existingService.Status -ne "Stopped") {
        Stop-Service `
            -Name $serviceName `
            -Force
    }

    & sc.exe delete $serviceName | Out-Null

    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($null -eq (
                Get-Service `
                    -Name $serviceName `
                    -ErrorAction SilentlyContinue)) {
            break
        }

        Start-Sleep -Milliseconds 100
    }

    if ($null -ne (
            Get-Service `
                -Name $serviceName `
                -ErrorAction SilentlyContinue)) {
        throw "The previous service is still marked for deletion."
    }
}

if (Test-Path $installDirectory) {
    Remove-Item $installDirectory -Recurse -Force
}

New-Item `
    -ItemType Directory `
    -Path $installDirectory `
    -Force | Out-Null

Copy-Item `
    -Path (Join-Path $publishDirectory "*") `
    -Destination $installDirectory `
    -Recurse `
    -Force

New-Item `
    -ItemType Directory `
    -Path $dataDirectory `
    -Force | Out-Null

& icacls.exe `
    $dataDirectory `
    "/inheritance:r" `
    "/grant:r" `
    "*S-1-5-18:(OI)(CI)F" `
    "*S-1-5-32-544:(OI)(CI)F" `
    "*S-1-5-32-545:(OI)(CI)RX" | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "Unable to set the shared index directory permissions."
}

$quotedExecutable = "`"$serviceExecutable`""

$createOutput = & sc.exe create `
    $serviceName `
    "binPath=" $quotedExecutable `
    "start=" "delayed-auto" `
    "obj=" "LocalSystem" `
    "DisplayName=" "Wheelercode Directory Search"

$createExitCode = $LASTEXITCODE

if ($createExitCode -ne 0) {
    $createDetails = $createOutput -join [Environment]::NewLine

    throw (
        "Unable to register the directory index service. " +
        "sc.exe exit code: $createExitCode" +
        [Environment]::NewLine +
        $createDetails
    )
}

& sc.exe description `
    $serviceName `
    "Maintains the Wheelercode PowerToys directory index." |
    Out-Null

& sc.exe failure `
    $serviceName `
    "reset=" "86400" `
    "actions=" "restart/5000/restart/15000/restart/60000" |
    Out-Null

& sc.exe failureflag `
    $serviceName `
    "1" |
    Out-Null

Start-Service -Name $serviceName

Write-Host ""
Write-Host "Wheelercode Directory Search service installed."
Write-Host "Service: $serviceName"
Write-Host "Program: $serviceExecutable"
Write-Host "Index:   $dataDirectory"
