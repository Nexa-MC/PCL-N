using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiStartupSession
{
    private sealed class StartupWindow : Window
    {
        private readonly Action _rendered;
        private readonly AvaloniaUiStartupCard _card;
        private AvaloniaUiStartupAppearance _appearance;
        private bool _firstRender, _closed;

        internal StartupWindow(Action rendered, Func<string, string> localize, Action retry,
            AvaloniaUiStartupAppearance appearance)
        {
            _rendered = rendered;
            _appearance = appearance;
            Title = "NexaCL";
            Width = 400;
            Height = 280;
            CanResize = false;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowDecorations = WindowDecorations.None;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
            _card = new AvaloniaUiStartupCard(localize, Close, retry);
            _card.Header.PointerPressed += OnHeaderPointerPressed;
            Content = _card;
            PropertyChanged += OnThemeChanged;
            SetAppearance(appearance);
            _card.Opacity = appearance.SuppressMotion ? 1 : .75;
        }

        internal void SetAppearance(AvaloniaUiStartupAppearance appearance)
        {
            if (_closed) return;
            _appearance = appearance;
            RequestedThemeVariant = appearance.ThemeMode switch
            {
                XsrUiThemeMode.Light => ThemeVariant.Light,
                XsrUiThemeMode.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
            UpdateBrand();
            if (appearance.SuppressMotion)
            {
                AvaloniaUiMotion.CancelAll(this);
                _card.Opacity = 1;
            }
        }

        private void OnThemeChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (!_closed && args.Property == ActualThemeVariantProperty) UpdateBrand();
        }

        private void UpdateBrand()
        {
            if (_closed) return;
            bool dark = AvaloniaUiBrandImages.ResolveDark(_appearance.ThemeMode, ActualThemeVariant);
            _card.ApplyAppearance(dark, _appearance.ProductVersion);
            Icon = _card.ProductIcon;
            TransparencyBackgroundFallback = new SolidColorBrush(dark ? Color.FromRgb(13, 26, 36) : Color.FromRgb(248, 252, 255));
        }

        internal void SetStage(string stage, bool failed, bool canRetry = false)
        {
            if (_closed) return;
            _card.SetStage(stage, failed, canRetry);
            Height = failed ? 346 : 280;
        }

        internal void Relocalize(Func<string, string> localize)
        { if (!_closed) _card.Relocalize(localize); }

        private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs args)
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            BeginMoveDrag(args);
            args.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }

        public override void Render(DrawingContext context)
        {
            if (_closed) return;
            base.Render(context);
            if (_firstRender) return;
            _firstRender = true;
            _rendered();
            if (_appearance.SuppressMotion) return;
            AvaloniaUiMotion.Animate(this, "splash-appearance", () => _card.Opacity, value => _card.Opacity = value,
                1, AvaloniaMotionTokens.SplashAppearanceMilliseconds, reducedMotion: () => _appearance.SuppressMotion);
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            AvaloniaUiMotion.CancelAll(this);
            PropertyChanged -= OnThemeChanged;
            _card.Header.PointerPressed -= OnHeaderPointerPressed;
            _card.Dispose();
            base.OnClosed(e);
        }
    }
}
