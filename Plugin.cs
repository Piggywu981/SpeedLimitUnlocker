using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Telemetry;
using ETS2LA.Notifications;
using ETS2LA.Settings;
using ETS2LA.Shared;
using ETS2LA.State;

namespace SpeedLimitUnlocker;

[Serializable]
public sealed class SpeedLimitUnlockerSettings
{
    public int SettingsVersion { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public double DefaultTargetSpeed { get; set; } = 9999;
    public double MaximumTargetSpeed { get; set; } = 9999;
    public double RestoreDelayMs { get; set; } = 50;
    public bool RestoreWhenTargetEqualsLimit { get; set; } = true;
}

public sealed class SpeedLimitUnlockerPlugin : Plugin, IPluginUi
{
    private const string SettingsFileName = "SpeedLimitUnlocker.json";
    private const string PluginId = "local.speedlimitunlocker";

    private readonly object sync = new();
    private SettingsHandler? settingsHandler;
    private SpeedLimitUnlockerSettings settings = new();
    private GameTelemetryData latestTelemetry = new();
    private float lastSpeedLimit;
    private float preferredTargetSpeed;
    private bool suppressTargetCapture;
    private DateTime ignoreLimitTargetCaptureUntilUtc = DateTime.MinValue;
    private DateTime forceRestoreUntilUtc = DateTime.MinValue;

    public override PluginInformation Info { get; } = new()
    {
        Id = PluginId,
        Name = "Speed Limit Unlocker",
        Description = "Keeps ETS2LA's ACC target speed from being reset to the road speed limit.",
        Version = "0.6.0",
        SupportedETS2LA = "*",
        AuthorName = "local",
        Dependencies = new List<string> { "tumppi066.adaptivecruisecontrol" },
        Tags = new[] { "ACC", "Speed", "Utility" }
    };

    public override float TickRate => 60.0f;

    public override void OnEnable()
    {
        settingsHandler = new SettingsHandler();
        settings = settingsHandler.Load<SpeedLimitUnlockerSettings>(SettingsFileName);
        NormalizeSettings(saveIfChanged: true);
        settingsHandler.RegisterListener<SpeedLimitUnlockerSettings>(SettingsFileName, OnSettingsChanged);

        Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        Events.Current.Subscribe<float>("TelemetryEvents.SpeedLimitChanged", OnSpeedLimitChanged);
        Events.Current.Subscribe<EventArgs>("ETS2LA.State.AssistsUnpaused", OnAssistsUnpaused);

        lock (sync)
        {
            latestTelemetry = GameTelemetry.Current.GetCurrentData();
            lastSpeedLimit = latestTelemetry.truckFloat.speedLimit;
            preferredTargetSpeed = BuildDefaultTargetSpeed();
            CaptureExistingTargetIfUseful();
            IgnoreSpeedLimitTargetCaptureBriefly();
            ForceRestoreBriefly();
        }

        base.OnEnable();
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = "SpeedLimitUnlocker.Enabled",
            Title = "Speed Limit Unlocker",
            Content = $"Forcing target speed to {settings.DefaultTargetSpeed:0}.",
            Level = NotificationLevel.Warning,
            CloseAfter = 5
        });
        _ = RestoreAfterDelayAsync();
    }

    public override void Tick()
    {
        if (!settings.Enabled)
            return;

        lock (sync)
        {
            CaptureExistingTargetIfUseful();

            CaptureExistingTargetIfUseful();
            RestorePreferredTarget();
        }
    }

    public override void OnDisable()
    {
        base.OnDisable();
        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        Events.Current.Unsubscribe<float>("TelemetryEvents.SpeedLimitChanged", OnSpeedLimitChanged);
        Events.Current.Unsubscribe<EventArgs>("ETS2LA.State.AssistsUnpaused", OnAssistsUnpaused);
        settingsHandler?.UnregisterListener<SpeedLimitUnlockerSettings>(SettingsFileName, OnSettingsChanged);
        settingsHandler?.Dispose();
        settingsHandler = null;
    }

    public override void Shutdown()
    {
        OnDisable();
    }

    public IEnumerable<PluginPage> RenderPages()
    {
        Units units = ApplicationState.Current.DisplayUnits;
        string unit = UnitConversions.GetUnitAbbreviation(UnitType.Speed, units);

        List<UiElement> body = new()
        {
            new UiSwitch(
                "Enabled",
                "When enabled, speed limit changes no longer pin target speed to the road limit.",
                settings.Enabled,
                "enabled"),
            new UiSlider(
                $"Default target speed ({unit})",
                "Used when ETS2LA has only provided a speed-limit target and no higher user target exists yet.",
                30,
                9999,
                1,
                settings.DefaultTargetSpeed,
                "defaultTarget"),
            new UiSwitch(
                "Limit maximum target speed",
                "When off, the plugin uses 9999 as the maximum target speed.",
                settings.MaximumTargetSpeed < 9999,
                "limitMaximum")
            ,
            new UiButton(
                "Reset target to 9999",
                "Sets both the default and maximum target speed to 9999.",
                "resetTarget",
                Emphasized: true)
        };

        if (settings.MaximumTargetSpeed < 9999)
        {
            body.Add(new UiSlider(
                $"Maximum target speed ({unit})",
                "The plugin will never restore a target above this value.",
                30,
                9999,
                1,
                settings.MaximumTargetSpeed,
                "maximumTarget"));
        }
        else
        {
            body.Add(new UiText("Maximum target speed: 9999", Muted: true));
        }

        body.AddRange(new UiElement[]
        {
            new UiSlider(
                "Restore delay (ms)",
                "A short delay lets ETS2LA finish processing the speed-limit event before this plugin restores the target.",
                0,
                500,
                10,
                settings.RestoreDelayMs,
                "restoreDelay"),
            new UiSwitch(
                "Restore matching limit during ticks",
                "Also restores when the current target is detected at the current road speed limit.",
                settings.RestoreWhenTargetEqualsLimit,
                "restoreMatchingLimit"),
            BuildStatusTable(units)
        });

        yield return new PluginPage(
            "speed-limit-unlocker",
            PluginPageLocation.Settings,
            "Speed Limit Unlocker",
            "Keeps ACC target speed above road speed limits by restoring ETS2LA's global target speed.",
            body);
    }

    public void OnAction(string actionId, object? value)
    {
        switch (actionId)
        {
            case "enabled":
                settings.Enabled = value is bool enabled && enabled;
                break;
            case "defaultTarget":
                settings.DefaultTargetSpeed = ClampDouble(value, 30, 9999, settings.DefaultTargetSpeed);
                ResetPreferredTargetFromDefaults();
                break;
            case "maximumTarget":
                settings.MaximumTargetSpeed = ClampDouble(value, 30, 9999, settings.MaximumTargetSpeed < 9999 ? settings.MaximumTargetSpeed : 120);
                ClampPreferredTarget();
                break;
            case "limitMaximum":
                bool shouldLimit = value is bool limit && limit;
                settings.MaximumTargetSpeed = shouldLimit ? Math.Max(settings.DefaultTargetSpeed, 120) : 9999;
                ClampPreferredTarget();
                break;
            case "resetTarget":
                settings.DefaultTargetSpeed = 9999;
                settings.MaximumTargetSpeed = 9999;
                ResetPreferredTargetFromDefaults();
                break;
            case "restoreDelay":
                settings.RestoreDelayMs = ClampDouble(value, 0, 500, settings.RestoreDelayMs);
                break;
            case "restoreMatchingLimit":
                settings.RestoreWhenTargetEqualsLimit = value is bool restore && restore;
                break;
        }

        settingsHandler?.Save(SettingsFileName, settings);
    }

    private void OnSettingsChanged(SpeedLimitUnlockerSettings newSettings)
    {
        lock (sync)
        {
            settings = newSettings ?? new SpeedLimitUnlockerSettings();
            NormalizeSettings(saveIfChanged: false);
            ClampPreferredTarget();
        }
    }

    private void NormalizeSettings(bool saveIfChanged)
    {
        bool changed = false;

        if (settings.SettingsVersion < 1)
        {
            settings.SettingsVersion = 1;
            if (settings.DefaultTargetSpeed <= 90)
                settings.DefaultTargetSpeed = 120;
            if (settings.MaximumTargetSpeed < settings.DefaultTargetSpeed)
                settings.MaximumTargetSpeed = settings.DefaultTargetSpeed;
            changed = true;
        }

        if (settings.SettingsVersion < 2)
        {
            settings.SettingsVersion = 2;
            if (settings.MaximumTargetSpeed <= 120)
                settings.MaximumTargetSpeed = 9999;
            changed = true;
        }

        if (settings.SettingsVersion < 3)
        {
            settings.SettingsVersion = 3;
            if (settings.MaximumTargetSpeed <= 120)
                settings.MaximumTargetSpeed = 9999;
            changed = true;
        }

        if (settings.SettingsVersion < 4)
        {
            settings.SettingsVersion = 4;
            if (settings.DefaultTargetSpeed <= 120)
                settings.DefaultTargetSpeed = 9999;
            if (settings.MaximumTargetSpeed <= 120)
                settings.MaximumTargetSpeed = 9999;
            changed = true;
        }

        if (settings.SettingsVersion < 5)
        {
            settings.SettingsVersion = 5;
            changed = true;
        }

        if (settings.DefaultTargetSpeed <= 120)
        {
            settings.DefaultTargetSpeed = 9999;
            changed = true;
        }

        if (settings.MaximumTargetSpeed <= 120)
        {
            settings.MaximumTargetSpeed = 9999;
            changed = true;
        }

        if (settings.DefaultTargetSpeed > settings.MaximumTargetSpeed)
        {
            settings.MaximumTargetSpeed = settings.DefaultTargetSpeed;
            changed = true;
        }

        if (saveIfChanged && changed)
            settingsHandler?.Save(SettingsFileName, settings);
    }

    private void OnTelemetry(GameTelemetryData data)
    {
        lock (sync)
        {
            latestTelemetry = data;
            lastSpeedLimit = BuildEffectiveSpeedLimit(data.truckFloat.speedLimit);
            CaptureExistingTargetIfUseful();
            if (settings.Enabled)
                RestorePreferredTarget();
        }
    }

    private void OnSpeedLimitChanged(float speedLimit)
    {
        if (!settings.Enabled)
            return;

        lock (sync)
        {
            lastSpeedLimit = BuildEffectiveSpeedLimit(speedLimit);
            EnsurePreferredTarget();
            IgnoreSpeedLimitTargetCaptureBriefly();
            ForceRestoreBriefly();
        }

        _ = RestoreAfterDelayAsync();
    }

    private void OnAssistsUnpaused(EventArgs eventArgs)
    {
        if (!settings.Enabled)
            return;

        lock (sync)
        {
            EnsurePreferredTarget();
            IgnoreSpeedLimitTargetCaptureBriefly();
            ForceRestoreBriefly();
        }

        _ = RestoreAfterDelayAsync();
    }

    private async Task RestoreAfterDelayAsync()
    {
        int delayMs = Math.Clamp((int)Math.Round(settings.RestoreDelayMs), 0, 500);
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);

        lock (sync)
        {
            if (!settings.Enabled)
                return;

            RestorePreferredTarget();
        }
    }

    private void CaptureExistingTargetIfUseful()
    {
        if (suppressTargetCapture)
        {
            suppressTargetCapture = false;
            return;
        }

        float desired = ApplicationState.Current.DesiredSpeed;
        if (desired <= 0.01f)
            return;

        float tolerance = UnitConversions.ToScientificUnits(UnitType.Speed, 1, ApplicationState.Current.DisplayUnits);
        float effectiveTelemetryLimit = BuildEffectiveSpeedLimit(latestTelemetry.truckFloat.speedLimit);
        bool looksLikeSpeedLimit = IsNear(desired, effectiveTelemetryLimit, tolerance)
                                || IsNear(desired, lastSpeedLimit, tolerance);

        if (looksLikeSpeedLimit && DateTime.UtcNow <= ignoreLimitTargetCaptureUntilUtc)
            return;

        if (!looksLikeSpeedLimit && desired > preferredTargetSpeed + tolerance)
            preferredTargetSpeed = ClampTarget(desired);
    }

    private bool ShouldRestoreBecauseTargetMatchesLimit()
    {
        float desired = ApplicationState.Current.DesiredSpeed;
        float limit = BuildEffectiveSpeedLimit(latestTelemetry.truckFloat.speedLimit);
        if (limit <= 0.01f)
            limit = lastSpeedLimit;

        if (desired <= 0.01f || limit <= 0.01f)
            return false;

        EnsurePreferredTarget();

        float tolerance = UnitConversions.ToScientificUnits(UnitType.Speed, 1, ApplicationState.Current.DisplayUnits);
        return preferredTargetSpeed > desired + tolerance
            && IsNear(desired, limit, tolerance);
    }

    private void RestorePreferredTarget()
    {
        EnsurePreferredTarget();
        float target = ClampTarget(BuildDefaultTargetSpeed());
        if (target <= 0.01f)
            return;

        suppressTargetCapture = true;
        ApplicationState.Current.DesiredSpeed = target;
    }

    private void EnsurePreferredTarget()
    {
        if (preferredTargetSpeed <= 0.01f)
            preferredTargetSpeed = BuildDefaultTargetSpeed();

        preferredTargetSpeed = ClampTarget(preferredTargetSpeed);
    }

    private void ResetPreferredTargetFromDefaults()
    {
        lock (sync)
        {
            preferredTargetSpeed = BuildDefaultTargetSpeed();
        }
    }

    private void ClampPreferredTarget()
    {
        lock (sync)
        {
            preferredTargetSpeed = ClampTarget(preferredTargetSpeed);
        }
    }

    private float BuildDefaultTargetSpeed()
    {
        return UnitConversions.ToScientificUnits(
            UnitType.Speed,
            (float)settings.DefaultTargetSpeed,
            ApplicationState.Current.DisplayUnits);
    }

    private float ClampTarget(float target)
    {
        float max = UnitConversions.ToScientificUnits(
            UnitType.Speed,
            (float)settings.MaximumTargetSpeed,
            ApplicationState.Current.DisplayUnits);

        if (max <= 0.01f)
            return target;

        return Math.Clamp(target, 0, max);
    }

    private void IgnoreSpeedLimitTargetCaptureBriefly()
    {
        int delayMs = Math.Clamp((int)Math.Round(settings.RestoreDelayMs), 0, 500);
        ignoreLimitTargetCaptureUntilUtc = DateTime.UtcNow.AddMilliseconds(delayMs + 750);
    }

    private void ForceRestoreBriefly()
    {
        int delayMs = Math.Clamp((int)Math.Round(settings.RestoreDelayMs), 0, 500);
        forceRestoreUntilUtc = DateTime.UtcNow.AddMilliseconds(delayMs + 2000);
    }

    private static float BuildEffectiveSpeedLimit(float speedLimit)
    {
        if (speedLimit > 0.01f)
            return speedLimit;

        return UnitConversions.ToScientificUnits(UnitType.Speed, 30, Units.Metric);
    }

    private static bool IsNear(float left, float right, float tolerance)
    {
        return right > 0.01f && Math.Abs(left - right) <= tolerance;
    }

    private UiTable BuildStatusTable(Units units)
    {
        string unit = UnitConversions.GetUnitAbbreviation(UnitType.Speed, units);

        string Preferred() => FormatSpeed(preferredTargetSpeed, units, unit);
        string Desired() => FormatSpeed(ApplicationState.Current.DesiredSpeed, units, unit);
        string Limit() => FormatSpeed(BuildEffectiveSpeedLimit(latestTelemetry.truckFloat.speedLimit), units, unit);

        return new UiTable(
            "Status",
            new[] { "Value", "Speed" },
            new[]
            {
                new[] { "Preferred target", Preferred() },
                new[] { "Current target", Desired() },
                new[] { "Road limit", Limit() }
            });
    }

    private static string FormatSpeed(float speed, Units units, string unit)
    {
        float display = UnitConversions.FromScientificUnits(UnitType.Speed, speed, units);
        return $"{display:0} {unit}";
    }

    private static double ClampDouble(object? value, double min, double max, double fallback)
    {
        double parsed = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s when double.TryParse(s, out double d) => d,
            _ => fallback
        };

        return Math.Clamp(parsed, min, max);
    }
}
