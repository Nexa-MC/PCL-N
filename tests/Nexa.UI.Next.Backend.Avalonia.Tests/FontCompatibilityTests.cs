using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void TypefaceCachePreservesUsableDefault()
    {
        int probes = 0;
        var cache = new AvaloniaUiTypefaceCache(typeface =>
        {
            probes++;
            return typeface.FontFamily == FontFamily.Default;
        }, () => throw new InvalidOperationException("A usable default must not enumerate system fonts."));

        Typeface regular = cache.Get(FontWeight.Normal);
        AssertEqual(FontFamily.Default, regular.FontFamily);
        for (int index = 0; index < 1000; index++) AssertEqual(regular, cache.Get(FontWeight.Normal));
        AssertEqual(1, probes);
        Typeface styled = cache.Get(FontWeight.Bold, FontStyle.Italic);
        AssertEqual(FontFamily.Default, styled.FontFamily);
        AssertEqual(FontWeight.Bold, styled.Weight);
        AssertEqual(FontStyle.Italic, styled.Style);
        AssertEqual(2, probes);
    }

    private static void TypefaceCacheProbesFallbacks()
    {
        // The default and installed names can all exist while their font data is unloadable.
        List<Typeface> attempted = [];
        int enumerations = 0;
        var cache = new AvaloniaUiTypefaceCache(typeface =>
        {
            attempted.Add(typeface);
            return typeface.FontFamily.Name == "Loadable Test Family";
        }, () =>
        {
            enumerations++;
            return [new FontFamily("Broken Test Family"), new FontFamily("Noto Sans"),
                new FontFamily("Loadable Test Family")];
        });

        Typeface face = cache.Get(FontWeight.SemiBold);
        AssertEqual("Loadable Test Family", face.FontFamily.Name);
        AssertEqual(FontWeight.SemiBold, face.Weight);
        AssertEqual(FontStyle.Normal, face.Style);
        AssertEqual(FontFamily.Default, attempted[0].FontFamily);
        AssertTrue(attempted.Any(typeface => typeface.FontFamily.Name == "Broken Test Family"));
        AssertEqual(1, attempted.Count(typeface => typeface.FontFamily.Name == "Noto Sans"));
        int probes = attempted.Count;
        AssertEqual(face, cache.Get(FontWeight.SemiBold));
        AssertEqual(probes, attempted.Count);
        AssertEqual(1, enumerations);
        Typeface italic = cache.Get(FontWeight.Bold, FontStyle.Italic);
        AssertEqual("Loadable Test Family", italic.FontFamily.Name);
        AssertEqual(FontWeight.Bold, italic.Weight);
        AssertEqual(FontStyle.Italic, italic.Style);
        AssertEqual(2, enumerations);

        var conventional = new AvaloniaUiTypefaceCache(typeface => typeface.FontFamily.Name == "DejaVu Sans",
            () => throw new InvalidOperationException("A usable conventional fallback must not enumerate fonts."));
        AssertEqual("DejaVu Sans", conventional.Get(FontWeight.Normal).FontFamily.Name);

        // A working regular default does not prove its styled variants can be loaded.
        var mixed = new AvaloniaUiTypefaceCache(typeface =>
            (typeface.FontFamily == FontFamily.Default && typeface.Weight == FontWeight.Normal && typeface.Style == FontStyle.Normal)
            || typeface.FontFamily.Name == "DejaVu Sans", () => []);
        AssertEqual(FontFamily.Default, mixed.Get(FontWeight.Normal).FontFamily);
        AssertEqual("DejaVu Sans", mixed.Get(FontWeight.Bold, FontStyle.Italic).FontFamily.Name);
        AssertEqual(FontFamily.Default, mixed.Get(FontWeight.Normal).FontFamily);
    }

    private static void TypefaceCacheDoesNotCacheFailure()
    {
        bool canLoad = false;
        var cache = new AvaloniaUiTypefaceCache(_ => canLoad, () => []);
        bool failed = false;
        try { cache.Get(FontWeight.Normal); }
        catch (InvalidOperationException error)
        {
            failed = error.Message.Contains("No usable system font", StringComparison.Ordinal);
        }
        AssertTrue(failed);
        canLoad = true;
        AssertEqual(FontFamily.Default, cache.Get(FontWeight.Normal).FontFamily);
    }

    private static int RunNativeFontSmoke(bool expectFallback)
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        bool defaultLoads = FontManager.Current.TryGetGlyphTypeface(new Typeface(FontFamily.Default), out _);
        Console.WriteLine($"Native default family={FontManager.Current.DefaultFontFamily}; loadable={defaultLoads}");
        if (expectFallback) AssertFalse(defaultLoads);
        VerifyTypefaceRenderingAndInput();
        return 0;
    }

    private static void VerifyTypefaceRenderingAndInput()
    {
        foreach (FontWeight weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
            foreach (FontStyle style in new[] { FontStyle.Normal, FontStyle.Italic })
            {
                Typeface face = AvaloniaUiTypefaceCache.GetDefault(weight, style);
                AssertTrue(FontManager.Current.TryGetGlyphTypeface(face, out _));
                FormattedText text = new("Nexa 中文", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, 14, Brushes.Black);
                AssertTrue(double.IsFinite(text.Width) && text.Width > 0);
                AssertTrue(double.IsFinite(text.Height) && text.Height > 0);
                Console.WriteLine($"Typeface family={face.FontFamily}; weight={weight}; style={style}; width={text.Width}");
            }

        using RenderTargetBitmap bitmap = new(new PixelSize(180, 40), new Vector(96, 96));
        using DrawingContext context = bitmap.CreateDrawingContext();
        var tree = new XsrUiTree();
        var node = Node(tree.Create("font-probe"), XsrUiSemanticRole.Text) with
        {
            Rect = new XsrUiRect(0, 0, 180, 40),
            Text = "Nexa 中文",
            TextRuns = [new XsrUiTextRun(0, 4, new XsrUiColor(20, 30, 40), Bold: true, Italic: true)],
            VisualStyle = new XsrUiVisualStyle { FontSize = 14, FontWeight = 600 }.Snapshot(),
        };
        var control = new AvaloniaUiSceneNodeControl(_ => { }, _ => { }, () => true);
        try
        {
            Draw(node);
            Draw(node with
            {
                Role = XsrUiSemanticRole.Button,
                CapsuleExpansionProgress = 1,
                VisualStyle = new XsrUiVisualStyle { HoverExpand = true }.Snapshot(),
            });
            var input = node with
            {
                Role = XsrUiSemanticRole.TextInput,
                Text = null,
                TextRuns = null,
                IsFocused = true,
                TextInput = new XsrUiTextInputSnapshot("Player 中文", "Name", false, 1, 6, "你好"),
            };
            control.Apply(input);
            AssertEqual(0, control.TextPositionAt(12));
            AssertEqual(9, control.TextPositionAt(10000));
            Draw(input);
            AssertTrue(control.TextCursorRectangle.Width > 0 && control.TextCursorRectangle.Height > 0);
            AssertTrue(double.IsFinite(control.TextCursorRectangle.X));
            Draw(input with { TextInput = new XsrUiTextInputSnapshot(new string('W', 100), "", false, 0, 100, "中") });
            AssertTrue(control.TextCursorRectangle.Right <= 180);
            Draw(input with { TextInput = new XsrUiTextInputSnapshot("", "Placeholder 中文", false, 0, 0, "") });
            Draw(input with { TextInput = new XsrUiTextInputSnapshot("••••", "", true, 0, 4, "••") });
        }
        finally { control.ReleasePresentation(); }
        Console.WriteLine("PASS: font measurement, styled labels, capsules, input selection, preedit, caret and hit testing");

        void Draw(XsrUiSceneNode current)
        {
            control.Apply(current);
            control.Measure(new Size(180, 40));
            control.Arrange(new Rect(0, 0, 180, 40));
            control.Render(context);
        }
    }
}
