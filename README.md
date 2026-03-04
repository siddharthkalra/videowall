# VideoWallpaper

Windows 11 WPF app (C#) for live wallpaper playback.

## Step 0 status
- Solution scaffolded with .NET 8 WPF app.
- Project: `VideoWallpaper.App`
- NuGet package added: `Microsoft.Web.WebView2`
- Wallpaper hosting not implemented yet.

## Prerequisites
- Windows 11
- .NET 8 SDK

## Build
```powershell
cd VideoWallpaper
$env:PATH = "C:\Users\siddh\.dotnet;" + $env:PATH
 dotnet restore .\VideoWallpaper.sln
 dotnet build .\VideoWallpaper.sln -c Release
```

## Run
```powershell
cd VideoWallpaper
$env:PATH = "C:\Users\siddh\.dotnet;" + $env:PATH
 dotnet run --project .\VideoWallpaper.App\VideoWallpaper.App.csproj
```

## Self-Contained EXE (No .NET runtime required on target machine)
```powershell
cd VideoWallpaper
$env:PATH = "C:\Users\siddh\.dotnet;" + $env:PATH
dotnet publish .\VideoWallpaper.App\VideoWallpaper.App.csproj -c Release -r win-x64 --self-contained true
```

Published app location:
`.\VideoWallpaper.App\bin\Release\net8.0-windows\win-x64\publish\VideoWallpaper.App.exe`
