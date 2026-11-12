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
        Reachability();
        GeneratedChainsAreReachable();
        AutopilotNeverStranded();

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

    /// <summary>
    /// Unit checks on the reachability contract: the ballistic budget, the wrap-aware gap and the
    /// rise limit the generator stacks against.
    /// </summary>
    private static void Reachability()
    {
        var maxRise = GameModel.MaxRise;
        Check("game/max rise is the bounce apex", MathF.Abs(maxRise - 128f) <= 0.01f, "MaxRise = " + maxRise);

        Check("game/reach is zero at the apex", GameModel.ReachableDistance(maxRise) == 0f, "ReachableDistance(MaxRise) = " + GameModel.ReachableDistance(maxRise));
        Check("game/reach is zero past the apex", GameModel.ReachableDistance(maxRise + 40f) == 0f, "ReachableDistance(MaxRise + 40) = " + GameModel.ReachableDistance(maxRise + 40f));
        Check("game/reach has no negative budget", GameModel.ReachableDistance(-20f) == GameModel.ReachableDistance(0f), "ReachableDistance(-20) = " + GameModel.ReachableDistance(-20f));

        Check("game/reach buys ~114 px on the tallest gap", MathF.Abs(GameModel.ReachableDistance(95f) - 113.7f) <= 2f, "ReachableDistance(95) = " + GameModel.ReachableDistance(95f));
        Check("game/reach buys ~169 px on the shortest gap", MathF.Abs(GameModel.ReachableDistance(55f) - 169.2f) <= 3f, "ReachableDistance(55) = " + GameModel.ReachableDistance(55f));

        // 60 px wide platforms, stacked on the 480 px play field the rest of the checks use.
        var low = new RectangleF(0f, 0f, 60f, 12f);

        Check("game/reach accepts a stacked platform", GameModel.IsReachable(low, new RectangleF(0f, 95f, 60f, 12f), 480), "same x, 95 px up");
        Check("game/reach rejects a rise past the apex", !GameModel.IsReachable(low, new RectangleF(0f, 130f, 60f, 12f), 480), "130 px up");
        Check("game/reach rejects a same-height platform", !GameModel.IsReachable(low, new RectangleF(0f, 0f, 60f, 12f), 480), "zero rise");

        // 120 px clear of both edges is further than the 91 px a 95 px bounce buys (0.8 * 113.7).
        Check("game/reach rejects an oversized gap", !GameModel.IsReachable(low, new RectangleF(240f, 95f, 60f, 12f), 480), "gap 180 either way, budget 91");

        // A 400 px offset is hopeless on a wide field...
        Check("game/reach rejects 400 px without the wrap", !GameModel.IsReachable(low, new RectangleF(400f, 95f, 60f, 12f), 960), "gap 340, budget 91");

        // ...but on a 480 px field the player wraps, so the same 400 px offset is a 20 px hop.
        Check("game/reach wraps the 400 px offset", GameModel.IsReachable(low, new RectangleF(400f, 95f, 60f, 12f), 480), "wrap gap 20, budget 91");

        // Wrap-aware interval gap of exactly 80 px: only the wrap fits the 135 px budget, the
        // direct reading (280 px) never does.
        Check("game/reach wraps an 80 px gap", GameModel.IsReachable(low, new RectangleF(340f, 55f, 60f, 12f), 480), "wrap gap 80, direct 280, budget 135");
    }

    /// <summary>
    /// Every pair of consecutive platforms the generator produces has to be climbable, checked
    /// bottom-to-top inside the play field where the player can actually see and use them.
    /// </summary>
    private static void GeneratedChainsAreReachable()
    {
        const int seeds = 64;
        const int ticks = 3000;
        const int checkpoint = 500;

        var brokenPairs = 0;
        var inspectedPairs = 0;
        var worstSlack = float.MinValue;
        var worstDetail = "no pair inspected";

        for (var seed = 1; seed <= seeds; seed++)
        {
            var model = new GameModel(seed, 480, 600);
            model.Start();
            var pilot = new Autopilot();

            for (var tick = 0; tick < ticks; tick++)
            {
                pilot.Tick(model);
                model.Tick();

                if ((tick + 1) % checkpoint != 0) continue;

                brokenPairs += InspectChain(model, out var pairs, out var slack, out var detail);
                inspectedPairs += pairs;

                if (slack <= worstSlack) continue;

                worstSlack = slack;
                worstDetail = "seed " + seed + " tick " + (tick + 1) + ": " + detail;
            }
        }

        Check("game/generated chains are reachable", brokenPairs == 0, brokenPairs + " broken, worst " + worstDetail);
        Check("game/generated chains were inspected", inspectedPairs >= 1000, "pairs = " + inspectedPairs);
    }

    /// <summary>
    /// The operator's requirement, measured: a competent-but-simple autopilot that only ever aims
    /// at the next platform up must never be stranded and must keep gaining height.
    /// </summary>
    private static void AutopilotNeverStranded()
    {
        const int seeds = 8;
        const int ticks = 6000;
        const int tail = 1000;

        var deaths = 0;
        var minMetres = int.MaxValue;
        var stalledSeeds = 0;
        var worstSeed = 0;

        for (var seed = 1; seed <= seeds; seed++)
        {
            var model = new GameModel(seed, 480, 600);
            model.Start();
            var pilot = new Autopilot();

            for (var tick = 0; tick < ticks - tail; tick++)
            {
                pilot.Tick(model);
                model.Tick();
            }

            var heightBefore = model.MaxHeight;
            for (var tick = 0; tick < tail; tick++)
            {
                pilot.Tick(model);
                model.Tick();
            }

            deaths += model.Deaths;
            if (model.MaxHeight <= heightBefore) stalledSeeds++;
            if (model.ScoreMetres < minMetres)
            {
                minMetres = model.ScoreMetres;
                worstSeed = seed;
            }
        }

        Check("game/autopilot never dies", deaths == 0, "deaths = " + deaths);
        // Measured ~235-245 m per seed over 6000 ticks; 100 m leaves plenty of room.
        Check("game/autopilot climbs", minMetres > 100, "weakest seed " + worstSeed + " scored " + minMetres + " m");
        Check("game/autopilot keeps climbing", stalledSeeds == 0, stalledSeeds + " seeds stalled in the final " + tail + " ticks");
    }

    /// <summary>
    /// Steers the player at the lowest platform strictly above their feet, wrap-aware, with a 4 px
    /// dead zone. Stands in for a competent human: no look-ahead, just "aim at the next step", and
    /// it commits to that step until the next bounce instead of re-aiming mid-flight.
    /// </summary>
    private sealed class Autopilot
    {
        private const float DeadZone = 4f;

        private int _bounces = -1;
        private bool _hasTarget;
        private float _targetX;

        public void Tick(GameModel model)
        {
            if (model.BounceCount != _bounces)
            {
                _bounces = model.BounceCount;
                _hasTarget = PickTarget(model, out var targetX);
                _targetX = targetX;
            }

            if (!_hasTarget)
            {
                model.Direction = 0;
                return;
            }

            var player = model.PlayerBounds;
            var delta = _targetX - (player.X + player.Width * 0.5f);
            while (delta > model.ViewportWidth * 0.5f) delta -= model.ViewportWidth;
            while (delta < -model.ViewportWidth * 0.5f) delta += model.ViewportWidth;

            if (delta > DeadZone) model.Direction = 1;
            else if (delta < -DeadZone) model.Direction = -1;
            else model.Direction = 0;
        }

        private static bool PickTarget(GameModel model, out float targetX)
        {
            var player = model.PlayerBounds;
            var feet = player.Bottom;

            var found = false;
            var lowest = float.MinValue;
            targetX = player.X + player.Width * 0.5f;

            foreach (var platform in model.Platforms)
            {
                if (platform.Y >= feet) continue;
                if (found && platform.Y <= lowest) continue;

                lowest = platform.Y;
                targetX = platform.X + platform.Width * 0.5f;
                found = true;
            }

            return found;
        }
    }

    /// <summary>
    /// Sorts the platforms bottom-to-top by world top and counts the consecutive pairs that a
    /// single bounce cannot cross. Only pairs inside the play field (plus a margin) are judged, so
    /// recycle churn far below or far above the camera cannot make the check flaky.
    /// </summary>
    private static int InspectChain(GameModel model, out int pairs, out float worstSlack, out string worstDetail)
    {
        var viewport = model.ViewportHeight;
        var floor = model.CameraY - 300f;
        var ceiling = model.CameraY + viewport + 300f;

        var count = model.Platforms.Count;
        var rects = new RectangleF[count];
        var tops = new float[count];
        for (var i = 0; i < count; i++)
        {
            var platform = model.Platforms[i];
            tops[i] = model.CameraY + viewport - platform.Y;
            rects[i] = new RectangleF(platform.X, tops[i], platform.Width, platform.Height);
        }

        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;
        Array.Sort(order, (a, b) => tops[a].CompareTo(tops[b]));

        var broken = 0;
        pairs = 0;
        worstSlack = float.MinValue;
        worstDetail = "no pair inspected";

        for (var i = 0; i + 1 < order.Length; i++)
        {
            var lowerTop = tops[order[i]];
            var upperTop = tops[order[i + 1]];
            if (lowerTop < floor || lowerTop > ceiling) continue;
            if (upperTop < floor || upperTop > ceiling) continue;

            pairs++;
            if (GameModel.IsReachable(rects[order[i]], rects[order[i + 1]], model.ViewportWidth)) continue;

            broken++;
            // 0.8 is GameModel's private ReachSafety; it only feeds the failure message.
            var slack = Gap(rects[order[i]], rects[order[i + 1]], model.ViewportWidth) - GameModel.ReachableDistance(upperTop - lowerTop) * 0.8f;
            if (slack <= worstSlack) continue;

            worstSlack = slack;
            worstDetail = "gap " + slack + " px past budget, rise " + (upperTop - lowerTop) + " px, x " + rects[order[i]].X + "->" + rects[order[i + 1]].X;
        }

        return broken;
    }

    /// <summary>Wrap-aware horizontal gap between two platform rects, for the failure messages.</summary>
    private static float Gap(RectangleF lower, RectangleF upper, int viewportWidth)
    {
        var gap = float.MaxValue;
        for (var shift = -1; shift <= 1; shift++)
        {
            var left = upper.X + shift * viewportWidth;
            var right = upper.Right + shift * viewportWidth;

            float candidate;
            if (right < lower.X) candidate = lower.X - right;
            else if (left > lower.Right) candidate = left - lower.Right;
            else candidate = 0f;

            if (candidate < gap) gap = candidate;
        }

        return gap;
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
