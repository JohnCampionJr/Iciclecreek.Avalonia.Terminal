using Avalonia.Media;
using XTerm.Common;

namespace Iciclecreek.Terminal.Rendering
{
    /// <summary>
    /// Everything one frame's grid drawing needs, resolved once on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>Resolved rather than referenced, deliberately. An implementation may draw on the
    /// compositor's thread while the pty reader writes to the buffer, so anything it reads there is
    /// a race with no lock to take — the values that can be settled for the whole frame are settled
    /// here, where the control already reads them, and a renderer that needs more takes it during
    /// its own <see cref="ITerminalGridRenderer.DrawGrid"/> call on this thread.</para>
    /// <para><see cref="Terminal"/> is the exception and is handed over knowingly: the classic
    /// renderer reads lines straight from the buffer as it draws, on this thread, which is the same
    /// unlocked read it has always done.</para>
    /// </remarks>
    internal readonly record struct GridFrame(
        XTerm.Terminal Terminal,
        ColorSnapshot Palette,
        MinimumContrast Contrast,
        int StartLine,
        int Rows,
        int Cols,
        double CellWidth,
        double CellHeight,
        double FontSize,
        FontFamily? Font,
        IBrush? Foreground,
        IBrush? Surface,
        double RenderScale,
        bool Ligatures,
        bool ReverseVideo,
        bool BlinkOn,
        bool BoldIsBright);

    /// <summary>
    /// What a renderer actually drew — which is not always everything it was asked for.
    /// </summary>
    /// <remarks>
    /// The partial answer is the point of this type. A renderer is allowed to decline the rows it
    /// cannot draw faithfully rather than drawing them wrong: the direct Skia path declines doubled
    /// rows, lines carrying OSC 66 sized runs, and lines holding a picture, because each needs
    /// geometry its snapshot has no field for. The caller then hands exactly those rows to a
    /// renderer that can, and both draw into the same frame.
    /// </remarks>
    internal readonly struct GridCoverage
    {
        private enum Kind : byte
        {
            /// <summary>Nothing drawn yet: every row is outstanding. The value a frame starts at.</summary>
            All,

            /// <summary>Everything drawn.</summary>
            None,

            /// <summary>Everything except the rows in <see cref="Declined"/>.</summary>
            Some,

            /// <summary>Nothing drawn, and this renderer cannot draw here at all.</summary>
            Unsupported,
        }

        private readonly Kind _kind;

        private GridCoverage(Kind kind, bool[]? declined)
        {
            _kind = kind;
            Declined = declined;
        }

        /// <summary>Every row still needs drawing — what a frame starts with.</summary>
        public static GridCoverage Everything => new(Kind.All, null);

        /// <summary>Every row was drawn; the caller is done.</summary>
        public static GridCoverage Complete => new(Kind.None, null);

        /// <summary>Drew everything except the rows flagged here, indexed by screen row.</summary>
        public static GridCoverage Partial(bool[] declined) => new(Kind.Some, declined);

        /// <summary>
        /// This renderer cannot draw in this environment at all — the backend will not give it what
        /// it needs. The caller should replace it, permanently, and draw with the fallback: every
        /// row is still outstanding.
        /// </summary>
        public static GridCoverage Unsupported => new(Kind.Unsupported, null);

        /// <summary>Rows left undrawn, indexed by screen row; null when that is all or none of them.</summary>
        public bool[]? Declined { get; }

        /// <summary>See <see cref="Unsupported"/>.</summary>
        public bool Unavailable => _kind == Kind.Unsupported;

        /// <summary>Whether anything is left for another renderer to draw.</summary>
        public bool IsComplete => _kind == Kind.None;

        /// <summary>Whether <paramref name="screenRow"/> still needs drawing.</summary>
        public bool NeedsDrawing(int screenRow) => _kind switch
        {
            Kind.None => false,
            Kind.Some => (uint)screenRow < (uint)Declined!.Length && Declined[screenRow],
            _ => true,   // All, and Unsupported: nothing was drawn
        };
    }

    /// <summary>
    /// Draws the terminal's cell grid — the text and its backgrounds — for one frame.
    /// </summary>
    /// <remarks>
    /// <para>The GRID only. Everything around it belongs to the control and is drawn the same way
    /// whichever renderer is in use: the surface, the gutter, OSC 66 blocks, search highlights, the
    /// hovered link, the selection, the cursor and the IME preedit. Those are overlays over a grid,
    /// not part of one, and a renderer that drew them would have to be replaced wholesale rather
    /// than swapped for the part it improves.</para>
    /// <para>Implementations may draw on another thread — see <see cref="GridFrame"/> — and may
    /// decline rows they cannot draw faithfully; see <see cref="GridCoverage"/>.</para>
    /// </remarks>
    internal interface ITerminalGridRenderer
    {
        /// <summary>
        /// Draws the rows of <paramref name="frame"/> that <paramref name="outstanding"/> still
        /// needs, and reports what was left undrawn.
        /// </summary>
        /// <param name="context">The frame's drawing context; an implementation may enqueue a
        /// custom operation into it rather than drawing immediately.</param>
        /// <param name="frame">The frame's resolved state.</param>
        /// <param name="outstanding">What an earlier renderer left undrawn;
        /// <see cref="GridCoverage.Everything"/> on the first call of a frame.</param>
        GridCoverage DrawGrid(DrawingContext context, in GridFrame frame, in GridCoverage outstanding);
    }
}
