using Avalonia.Media;

namespace Iciclecreek.Terminal.Rendering
{
    /// <summary>
    /// The DrawingContext renderer: a fill and a text draw per styled run, recorded into the frame's
    /// display list. Draws every row it is asked for and declines nothing, which makes it the
    /// fallback for every other implementation as well as the default.
    /// </summary>
    /// <remarks>
    /// <para>An adapter, for now. The drawing itself still lives in <see cref="TerminalView"/> —
    /// <c>BuildLineRuns</c>, <c>CollectLineRuns</c> and the row walk — because it reaches into the
    /// view's per-frame state (the palette snapshot, the contrast floor, the deferred sized-block
    /// list, the run text builder) and moving it is a change of its own, worth making separately
    /// from introducing the seam.</para>
    /// <para>What the seam already buys, even as an adapter: the direct renderer is chosen and
    /// replaced through one reference instead of two latched booleans, a frame's state travels as a
    /// value instead of as a dozen field reads, and the partial answer is expressed in the type
    /// system rather than in a nullable snapshot the row loop happens to consult.</para>
    /// </remarks>
    internal sealed class ClassicGridRenderer : ITerminalGridRenderer
    {
        private readonly TerminalView _view;

        public ClassicGridRenderer(TerminalView view) => _view = view;

        /// <inheritdoc/>
        public GridCoverage DrawGrid(DrawingContext context, in GridFrame frame, in GridCoverage outstanding)
        {
            _view.DrawClassicRows(context, frame, outstanding);
            return GridCoverage.Complete;
        }
    }
}
