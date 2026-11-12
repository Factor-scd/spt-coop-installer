using System.Drawing;

namespace SPTCoopSetup;

/// <summary>
/// Physics/mechanics regression tests for <see cref="GameModel"/>. Standalone on purpose:
/// the project has no test framework and must run on the Linux build host.
/// </summary>
internal static class GameModelTests
{
    private static int _checks;
    private static int _failures;

    /// <summary>Number of checks executed by the last <see cref="Run"/>.</summary>
    public static int Checks
    {
        get { return _checks; }
    }

    /// <summary>Number of checks that failed in the last <see cref="Run"/>.</summary>
    public static int Failures
    {
        get { return _failures; }
    }

    /// <summary>Runs every check and returns the failure count.</summary>
    public static int Run()
    {
        _checks = 0;
        _failures = 0;

        Determinism();
        AutoBounce();
        Wrap();
        CameraNeverDrops();
        DeathAndAutoRestart();
        Attract();
        Pause();
        Resize();
        PlatformCountStaysBounded();

        Console.WriteLine();
        return _failures;
    }

    private static void Determinism()
    {
        var a = new GameModel(GameModel.PreviewSeed, 480, 600);
        var b = new GameModel(GameModel.PreviewSeed, 480, 600);
        a.Start();
        b.Start();

        for (var tick = 0; tick < 400; tick++)
        {
            var direction = tick < 120 ? 1 : tick < 260 ? -1 : 0;
            a.Direction = direction;
            b.Direction = direction;
            a.Tick();
            b.Tick();
        }

        Check("game/determinism score", a.ScoreMetres == b.ScoreMetres, a.ScoreMetres + " vs " + b.ScoreMetres);
        Check("game/determinism max height", a.MaxHeight == b.MaxHeight, a.MaxHeight + " vs " + b.MaxHeight);
        Check("game/determinism player bounds", a.PlayerBounds == b.PlayerBounds, a.PlayerBounds + " vs " + b.PlayerBounds);
        Check("game/determinism platform count", a.Platforms.Count == b.Platforms.Count, a.Platforms.Count + " vs " + b.Platforms.Count);

        var samePlatforms = a.Platforms.Count == b.Platforms.Count;
        for (var i = 0; samePlatforms && i < a.Platforms.Count; i++)
        {
            samePlatforms = a.Platforms[i] == b.Platforms[i];
        }

        Check("game/determinism platform geometry", samePlatforms, "platform rects differ");
    }

    private static void AutoBounce()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();

        var moved = 0;
        var previous = model.PlayerBounds.Y;
        for (var tick = 0; tick < 400; tick++)
        {
            model.Tick();
            var current = model.PlayerBounds.Y;
            if (current != previous) moved++;
            previous = current;
        }

        Check("game/auto-bounce happens", model.BounceCount > 0, "BounceCount = " + model.BounceCount);
        Check("game/player never rests", moved >= 380, "moved on " + moved + " of 400 ticks");
    }

    private static void Wrap()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();
        model.Direction = 1;

        for (var tick = 0; tick < 200; tick++)
        {
            model.Tick();
        }

        Check("game/wrap increments", model.WrapCount > 0, "WrapCount = " + model.WrapCount);
        Check("game/wrap keeps the player on screen", model.PlayerBounds.X >= -1f && model.PlayerBounds.Right <= model.ViewportWidth + 1f, model.PlayerBounds.ToString());
    }

    private static void CameraNeverDrops()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();

        var monotonic = true;
        var previousCamera = model.CameraY;
        for (var tick = 0; tick < 600; tick++)
        {
            model.Direction = tick % 90 < 45 ? 1 : -1;
            model.Tick();
            if (model.CameraY < previousCamera) monotonic = false;
            previousCamera = model.CameraY;
        }

        Check("game/camera never drops", model.CameraDropCount == 0, "CameraDropCount = " + model.CameraDropCount);
        Check("game/camera is monotonic", monotonic, "CameraY went backwards");
    }

    private static void DeathAndAutoRestart()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();
        var deathsBefore = model.Deaths;

        model.SimulateFall();
        model.Tick();

        Check("game/falling below the viewport kills", model.State == GameState.Dead, model.State.ToString());
        Check("game/death is counted", model.Deaths == deathsBefore + 1, "Deaths = " + model.Deaths);

        for (var tick = 0; tick < 130; tick++)
        {
            model.Tick();
        }

        Check("game/dead auto-restarts", model.State == GameState.Playing, model.State.ToString());
    }

    private static void Attract()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        Check("game/starts in attract", model.State == GameState.Attract, model.State.ToString());

        var before = model.PlayerBounds;
        var platformsBefore = model.Platforms.Count;
        for (var tick = 0; tick < 60; tick++)
        {
            model.Tick();
        }

        Check("game/attract does not move the player", model.PlayerBounds == before, model.PlayerBounds + " vs " + before);
        Check("game/attract keeps platforms", model.Platforms.Count == platformsBefore && model.Platforms.Count > 0, model.Platforms.Count + " vs " + platformsBefore);

        model.Start();
        Check("game/start leaves attract", model.State == GameState.Playing, model.State.ToString());
    }

    private static void Pause()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();
        for (var tick = 0; tick < 40; tick++)
        {
            model.Tick();
        }

        model.Paused = true;
        var boundsBefore = model.PlayerBounds;
        var heightBefore = model.MaxHeight;
        var bouncesBefore = model.BounceCount;

        for (var tick = 0; tick < 50; tick++)
        {
            model.Tick();
        }

        Check("game/pause freezes the player", model.PlayerBounds == boundsBefore, model.PlayerBounds + " vs " + boundsBefore);
        Check("game/pause freezes the score", model.MaxHeight == heightBefore && model.BounceCount == bouncesBefore, model.MaxHeight + " vs " + heightBefore);

        model.Paused = false;
        model.Tick();
        Check("game/unpause resumes", model.PlayerBounds != boundsBefore, "player stayed put after unpause");
    }

    private static void Resize()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();
        for (var tick = 0; tick < 60; tick++)
        {
            model.Tick();
        }

        var threw = false;
        try
        {
            model.Resize(320, 420);
            model.Tick();
        }
        catch (Exception exception)
        {
            threw = true;
            Console.WriteLine("  resize threw " + exception.GetType().Name + ": " + exception.Message);
        }

        Check("game/resize does not throw", !threw, "Resize threw");
        Check("game/resize keeps the viewport", model.ViewportWidth == 320 && model.ViewportHeight == 420, model.ViewportWidth + "x" + model.ViewportHeight);
        Check("game/resize keeps platforms", model.Platforms.Count > 0, "no platforms");

        var bounds = model.PlayerBounds;
        Check("game/resize keeps the player inside", bounds.X >= -1f && bounds.Right <= model.ViewportWidth + 1f, bounds.ToString());
    }

    private static void PlatformCountStaysBounded()
    {
        var model = new GameModel(GameModel.PreviewSeed, 480, 600);
        model.Start();

        var maxPlatforms = 0;
        for (var tick = 0; tick < 5000; tick++)
        {
            model.Direction = tick % 120 < 60 ? 1 : 0;
            model.Tick();
            if (model.Platforms.Count > maxPlatforms) maxPlatforms = model.Platforms.Count;
        }

        Check("game/platforms are recycled", maxPlatforms < 400, "peak platform count = " + maxPlatforms);
        Check("game/scoring advanced", model.BestMetres > 0, "BestMetres = " + model.BestMetres);
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine("PASS " + name);
            return;
        }

        _failures++;
        Console.WriteLine("FAIL " + name + (detail is null ? "" : " -> " + detail));
    }
}
