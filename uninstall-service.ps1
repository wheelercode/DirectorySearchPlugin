param(
    [switch]$RemoveData
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

    $removeDataArgument = if ($RemoveData) {
        " -RemoveData"
    }
    else {
        ""
    }

    $argumentText = (
        "-NoProfile -ExecutionPolicy Bypass " +
        "-File `"$PSCommandPath`"$removeDataArgument"
    )

    $elevated = Start-Process `
        -FilePath $shellPath `
        -Verb RunAs `
        -ArgumentList $argumentText `
        -Wait `
        -PassThru

    if ($elevated.ExitCode -ne 0) {
        throw "Elevated service removal failed."
    }

    return
}

$serviceName = "WheelercodeDirectorySearch"
$installDirectory = Join-Path `
    $env:ProgramFiles `
    "Wheelercode\DirectorySearchPlugin"

$dataDirectory = Join-Path `
    $env:ProgramData `
    "Wheelercode\DirectorySearchPlugin"

$service = Get-Service `
    -Name $serviceName `
    -ErrorAction SilentlyContinue

if ($null -ne $service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service `
            -Name $serviceName `
            -Force
    }

    & sc.exe delete $serviceName | Out-Null
}

if (Test-Path $installDirectory) {
    Remove-Item $installDirectory -Recurse -Force
}

if ($RemoveData -and (Test-Path $dataDirectory)) {
    Remove-Item $dataDirectory -Recurse -Force
}

Write-Host ""
Write-Host "Wheelercode Directory Search service uninstalled."

if (-not $RemoveData) {
    Write-Host "Existing index data was preserved at:"
    Write-Host $dataDirectory
}
