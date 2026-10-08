namespace Nexa.UI.Next;

/// <summary>Last completed scene facts and current queued state, without forcing a render.</summary>
public readonly record struct XsrUiRendererDiagnostics(long SceneVersion, int SceneNodes,
    int LayoutVisits, int TreeEntities, int PendingStateEntries);

public sealed partial class XsrUiRenderer
{
    /// <summary>Read on the render thread; never drains state or changes tree/scene facts.</summary>
    public XsrUiRendererDiagnostics CaptureDiagnostics() => new(SceneVersion, _scene?.Count ?? 0,
        LastLayoutVisits, _tree.Count, _stateBridge?.PendingCount ?? 0);
}
