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
