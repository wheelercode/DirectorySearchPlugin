$ProjectRoot = "C:\Users\wheel\Documents\code\C#\DirectorySearchPlugin"

function killp {
    Get-Process -Name *PowerToys* -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function buildp {
    killp
    Start-Sleep -Milliseconds 500

    Push-Location $ProjectRoot
    try {
        dotnet build ".\DirectorySearchPlugin\DirectorySearchPlugin.csproj" -c Debug
    }
    finally {
        Pop-Location
    }
}

function buildi{
    killp
    Start-Sleep -Milliseconds 500

    Push-Location $ProjectRoot
    try {
        dotnet build ".\DirectorySearchIndexer\DirectorySearchIndexer.csproj" -c Debug
    }
    finally {
        Pop-Location
    }
}

function gitall {
    Push-Location $ProjectRoot

    try {
        git add .

        if ($LASTEXITCODE -ne 0) {
            return
        }

        $message = Read-Host "Commit message"

        if ([string]::IsNullOrWhiteSpace($message)) {
            Write-Host "Commit cancelled: message was empty."
            return
        }

        git commit -m $message

        if ($LASTEXITCODE -ne 0) {
            return
        }

        git push origin master

        Write-Host "repo: wheelercode.com/wheelercode/directorysearchplugin"
        Write-Host "branch: master"
        Write-Host "commit: "

        git rev-parse HEAD
    }
    finally {
        git status
        Pop-Location
    }
}

function runp {
    Start-Process "C:\Program Files\PowerToys\PowerToys.exe"
}

function runi {
    Start-Process "C:\Users\wheel\Documents\code\C#\DirectorySearchPlugin\DirectorySearchIndexer\bin\Debug\net10.0-windows10.0.26100.0\DirectorySearchIndexer.exe"
}

Write-Host "Commands: buildp, buildi, runp, runi, and gitall are now available."