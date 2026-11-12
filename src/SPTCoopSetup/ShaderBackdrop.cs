using System.Drawing.Drawing2D;

namespace SPTCoopSetup;

/// <summary>
/// Loads the embedded background shader still once and paints it cover-scaled behind a scrim.
/// The scaled bitmap is cached per client size so painting costs two draws and no scaling.
/// </summary>
internal sealed class ShaderBackdrop : IDisposable
{
    private const string ResourceName = "SPTCoopSetup.assets.background-shader.png";

    private readonly Image? _source;
    private readonly SolidBrush _baseBrush = new(Theme.BgBase);
    private readonly SolidBrush _scrimBrush = new(Theme.BgScrim);

    private Bitmap? _scaled;
    private Size _scaledFor;
    private bool _disposed;

    public ShaderBackdrop()
    {
        _source = LoadSource();
        HasImage = _source is not null;
    }

    /// <summary>False when the embedded still could not be loaded: the window then paints Bg.Base only.</summary>
    public bool HasImage { get; }

    /// <summary>Paints Bg.Base, then the cover-scaled still, then the BgScrim overlay across the whole rect.</summary>
    public void Paint(Graphics graphics, Size clientSize)
    {
        if (_disposed || graphics is null)
        {
            return;
        }

        if (clientSize.Width <= 0 || clientSize.Height <= 0)
        {
            return;
        }

        var rect = new Rectangle(0, 0, clientSize.Width, clientSize.Height);

        if (_source is not null)
        {
            EnsureScaled(clientSize);

            if (_scaled is not null)
            {
                graphics.DrawImageUnscaled(_scaled, 0, 0);
            }
            else
            {
                graphics.FillRectangle(_baseBrush, rect);
            }
        }
        else
        {
            graphics.FillRectangle(_baseBrush, rect);
        }

        graphics.FillRectangle(_scrimBrush, rect);
    }

    /// <summary>Drops the scaled cache so the next Paint rescales (call on DPI change).</summary>
    public void Invalidate()
    {
        _scaled?.Dispose();
        _scaled = null;
        _scaledFor = Size.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Invalidate();
        _source?.Dispose();
        _baseBrush.Dispose();
        _scrimBrush.Dispose();
    }

    /// <summary>
    /// Aspect-preserving cover fit: the source rect is the centred crop that matches the
    /// client aspect ratio, the destination is the whole client rect.
    /// </summary>
    private void EnsureScaled(Size clientSize)
    {
        if (_scaled is not null && _scaledFor == clientSize)
        {
            return;
        }

        Invalidate();

        if (_source is null)
        {
            return;
        }

        var scale = Math.Max(
            clientSize.Width / (double)_source.Width,
            clientSize.Height / (double)_source.Height);

        if (scale <= 0d)
        {
            return;
        }

        var sourceWidth = (int)Math.Round(clientSize.Width / scale);
        var sourceHeight = (int)Math.Round(clientSize.Height / scale);

        sourceWidth = Math.Clamp(sourceWidth, 1, _source.Width);
        sourceHeight = Math.Clamp(sourceHeight, 1, _source.Height);

        var sourceLeft = (_source.Width - sourceWidth) / 2;
        var sourceTop = (_source.Height - sourceHeight) / 2;

        var sourceRect = new Rectangle(sourceLeft, sourceTop, sourceWidth, sourceHeight);

        try
        {
            var scaled = new Bitmap(clientSize.Width, clientSize.Height);

            using (var scaledGraphics = Graphics.FromImage(scaled))
            {
                scaledGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                scaledGraphics.CompositingQuality = CompositingQuality.HighQuality;

                scaledGraphics.DrawImage(
                    _source,
                    new Rectangle(0, 0, clientSize.Width, clientSize.Height),
                    sourceRect,
                    GraphicsUnit.Pixel);
            }

            _scaled = scaled;
            _scaledFor = clientSize;
        }
        catch (Exception)
        {
            _scaled?.Dispose();
            _scaled = null;
            _scaledFor = Size.Empty;
        }
    }

    private static Image? LoadSource()
    {
        try
        {
            var assembly = typeof(ShaderBackdrop).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                return null;
            }

            using var loaded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(loaded);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
