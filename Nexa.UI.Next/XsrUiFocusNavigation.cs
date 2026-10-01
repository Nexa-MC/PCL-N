namespace Nexa.UI.Next;

/// <summary>
/// Optional UI-thread logical focus resolver for a virtualized collection. Return an unassigned
/// target to retain scene-order traversal. The renderer validates the target before focusing it.
/// The resolver may materialize UI entities but must not render, perform I/O or call services.
/// </summary>
public sealed record XsrUiFocusNavigation(Func<XsrUiEntityId, bool, XsrUiEntityId> Resolve);
