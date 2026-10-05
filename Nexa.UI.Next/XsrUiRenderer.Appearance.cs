namespace Nexa.UI.Next;

public sealed partial class XsrUiRenderer
{
    private XsrUiColorScheme _colorScheme;

    /// <summary>Render-thread palette projection. Changing it preserves the original tree tokens and geometry.</summary>
    public XsrUiColorScheme ColorScheme
    {
        get => _colorScheme;
        set
        {
            if (value.Accent is not (XsrUiAccent.Blue or XsrUiAccent.Purple or XsrUiAccent.Green or XsrUiAccent.Orange))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_colorScheme == value) return;
            _colorScheme = value;
            if (_root.IsAssigned && _tree.IsAlive(_root)) _tree.MarkDirty(_root, XsrUiDirtyKinds.Paint);
        }
    }

    private XsrUiVisualStyleSnapshot ProjectStyle(XsrUiVisualStyle? source) =>
        _colorScheme.Project(source?.Snapshot() ?? default);

    private IReadOnlyList<XsrUiTextRun>? ProjectTextRuns(IReadOnlyList<XsrUiTextRun>? source)
    {
        if (source is null || source.Count == 0 || _colorScheme == default) return source;
        return Array.AsReadOnly(source.Select(run => run with { Foreground = _colorScheme.Foreground(run.Foreground) }).ToArray());
    }
}
