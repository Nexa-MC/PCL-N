using System.Buffers.Binary;
using Nexa.Core.Media;
using Nexa.Desktop.Ui;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void WardrobePlayerProjectsSevenViewsAndModernLayers()
    {
        PngImage skin = WardrobeDesktopSkin();
        var view = WardrobePlayerView.Isometric;
        HashSet<WardrobePlayerView> visited = [];
        for (int index = 0; index < 7; index++)
        {
            AssertTrue(visited.Add(view));
            XsrUiRasterImage player = WardrobePlayerPresentation.Player(skin, null, false, view);
            AssertEqual(skin.Key, player.Image.Key); AssertEqual(.65, player.AspectRatio);
            AssertEqual(view == WardrobePlayerView.Isometric ? 36 : 12, player.Layers.Count);
            AssertEqual(view is WardrobePlayerView.Top or WardrobePlayerView.Bottom ? 0 : 1, player.BackgroundEllipses.Count);
            foreach (XsrUiImageLayer layer in player.Layers) AssertPlayerLayerInBounds(player, layer);
            view = WardrobePlayerPresentation.Next(view);
        }
        AssertEqual(WardrobePlayerView.Isometric, view);
        AssertEqual(WardrobePlayerView.Isometric, WardrobePlayerPresentation.Next((WardrobePlayerView)99));
        var front = WardrobePlayerPresentation.Player(skin, null, false, WardrobePlayerView.Front);
        var back = WardrobePlayerPresentation.Player(skin, null, false, WardrobePlayerView.Back);
        AssertTrue(front.Layers.Any(layer => layer.Source == new XsrUiRect(8, 8, 8, 8)));
        AssertTrue(back.Layers.Any(layer => layer.Source == new XsrUiRect(24, 8, 8, 8)));
        AssertTrue(front.Layers.Any(layer => layer.Source == new XsrUiRect(36, 52, 4, 12)));
        AssertTrue(front.Layers.Any(layer => layer.Source == new XsrUiRect(20, 52, 4, 12)));
        AssertTrue(front.Layers.Any(layer => layer.Source == new XsrUiRect(40, 8, 8, 8)));
    }

    private static void WardrobePlayerPreservesSlimLegacyAndFallbackModels()
    {
        PngImage skin = WardrobeDesktopSkin();
        var classic = WardrobePlayerPresentation.Player(skin, null, false, WardrobePlayerView.Front);
        var slim = WardrobePlayerPresentation.Player(skin, null, true, WardrobePlayerView.Front);
        AssertTrue(slim.Layers.Any(layer => layer.Source == new XsrUiRect(44, 20, 3, 12)));
        AssertFalse(slim.Layers.Any(layer => layer.Source == new XsrUiRect(44, 20, 4, 12)));
        double classicArmRatio = classic.Layers.Single(layer => layer.Source == new XsrUiRect(44, 20, 4, 12)).Transform!.Value.M11
            / classic.Layers.Single(layer => layer.Source == new XsrUiRect(20, 20, 8, 12)).Transform!.Value.M11;
        double slimArmRatio = slim.Layers.Single(layer => layer.Source == new XsrUiRect(44, 20, 3, 12)).Transform!.Value.M11
            / slim.Layers.Single(layer => layer.Source == new XsrUiRect(20, 20, 8, 12)).Transform!.Value.M11;
        AssertTrue(Math.Abs(.5 - classicArmRatio) < 1e-9); AssertTrue(Math.Abs(.375 - slimArmRatio) < 1e-9);
        PngImage legacy = PlayerImageHeader(64, 32);
        var old = WardrobePlayerPresentation.Player(legacy, null, true, WardrobePlayerView.Front);
        AssertEqual(7, old.Layers.Count);
        AssertTrue(old.Layers.All(layer => layer.Source.Y + layer.Source.Height <= 32));
        AssertTrue(old.Layers.Any(layer => layer.Transform!.Value.M11 < 0));
        AssertEqual(2, old.Layers.Count(layer => layer.Source == new XsrUiRect(44, 20, 4, 12)));
        AssertTrue(old.Layers.Any(layer => layer.Source == new XsrUiRect(40, 8, 8, 8)));
        var steve = WardrobePlayerPresentation.Player(null, null, false);
        var alex = WardrobePlayerPresentation.Player(null, null, true);
        AssertEqual(skin.Key, steve.Image.Key); AssertTrue(steve.Image.Key != alex.Image.Key);
        AssertTrue(ReferenceEquals(steve.Image, WardrobePlayerPresentation.Player(PlayerImageHeader(8, 8), null, false).Image));
        AssertTrue(ReferenceEquals(alex.Image, WardrobePlayerPresentation.Player(null, null, true).Image));
    }

    private static void WardrobePlayerComposesIndependentCapeAndScaledTextures()
    {
        PngImage skin = PlayerImageHeader(128, 128), cape = PlayerImageHeader(128, 64);
        foreach (WardrobePlayerView view in Enum.GetValues<WardrobePlayerView>())
        {
            var player = WardrobePlayerPresentation.Player(skin, cape, false, view);
            AssertEqual(view == WardrobePlayerView.Isometric ? 39 : 13, player.Layers.Count);
            AssertEqual(view == WardrobePlayerView.Isometric ? 3 : 1, player.Layers.Count(layer => ReferenceEquals(layer.SourceImage, cape)));
            foreach (var layer in player.Layers) AssertPlayerLayerInBounds(player, layer);
        }
        foreach (int scale in new[] { 1, 2 })
        {
            PngImage scaledSkin = PlayerImageHeader(64 * scale, 64 * scale), scaledCape = PlayerImageHeader(64 * scale, 32 * scale);
            AssertPlayerCapeOcclusion(scaledSkin, scaledCape, scale, WardrobePlayerView.Back);
            AssertPlayerCapeOcclusion(scaledSkin, scaledCape, scale, WardrobePlayerView.Front);
        }
        AssertEqual(12, WardrobePlayerPresentation.Player(skin, PlayerImageHeader(8, 8), false, WardrobePlayerView.Back).Layers.Count);
    }

    private static void AssertPlayerCapeOcclusion(PngImage skin, PngImage cape, int scale, WardrobePlayerView view)
    {
        var player = WardrobePlayerPresentation.Player(skin, cape, false, view);
        int capeIndex = Enumerable.Range(0, player.Layers.Count).Single(index => ReferenceEquals(cape, player.Layers[index].SourceImage));
        var capeLayer = player.Layers[capeIndex];
        AssertEqual(new XsrUiRect((view == WardrobePlayerView.Back ? 12 : 1) * scale, scale, 10 * scale, 16 * scale), capeLayer.Source);
        XsrUiRect capeBounds = PlayerLayerBounds(capeLayer);
        int overlappingSkinFaces = 0;
        for (int index = 0; index < player.Layers.Count; index++)
        {
            if (index == capeIndex) continue;
            XsrUiRect bounds = PlayerLayerBounds(player.Layers[index]);
            if (Math.Min(bounds.X + bounds.Width, capeBounds.X + capeBounds.Width) <= Math.Max(bounds.X, capeBounds.X)
                || Math.Min(bounds.Y + bounds.Height, capeBounds.Y + capeBounds.Height) <= Math.Max(bounds.Y, capeBounds.Y)) continue;
            overlappingSkinFaces++;
            AssertTrue(view == WardrobePlayerView.Back ? index < capeIndex : index > capeIndex);
        }
        // The torso and four limbs, including their outer layers, overlap the cape.
        // Head/hat planes may sort later in Back because they are nearer the camera;
        // their projected rectangles sit above the cape and cannot occlude it.
        AssertEqual(10, overlappingSkinFaces);
    }

    private static XsrUiRect PlayerLayerBounds(XsrUiImageLayer layer)
    {
        XsrUiImageTransform t = layer.Transform!.Value;
        double left = t.OffsetX + Math.Min(0, t.M11) + Math.Min(0, t.M21);
        double top = t.OffsetY + Math.Min(0, t.M12) + Math.Min(0, t.M22);
        return new(left, top, Math.Abs(t.M11) + Math.Abs(t.M21), Math.Abs(t.M12) + Math.Abs(t.M22));
    }

    private static void AssertPlayerLayerInBounds(XsrUiRasterImage player, XsrUiImageLayer layer)
    {
        PngImage source = layer.SourceImage ?? player.Image;
        AssertTrue(layer.Source.X >= 0 && layer.Source.Y >= 0 && layer.Source.Width > 0 && layer.Source.Height > 0
            && layer.Source.X + layer.Source.Width <= source.Width && layer.Source.Y + layer.Source.Height <= source.Height);
        XsrUiImageTransform transform = layer.Transform!.Value;
        AssertTrue(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21) > 1e-12);
        foreach (var point in new[] { new XsrUiPoint(transform.OffsetX, transform.OffsetY),
            new(transform.OffsetX + transform.M11, transform.OffsetY + transform.M12),
            new(transform.OffsetX + transform.M21, transform.OffsetY + transform.M22),
            new(transform.OffsetX + transform.M11 + transform.M21, transform.OffsetY + transform.M12 + transform.M22) })
            AssertTrue(double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1);
    }

    // Geometry tests inspect encoded carriers, without asking a native PNG decoder to accept fixtures.
    private static PngImage PlayerImageHeader(int width, int height)
    {
        byte[] bytes = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return PngImage.TryCreate(bytes)!;
    }
}
