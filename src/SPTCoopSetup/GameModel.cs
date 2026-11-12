using System.Collections;
using System.Drawing;

namespace SPTCoopSetup;

/// <summary>Where the game is in its lifecycle.</summary>
internal enum GameState { Attract, Playing, Dead }

/// <summary>
/// Pure, deterministic simulation of the coop scrolling game. No drawing, no input
/// polling, no timers and no <see cref="System.Random"/>: the view layer calls
/// <see cref="Tick"/> at a fixed cadence and reads bounds back in screen space.
/// </summary>
internal sealed class GameModel
{
    /// <summary>Fixed seed used by --preview-ui and --test-game so frames are repeatable.</summary>
    public const int PreviewSeed = 20261112;

    /// <summary>The fixed simulation step: 16 ms.</summary>
    public const float StepSeconds = 0.016f;

    /// <summary>Pixels per scored metre.</summary>
    public const float PixelsPerMetre = 48f;

    private const float PlayerWidth = 34f;
    private const float PlayerHeight = 40f;
    private const float MoveSpeed = 280f;
    private const float Gravity = -1600f;
    private const float BounceVelocity = 640f;
    private const float PlatformHeight = 12f;
    private const float PlatformBaseWidth = 72f;
    private const float PlatformMinWidth = PlatformBaseWidth - 12f;
    private const float PlatformMaxWidth = PlatformBaseWidth + 12f;
    private const float MinGap = 55f;
    private const float MaxGap = 95f;
    private const float CameraAnchorRatio = 0.45f;
    private const float RecycleBelowMargin = 200f;
    private const float AboveViewportMargin = 200f;
    private const float AttractDrift = 8f;
    private const int AutoRestartTicks = 125;
    private const int MaxPlatforms = 320;
    private const int GuardLimit = 64;

    private readonly Platform[] _platformState = new Platform[MaxPlatforms];
    private readonly RectangleF[] _screen = new RectangleF[MaxPlatforms];
    private readonly IReadOnlyList<RectangleF> _screenView;

    private uint _rng;
    private int _platformCount;

    private int _viewportWidth;
    private int _viewportHeight;

    private float _playerX;
    private float _playerY;
    private float _playerVelocityY;
    private int _direction;

    private float _cameraY;
    private float _maxHeight;

    private int _state;
    private bool _paused;
    private int _bounceCount;
    private int _wrapCount;
    private int _cameraDropCount;
    private int _deaths;
    private int _bestMetres;
    private int _deadTicks;

    /// <summary>Creates a model parked in <see cref="GameState.Attract"/> at the bottom of a fresh world.</summary>
    public GameModel(int seed, int viewportWidth, int viewportHeight)
    {
        if (viewportWidth < 1) viewportWidth = 1;
        if (viewportHeight < 1) viewportHeight = 1;

        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;

        // Seed 0 would make xorshift32 a no-op, so fold it to the golden ratio constant.
        _rng = (uint)seed;
        if (_rng == 0) _rng = 0x9E3779B9u;

        _screenView = new PlatformView(this);

        ResetRun(attract: true);
    }

    /// <summary>Viewport width in pixels.</summary>
    public int ViewportWidth
    {
        get { return _viewportWidth; }
    }

    /// <summary>Viewport height in pixels.</summary>
    public int ViewportHeight
    {
        get { return _viewportHeight; }
    }

    /// <summary>Current lifecycle state.</summary>
    public GameState State
    {
        get { return (GameState)_state; }
    }

    /// <summary>When true, <see cref="Tick"/> does nothing and every counter stays frozen.</summary>
    public bool Paused
    {
        get { return _paused; }
        set { _paused = value; }
    }

    /// <summary>-1 = left, 0 = none, +1 = right. Values are clamped on assignment.</summary>
    public int Direction
    {
        get { return _direction; }
        set
        {
            if (value > 0) _direction = 1;
            else if (value < 0) _direction = -1;
            else _direction = 0;
        }
    }

    /// <summary>Ticks where an auto-bounce was applied, this run.</summary>
    public int BounceCount
    {
        get { return _bounceCount; }
    }

    /// <summary>Times the player wrapped across an edge, this run.</summary>
    public int WrapCount
    {
        get { return _wrapCount; }
    }

    /// <summary>Ticks where the camera moved DOWN. Must stay 0.</summary>
    public int CameraDropCount
    {
        get { return _cameraDropCount; }
    }

    /// <summary>Deaths since construction. Survives <see cref="Restart"/>.</summary>
    public int Deaths
    {
        get { return _deaths; }
    }

    /// <summary>Max height reached this run, in metres.</summary>
    public int ScoreMetres
    {
        get { return (int)(_maxHeight / PixelsPerMetre); }
    }

    /// <summary>Session high score: the best of every run's <see cref="ScoreMetres"/>.</summary>
    public int BestMetres
    {
        get { return _bestMetres; }
    }

    /// <summary>World y of the viewport's bottom edge. Only ever increases.</summary>
    public float CameraY
    {
        get { return _cameraY; }
    }

    /// <summary>Highest world player y reached this run, in pixels.</summary>
    public float MaxHeight
    {
        get { return _maxHeight; }
    }

    /// <summary>Player bounds in SCREEN space (camera already applied).</summary>
    public RectangleF PlayerBounds
    {
        get
        {
            var screenTop = _viewportHeight - (_playerY + PlayerHeight - _cameraY);
            return new RectangleF(_playerX - PlayerWidth * 0.5f, screenTop, PlayerWidth, PlayerHeight);
        }
    }

    /// <summary>Platform bounds in SCREEN space, in stable generation order.</summary>
    public IReadOnlyList<RectangleF> Platforms
    {
        get { return _screenView; }
    }

    /// <summary>Resizes the viewport, keeping the world, camera and score intact.</summary>
    public void Resize(int viewportWidth, int viewportHeight)
    {
        if (viewportWidth < 1) viewportWidth = 1;
        if (viewportHeight < 1) viewportHeight = 1;

        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;

        // Keep the player horizontally inside the new width.
        var half = PlayerWidth * 0.5f;
        if (_playerX > _viewportWidth - half) _playerX = _viewportWidth - half;
        if (_playerX < half) _playerX = half;
    }

    /// <summary>Advances the simulation by exactly <see cref="StepSeconds"/>.</summary>
    public void Tick()
    {
        if (_paused) return;

        switch ((GameState)_state)
        {
            case GameState.Attract:
                TickAttract();
                break;
            case GameState.Playing:
                TickPlaying();
                break;
            case GameState.Dead:
                TickDead();
                break;
        }

        ProjectPlatforms();
    }

    /// <summary>Leaves <see cref="GameState.Attract"/> and starts a real run.</summary>
    public void Start()
    {
        if ((GameState)_state != GameState.Attract) return;
        ResetRun(attract: false);
    }

    /// <summary>Starts a fresh run immediately, from Dead or Attract.</summary>
    public void Restart()
    {
        ResetRun(attract: false);
    }

    /// <summary>
    /// Test seam: drops the player below the viewport so the ordinary death
    /// detection fires on the next <see cref="Tick"/>. A model still in
    /// <see cref="GameState.Attract"/> is started first.
    /// </summary>
    public void SimulateFall()
    {
        if ((GameState)_state == GameState.Attract)
        {
            ResetRun(attract: false);
        }

        _playerY = _cameraY - 500f;
        _playerVelocityY = -100f;
    }

    private void ResetRun(bool attract)
    {
        _bounceCount = 0;
        _wrapCount = 0;
        _cameraY = 0f;
        _maxHeight = 0f;
        _playerVelocityY = 0f;
        _direction = 0;
        _deadTicks = 0;

        _playerX = _viewportWidth * 0.5f;
        _playerY = 80f;

        _state = attract ? (int)GameState.Attract : (int)GameState.Playing;

        BuildWorld();

        if (!attract)
        {
            _maxHeight = _playerY;
        }
    }

    private void BuildWorld()
    {
        _platformCount = 0;

        // A full-width starting floor under the player: the surface has to catch the player
        // on the very first frame no matter which way it is steered.
        AddPlatform(_viewportWidth * 0.5f, 20f, _viewportWidth);

        var ceiling = _cameraY + _viewportHeight + 400f;
        var top = 20f;
        while (_platformCount < MaxPlatforms)
        {
            top += NextGap();
            if (top > ceiling) break;

            var width = NextWidth();
            AddPlatform(NextX(width), top, width);
        }

        // Platforms are readable straight after construction, before the first tick.
        ProjectPlatforms();
    }

    private void TickAttract()
    {
        for (var i = 0; i < _platformCount; i++)
        {
            _platformState[i].Top -= AttractDrift * StepSeconds;
        }

        RecyclePlatforms();
        FillAbove();
    }

    private void TickPlaying()
    {
        // Horizontal motion, then the player centre wraps modulo the viewport width.
        if (_direction != 0)
        {
            _playerX += _direction * MoveSpeed * StepSeconds;
        }

        if (_playerX >= _viewportWidth)
        {
            _playerX -= _viewportWidth;
            _wrapCount++;
        }
        else if (_playerX < 0f)
        {
            _playerX += _viewportWidth;
            _wrapCount++;
        }

        // Vertical motion under gravity.
        var feetBefore = _playerY;
        _playerVelocityY += Gravity * StepSeconds;
        _playerY += _playerVelocityY * StepSeconds;

        // Auto-bounce: always bounce off a platform, never come to rest.
        if (_playerVelocityY <= 0f)
        {
            for (var i = 0; i < _platformCount; i++)
            {
                ref var platform = ref _platformState[i];
                if (feetBefore < platform.Top || _playerY > platform.Top) continue;
                if (!OverlapsX(platform)) continue;

                _playerY = platform.Top;
                _playerVelocityY = BounceVelocity;
                _bounceCount++;
                break;
            }
        }

        if (_playerY > _maxHeight)
        {
            _maxHeight = _playerY;
        }

        if (ScoreMetres > _bestMetres)
        {
            _bestMetres = ScoreMetres;
        }

        // Camera rises only, anchored 45% up the viewport.
        RaiseCamera(_playerY - _viewportHeight * CameraAnchorRatio);

        RecyclePlatforms();
        FillAbove();

        // Death: the player is now entirely below the viewport.
        var screenTop = _viewportHeight - (_playerY + PlayerHeight - _cameraY);
        if (screenTop > _viewportHeight)
        {
            _state = (int)GameState.Dead;
            _deaths++;
            _deadTicks = 0;
        }
    }

    private void TickDead()
    {
        _deadTicks++;

        if (_deadTicks >= AutoRestartTicks)
        {
            ResetRun(attract: false);
        }
    }

    /// <summary>Moves the camera up. The camera is never allowed to descend.</summary>
    private void RaiseCamera(float target)
    {
        if (target <= _cameraY) return;

        SetCamera(target);
    }

    /// <summary>Assigns the camera, refusing any downward move so scrolling only ever climbs.</summary>
    private void SetCamera(float target)
    {
        if (target < _cameraY)
        {
            // Safety net only: RaiseCamera filters this out, so the counter stays at zero.
            _cameraDropCount++;
            return;
        }

        _cameraY = target;
    }

    private void RecyclePlatforms()
    {
        var floor = _cameraY - RecycleBelowMargin;
        for (var i = 0; i < _platformCount; i++)
        {
            if (_platformState[i].Top < floor)
            {
                MoveToTop(i);
            }
        }
    }

    /// <summary>Index of the lowest platform that sits entirely below the viewport, or -1.</summary>
    private int LowestBelowCamera()
    {
        var index = -1;
        var lowest = float.MaxValue;
        for (var i = 0; i < _platformCount; i++)
        {
            if (_platformState[i].Top >= _cameraY) continue;
            if (_platformState[i].Top >= lowest) continue;

            lowest = _platformState[i].Top;
            index = i;
        }

        return index;
    }

    /// <summary>Keeps the stack topped up as the camera rises, touching only off-screen ground.</summary>
    private void FillAbove()
    {
        var ceiling = _cameraY + _viewportHeight + AboveViewportMargin;
        var guard = 0;
        while (HighestTop() < ceiling && guard < GuardLimit)
        {
            var index = LowestBelowCamera();
            if (index < 0) break;

            MoveToTop(index);
            guard++;
        }
    }

    private void MoveToTop(int index)
    {
        ref var platform = ref _platformState[index];
        var top = HighestTop();
        if (top < _cameraY) top = _cameraY;

        platform.Top = top + NextGap();
        platform.Width = NextWidth();
        platform.CenterX = NextX(platform.Width);
    }

    private void ProjectPlatforms()
    {
        for (var i = 0; i < _platformCount; i++)
        {
            ref var platform = ref _platformState[i];
            var screenTop = _viewportHeight - (platform.Top - _cameraY);
            _screen[i] = new RectangleF(
                platform.CenterX - platform.Width * 0.5f,
                screenTop,
                platform.Width,
                PlatformHeight);
        }
    }

    private void AddPlatform(float centerX, float top, float width)
    {
        if (_platformCount >= MaxPlatforms) return;

        ref var platform = ref _platformState[_platformCount];
        platform.CenterX = centerX;
        platform.Top = top;
        platform.Width = width;
        _platformCount++;
    }

    private bool OverlapsX(in Platform platform)
    {
        var playerLeft = _playerX - PlayerWidth * 0.5f;
        var playerRight = _playerX + PlayerWidth * 0.5f;
        var platformLeft = platform.CenterX - platform.Width * 0.5f;
        var platformRight = platform.CenterX + platform.Width * 0.5f;

        return playerRight > platformLeft && playerLeft < platformRight;
    }

    private float HighestTop()
    {
        if (_platformCount == 0) return 0f;

        var highest = _platformState[0].Top;
        for (var i = 1; i < _platformCount; i++)
        {
            if (_platformState[i].Top > highest) highest = _platformState[i].Top;
        }

        return highest;
    }

    private float NextWidth()
    {
        var span = PlatformMaxWidth - PlatformMinWidth + 1f;
        return PlatformMinWidth + (NextUInt() % (uint)span);
    }

    private float NextGap()
    {
        return MinGap + (NextUInt() % 41u);
    }

    private float NextX(float width)
    {
        var span = _viewportWidth - width;
        if (span <= 0f) return _viewportWidth * 0.5f;

        return width * 0.5f + (NextUInt() % (uint)span);
    }

    private uint NextUInt()
    {
        // xorshift32: identical sequence on every run and every machine.
        var x = _rng;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rng = x;
        return x;
    }

    private struct Platform
    {
        public float CenterX;
        public float Top;
        public float Width;
    }

    /// <summary>Read-only view over the pre-sized screen projection, shared by every caller.</summary>
    private sealed class PlatformView : IReadOnlyList<RectangleF>
    {
        private readonly GameModel _owner;

        public PlatformView(GameModel owner)
        {
            _owner = owner;
        }

        public RectangleF this[int index]
        {
            get { return _owner._screen[index]; }
        }

        public int Count
        {
            get { return _owner._platformCount; }
        }

        public IEnumerator<RectangleF> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return _owner._screen[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
