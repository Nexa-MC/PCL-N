using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Provider;
using Avalonia.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

internal sealed partial class AvaloniaUiSceneNodeControl
{
    private static readonly StyledProperty<double> ToggleProgressProperty =
        AvaloniaProperty.Register<AvaloniaUiSceneNodeControl, double>("ToggleProgress");

    internal double PresentedToggleProgress => GetValue(ToggleProgressProperty);

    private void ApplyToggle(XsrUiSceneNode previous, XsrUiSceneNode node)
    {
        if (node.IsChecked is not { } value) return;
        if (!_applied) SetValue(ToggleProgressProperty, value ? 1d : 0d);
        else if (previous.IsChecked != value)
        {
            AnimateFact(ToggleProgressProperty, value ? 1 : 0, 180);
            if (global::Avalonia.Automation.Peers.ControlAutomationPeer.FromElement(this) is { } peer)
                peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                    previous.IsChecked == true ? ToggleState.On : ToggleState.Off,
                    value ? ToggleState.On : ToggleState.Off);
        }
    }

    private void DrawToggle(DrawingContext context, Rect rect, XsrUiVisualStyleSnapshot style)
    {
        if (rect.Width <= 4 || rect.Height <= 4) return;
        double progress = Math.Clamp(PresentedToggleProgress, 0, 1);
        var accent = ToggleFill(style);
        var surface = _node.ColorScheme.Project(OwnedToggleSurface);
        if (_node.Role == XsrUiSemanticRole.Switch)
        {
            double height = Math.Min(28, rect.Height - 4);
            double width = Math.Min(48, rect.Width - 4);
            Rect rail = new((rect.Width - width) / 2, (rect.Height - height) / 2, width, height);
            context.DrawRectangle(Brush(_node.ColorScheme.Project(OwnedToggleRail).Background), null, new RoundedRect(rail, new CornerRadius(height / 2)));
            using (context.PushOpacity(progress))
                context.DrawRectangle(Brush(accent), null, new RoundedRect(rail, new CornerRadius(height / 2)));
            double diameter = height - 4;
            context.DrawEllipse(Brush(new(255, 255, 255)), null,
                new Point(rail.X + 2 + diameter / 2 + (width - height) * progress, rail.Center.Y), diameter / 2, diameter / 2);
        }
        else
        {
            Rect box = new(2, (rect.Height - 20) / 2, 20, 20);
            context.DrawRectangle(Brush(surface.Background), new Pen(Brush(surface.Border), 1),
                new RoundedRect(box, new CornerRadius(5)));
            using (context.PushOpacity(progress))
            {
                context.DrawRectangle(Brush(accent), null, new RoundedRect(box, new CornerRadius(5)));
                var pen = new Pen(Brush(new(255, 255, 255)), 2);
                context.DrawLine(pen, new(box.X + 5, box.Y + 10), new(box.X + 9, box.Y + 14));
                context.DrawLine(pen, new(box.X + 9, box.Y + 14), new(box.X + 16, box.Y + 6));
            }
            if (_node.Text is { Length: > 0 }) DrawText(context, style, 32);
        }
        if (_node.IsFocusVisible)
            context.DrawRectangle(null, new Pen(Brush(_node.ColorScheme.AccentText), 2),
                new RoundedRect(rect.Deflate(1), new CornerRadius(_node.Role == XsrUiSemanticRole.Switch ? 16 : 6)));
    }
}
