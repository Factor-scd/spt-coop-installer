using System.Globalization;

namespace SPTCoopSetup;

/// <summary>Runs the real game model headlessly so CI can prove the physics without a window.</summary>
internal static class GameTest
{
    /// <summary>Ticks the deterministic timeline steers right before it lets go.</summary>
    private const int SteerTicks = 200;

    /// <summary>Below this many ticks the timeline cannot be expected to have wrapped an edge yet.</summary>
    private const int WrapGraceTicks = 140;

    /// <summary>Ticks granted to the death probe for the ~2 s automatic restart.</summary>
    private const int RestartProbeTicks = 130;

    /// <summary>
    /// Drives <paramref name="ticks"/> fixed 16 ms steps of the seeded model (no clock, no window,
    /// no engine) and prints the measured counters plus the four assertions. Returns 0 when every
    /// assertion holds, 4 otherwise.
    /// </summary>
    public static int Run(int ticks)
    {
        if (ticks <= 0)
        {
            WriteCounters(ticks, 0, 0f, 0, 0);
            WriteAssertions(bounce: false, wrap: false, camera: false, death: false);
            Console.Error.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"game assert: ticks expected > 0, measured {ticks}"));
            Console.Out.Flush();
            return 4;
        }

        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();

        // One Tick() per iteration - a tick IS 16 ms, so there is no clock and no sleep here.
        var steerUntil = Math.Min(ticks, SteerTicks);
        for (var tick = 0; tick < ticks; tick++)
        {
            model.Direction = tick < steerUntil ? 1 : 0;
            model.Tick();
        }

        // Measured BEFORE the death probe, so these counters describe the N-tick run.
        var finalY = (int)Math.Round(model.PlayerBounds.Top);
        var maxY = model.MaxHeight;
        var platforms = model.Platforms.Count;
        var bounces = model.BounceCount;
        var wraps = model.WrapCount;
        var cameraDrops = model.CameraDropCount;

        var bounceOk = bounces > 0;
        var wrapOk = wraps > 0 || ticks < WrapGraceTicks;
        var cameraOk = cameraDrops == 0;

        // Death probe: fall out of the world, then wait out the automatic restart.
        var deathsBefore = model.Deaths;
        model.SimulateFall();
        model.Tick();
        var died = model.State == GameState.Dead && model.Deaths == deathsBefore + 1;
        for (var tick = 0; tick < RestartProbeTicks; tick++)
        {
            model.Tick();
        }

        var deathOk = died && model.State == GameState.Playing;

        WriteCounters(ticks, finalY, maxY, platforms, model.Deaths);
        WriteAssertions(bounceOk, wrapOk, cameraOk, deathOk);

        // Exactly one stderr line, naming the first broken measurement in assertion order.
        string? broken = null;
        if (!bounceOk)
        {
            broken = string.Create(CultureInfo.InvariantCulture, $"bounce expected > 0, measured {bounces}");
        }
        else if (!wrapOk)
        {
            broken = string.Create(CultureInfo.InvariantCulture, $"wrap expected > 0, measured {wraps}");
        }
        else if (!cameraOk)
        {
            broken = string.Create(CultureInfo.InvariantCulture, $"camera expected 0 drops, measured {cameraDrops}");
        }
        else if (!deathOk)
        {
            broken = string.Create(
                CultureInfo.InvariantCulture,
                $"death expected {GameState.Dead} then {GameState.Playing} with one more death, measured {model.State} deaths {model.Deaths} (was {deathsBefore})");
        }

        if (broken is not null)
        {
            Console.Error.WriteLine("game assert: " + broken);
        }

        // The app is a WinExe: an inherited stdout only sees these lines if we flush before returning.
        Console.Out.Flush();

        return bounceOk && wrapOk && cameraOk && deathOk ? 0 : 4;
    }

    private static void WriteCounters(int ticks, int finalY, float maxY, int platforms, int deaths)
    {
        Console.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"game ticks={ticks} finalY={finalY} maxY={(int)Math.Round(maxY)} platforms={platforms} deaths={deaths}"));
    }

    private static void WriteAssertions(bool bounce, bool wrap, bool camera, bool death)
    {
        Console.Out.WriteLine(
            "game assert bounce=" + Field(bounce) +
            " wrap=" + Field(wrap) +
            " camera=" + Field(camera) +
            " death=" + Field(death));
    }

    private static string Field(bool ok) => ok ? "ok" : "fail";
}
