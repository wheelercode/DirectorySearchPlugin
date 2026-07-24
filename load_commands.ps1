$ProjectRoot = "C:\Users\wheel\Documents\code\C#\DirectorySearchPlugin"

function killpt {
    Get-Process -Name *PowerToys* -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function buildp {
    killpt
    Start-Sleep -Milliseconds 500

    Push-Location $ProjectRoot
    try {
        dotnet build `
            ".\DirectoryIndexPlugin\DirectorySearchPlugin.csproj" `
            -c Debug
    }
    finally {
        Pop-Location
    }
}

function buildi {
    killpt
    Start-Sleep -Milliseconds 500

    Push-Location $ProjectRoot
    try {
        dotnet build `
            ".\DirectorySearchIndexer\DirectorySearchIndexer.csproj" `
            -c Debug
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
    }
    finally {
        git status
        Pop-Location
    }
}

Write-Host "Commands: killpt, buildp, buildi, and gitall have been loaded."