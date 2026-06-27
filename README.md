# Speed Limit Unlocker

Third-party ETS2LA plugin that keeps the global ACC target speed from being reset to the road speed limit.

This does not modify ETS2LA or the closed-source ACC plugin. It uses the public plugin API and writes `ApplicationState.Current.DesiredSpeed` after ETS2LA handles speed-limit changes.

## Build

```powershell
dotnet build .\SpeedLimitUnlocker.csproj -c Release
```

Copy `bin\Release\SpeedLimitUnlocker.dll` into the manual plugin folder root:

```text
Release-ETS2LA-win-beta-Portable\current\Plugins\SpeedLimitUnlocker.dll
```

Restart ETS2LA, then enable "Speed Limit Unlocker" in the plugin manager.

In the plugin settings, "Limit maximum target speed" is off by default. In the JSON settings file this is stored as `MaximumTargetSpeed: 9999`.
