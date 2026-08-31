using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Iciclecreek.Terminal.Skia;

namespace Iciclecreek.Terminal.Rendering
{
    /// <summary>
    /// The direct renderer: the cell grid drawn onto the compositor's own SKCanvas instead of
    /// recorded as a display list, which moves the drawing off the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>Declines the rows whose geometry its snapshot cannot express — a doubled row, a line
    /// carrying OSC 66 sized runs, a line holding a picture — and reports them, so the classic
    /// renderer draws exactly those into the same frame.</para>
    /// <para>Owns the snapshot pool and the font cache, and disposes the cache with itself: both
    /// exist only to serve this path.</para>
    /// </remarks>
    internal sealed class SkiaGridRenderer : ITerminalGridRenderer, IDisposable
    {
        private readonly SnapshotBuilder _builder = new();
        private readonly SkiaFontCache _fonts = new();
        private readonly Action _requestPaint;

        /// <summary>The layer enqueued last frame, asked afterwards whether it could draw at all.</summary>
        private TerminalSkiaLayer? _lastLayer;

        public SkiaGridRenderer(Action requestPaint) => _requestPaint = requestPaint;

        /// <inheritdoc/>
        public GridCoverage DrawGrid(DrawingContext context, in GridFrame frame, in GridCoverage outstanding)
        {
            // A custom draw operation only draws where Avalonia is on its Skia backend, and there is
            // no way to ask before enqueuing one -- so the layer reports afterwards and this reads
            // the report before deciding, on the frame after the one that failed.
            if (_lastLayer is { Unsupported: true })
            {
                _lastLayer = null;
                return GridCoverage.Unsupported;
            }

            if (frame.CellWidth <= 0 || frame.CellHeight <= 0)
                return GridCoverage.Unsupported;

            TerminalSnapshot snapshot;
            try
            {
                // Reads the live buffer without the lock, exactly as the classic path does. Its own
                // catch is what keeps a concurrent write from becoming an unhandled exception out of
                // Render; this needs the same, since it runs before that one is entered.
                snapshot = _builder.Build(
                    frame.Terminal, frame.Palette, frame.StartLine, frame.Rows, frame.Cols,
                    frame.CellWidth, frame.CellHeight, frame.FontSize,
                    frame.Font?.Name ?? "monospace", frame.Foreground, frame.Surface,
                    _requestPaint, frame.Ligatures, frame.ReverseVideo, frame.BlinkOn,
                    frame.BoldIsBright, frame.Contrast);
            }
            catch (Exception ex)
            {
                // A write landed mid-read. Hand the whole frame to the fallback rather than lose it.
                Debug.WriteLine($"[SkiaGridRenderer] snapshot skipped: {ex.Message}");
                return GridCoverage.Everything;
            }

            snapshot.RenderScale = frame.RenderScale;

            var layer = new TerminalSkiaLayer(snapshot, _fonts,
                new Rect(0, 0, frame.Cols * frame.CellWidth, frame.Rows * frame.CellHeight));
            context.Custom(layer);
            _lastLayer = layer;

            return snapshot.AnyDeferred
                ? GridCoverage.Partial(snapshot.Deferred)
                : GridCoverage.Complete;
        }

        public void Dispose()
        {
            _fonts.Dispose();

            // The layer was kept only to read its report; it holds a snapshot and its rows.
            _lastLayer = null;
        }
    }
}
