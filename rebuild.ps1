& "$PSScriptRoot\..\redbamboo-packages\dotnet\rebuild.ps1" `
    -AppName Nova `
    -Port 18803 `
    -PackageManager pnpm `
    -FrontendDir "$PSScriptRoot\web" `
    -BuildTarget "$PSScriptRoot\Nova.sln" `
    -ExePath "$PSScriptRoot\src\Nova.App\bin\Release\net9.0-windows\Nova.exe"
