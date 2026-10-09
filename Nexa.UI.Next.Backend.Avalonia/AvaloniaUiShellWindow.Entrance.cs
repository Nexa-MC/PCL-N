using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiShellWindow
{
    private NeonEntranceTrace? _entranceTrace;
    private bool _startupEntranceCompleted;
    internal bool StartupMotionSuppressed { get; set; }

    private void RunStartupReveal()
    {
        if (_disposed || _closeAnimationStarted || _startupEntranceCompleted || _entranceTrace is not null) return;
        // Hidden preparation suspends optional scene motion before native focus is granted.
        // This finite first-show decoration follows the explicit preference independently.
        if (StartupMotionSuppressed || _shell.Renderer.ReducedMotion || _root.Bounds.Width <= 0 || _root.Bounds.Height <= 0)
        {
            OnStartupRevealCompleted();
            return;
        }

        // Only this small decoration moves. The scene, native shape, window dimensions and
        // input coordinates are already final, including when the user resizes during entrance.
        var trace = new NeonEntranceTrace
        {
            Name = "NexaWindowEntrance",
            Width = CloseIconSize,
            Height = CloseIconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Focusable = false,
        };
        _entranceTrace = trace;
        _maskedContent.Opacity = AvaloniaMotionTokens.StartupSceneInitialOpacity;
        _root.Children.Add(trace);
        AvaloniaUiMotion.Animate(this, "startup-reveal", () => trace.Progress, value =>
        {
            trace.Progress = value;
            _maskedContent.Opacity = AvaloniaMotionTokens.StartupSceneInitialOpacity
                + (1 - AvaloniaMotionTokens.StartupSceneInitialOpacity) * AvaloniaUiMotion.EaseOut(value);
            trace.InvalidateVisual();
        }, 1, AvaloniaMotionTokens.StartupRevealMilliseconds,
            easing: static progress => progress,
            completed: OnStartupRevealCompleted,
            reducedMotion: () => StartupMotionSuppressed || _shell.Renderer.ReducedMotion);
    }

    private void OnStartupRevealCompleted()
    {
        if (_disposed || _closeAnimationStarted || _startupEntranceCompleted) return;
        _startupEntranceCompleted = true;
        CancelStartupEntrance();
        StartupRevealCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CancelStartupEntrance()
    {
        _awaitingFirstSceneCommit = false;
        AvaloniaUiMotion.Cancel(this, "startup-reveal");
        _maskedContent.Opacity = 1;
        if (_entranceTrace is not { } trace) return;
        _entranceTrace = null;
        _root.Children.Remove(trace);
    }

    private sealed class NeonEntranceTrace : Control
    {
        private readonly LinearGradientBrush _light = new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromRgb(100, 232, 187), 0),
                new GradientStop(Color.FromRgb(147, 224, 237), 1),
            ],
        };
        private readonly Pen _glow;
        private readonly Pen _core;

        internal NeonEntranceTrace()
        {
            _glow = new Pen(_light, 14, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _core = new Pen(_light, 4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        }

        internal double Progress { get; set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            double progress = Math.Clamp(Progress, 0, 1);
            if (progress <= 0 || progress >= 1 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            double width = Bounds.Width, height = Bounds.Height;
            Point bottomLeft = new(width * .23, height * .76);
            Point topLeft = new(width * .23, height * .24);
            Point bottomRight = new(width * .77, height * .76);
            Point topRight = new(width * .77, height * .24);
            // A bounded three-segment light trace follows the N's two stems and diagonal.
            // It draws no source SVG at runtime, has no loop, and fades completely by 420 ms.
            double reach = Math.Min(3, progress / .7 * 3);
            double opacity = Math.Min(1, progress / .12) * Math.Clamp((1 - progress) / .3, 0, 1);
            using (context.PushOpacity(opacity * .16)) DrawTrace(context, _glow, reach);
            using (context.PushOpacity(opacity * .8)) DrawTrace(context, _core, reach);

            void DrawTrace(DrawingContext drawing, Pen pen, double segments)
            {
                Segment(bottomLeft, topLeft, 0);
                Segment(topLeft, bottomRight, 1);
                Segment(bottomRight, topRight, 2);
                void Segment(Point from, Point to, int index)
                {
                    double amount = Math.Clamp(segments - index, 0, 1);
                    if (amount <= 0) return;
                    drawing.DrawLine(pen, from, new(from.X + (to.X - from.X) * amount, from.Y + (to.Y - from.Y) * amount));
                }
            }
        }
    }
}
