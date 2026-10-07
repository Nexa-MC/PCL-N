using Nexa.Core.Media;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.Desktop.Ui;

internal enum WardrobePlayerView { Isometric, Front, Back, Left, Right, Top, Bottom }

/// <summary>Pure Minecraft texture geometry. PNG decoding and drawing belong to the native edge.</summary>
internal static class WardrobePlayerPresentation
{
    private const double Aspect = .65;
    private static readonly Lazy<PngImage> Steve = new(() => DefaultSkin("Steve"));
    private static readonly Lazy<PngImage> Alex = new(() => DefaultSkin("Alex"));

    public static WardrobePlayerView Next(WardrobePlayerView view) => view switch
    {
        WardrobePlayerView.Isometric => WardrobePlayerView.Front,
        WardrobePlayerView.Front => WardrobePlayerView.Back,
        WardrobePlayerView.Back => WardrobePlayerView.Left,
        WardrobePlayerView.Left => WardrobePlayerView.Right,
        WardrobePlayerView.Right => WardrobePlayerView.Top,
        WardrobePlayerView.Top => WardrobePlayerView.Bottom,
        _ => WardrobePlayerView.Isometric,
    };

    public static XsrUiRasterImage Player(PngImage? skin, PngImage? cape, bool slim,
        WardrobePlayerView view = WardrobePlayerView.Isometric)
    {
        skin = IsSkin(skin) ? skin! : slim ? Alex.Value : Steve.Value;
        bool modern = skin.Height == skin.Width;
        slim &= modern;
        if (!IsCape(cape)) cape = null;
        if (!Enum.IsDefined(view)) view = WardrobePlayerView.Isometric;
        List<Face> geometry = [];
        double arm = slim ? 3 : 4;
        AddBox(geometry, new(-4, 24, -4, 4, 32, 4), Head(0), skin, false);
        AddBox(geometry, new(-4, 12, -2, 4, 24, 2), Torso(16), skin, false);
        AddBox(geometry, new(-4 - arm, 12, -2, -4, 24, 2), Limb(40, 16, arm), skin, false);
        AddBox(geometry, new(4, 12, -2, 4 + arm, 24, 2), modern ? Limb(32, 48, arm) : Limb(40, 16, arm).Mirror(), skin, false);
        AddBox(geometry, new(-4, 0, -2, 0, 12, 2), Limb(0, 16, 4), skin, false);
        AddBox(geometry, new(0, 0, -2, 4, 12, 2), modern ? Limb(16, 48, 4) : Limb(0, 16, 4).Mirror(), skin, false);
        // Legacy skins also have the independent hat texture in the upper half.
        AddBox(geometry, new Box(-4, 24, -4, 4, 32, 4).Expanded(.5), Head(32), skin, true);
        if (modern)
        {
            AddBox(geometry, new Box(-4, 12, -2, 4, 24, 2).Expanded(.25), Torso(32), skin, true);
            AddBox(geometry, new Box(-4 - arm, 12, -2, -4, 24, 2).Expanded(.25), Limb(40, 32, arm), skin, true);
            AddBox(geometry, new Box(4, 12, -2, 4 + arm, 24, 2).Expanded(.25), Limb(48, 48, arm), skin, true);
            AddBox(geometry, new Box(-4, 0, -2, 0, 12, 2).Expanded(.25), Limb(0, 32, 4), skin, true);
            AddBox(geometry, new Box(0, 0, -2, 4, 12, 2).Expanded(.25), Limb(0, 48, 4), skin, true);
        }
        if (cape is not null)
            AddBox(geometry, new(-5, 6.5, -2.8, 5, 22.5, -2.25),
                new(new(0, 1, 1, 16), new(11, 1, 1, 16), new(1, 0, 10, 1), new(11, 0, 10, 1), new(1, 1, 10, 16), new(12, 1, 10, 16)), cape, false);

        Vertex camera = Camera(view);
        Face[] visible = geometry.Where(face => Dot(face.Normal, camera) > .0001)
            .OrderBy(face => Dot(face.Center, camera)).ToArray();
        double left = double.PositiveInfinity, right = double.NegativeInfinity;
        double top = double.PositiveInfinity, bottom = double.NegativeInfinity;
        foreach (Face face in visible)
        {
            Include(Project(face.Origin, view)); Include(Project(face.Across, view));
            Include(Project(face.Down, view)); Include(Project(face.Across + face.Down - face.Origin, view));
        }
        bool ground = view is not (WardrobePlayerView.Top or WardrobePlayerView.Bottom);
        XsrUiPoint shadow = Project(new(0, -.45, 0), view);
        if (ground) { Include(new(shadow.X - 6.5, shadow.Y - 1.1)); Include(new(shadow.X + 6.5, shadow.Y + 1.1)); }
        const double padding = .03575;
        double scale = Math.Min((Aspect - padding * 2) / (right - left), (1 - padding * 2) / (bottom - top));
        double offsetX = (Aspect - (right - left) * scale) / 2 - left * scale;
        double offsetY = (1 - (bottom - top) * scale) / 2 - top * scale;
        List<XsrUiImageLayer> layers = new(visible.Length);
        foreach (Face face in visible)
        {
            XsrUiPoint origin = Canvas(Project(face.Origin, view));
            XsrUiPoint across = Canvas(Project(face.Across, view));
            XsrUiPoint down = Canvas(Project(face.Down, view));
            double ax = across.X - origin.X, ay = across.Y - origin.Y;
            double dx = down.X - origin.X, dy = down.Y - origin.Y;
            if (face.Texture.Flip)
            {
                origin = across; ax = -ax; ay = -ay;
            }
            double pixels = face.Image.Width / 64d;
            var source = face.Texture.Region;
            layers.Add(new(new(source.X * pixels, source.Y * pixels, source.Width * pixels, source.Height * pixels), default)
            {
                SourceImage = ReferenceEquals(face.Image, skin) ? null : face.Image,
                Transform = new(ax, ay, dx, dy, origin.X, origin.Y),
                Shade = face.Overlay ? XsrUiImageShade.None : Shade(face.Normal),
            });
        }
        IReadOnlyList<XsrUiImageEllipse> ellipses = ground
            ? Array.AsReadOnly(new[] { new XsrUiImageEllipse(new((shadow.X * scale + offsetX - 6.5 * scale) / Aspect,
                shadow.Y * scale + offsetY - 1.1 * scale, 13 * scale / Aspect, 2.2 * scale), 0x22000000) })
            : Array.Empty<XsrUiImageEllipse>();
        return new(skin, layers.AsReadOnly()) { AspectRatio = Aspect, BackgroundEllipses = ellipses };

        void Include(XsrUiPoint point)
        {
            left = Math.Min(left, point.X); right = Math.Max(right, point.X);
            top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
        }
        XsrUiPoint Canvas(XsrUiPoint point) => new((point.X * scale + offsetX) / Aspect, point.Y * scale + offsetY);
    }

    private static bool IsSkin(PngImage? image) => image is not null && image.Width >= 64 && image.Width % 64 == 0
        && (image.Height == image.Width || image.Height * 2 == image.Width);
    private static bool IsCape(PngImage? image) => image is not null && image.Width >= 64 && image.Width % 64 == 0
        && image.Height >= image.Width / 2;

    private static PngImage DefaultSkin(string name)
    {
        using Stream stream = typeof(AvaloniaUiPlatformActions).Assembly.GetManifestResourceStream(
            $"Nexa.UI.Next.Backend.Avalonia.Assets.Avatars.{name}.png")
            ?? throw new InvalidOperationException($"Missing embedded player texture: {name}.");
        using MemoryStream encoded = new(); stream.CopyTo(encoded);
        return PngImage.TryCreate(encoded.GetBuffer().AsSpan(0, checked((int)encoded.Length)))
            ?? throw new InvalidOperationException($"Invalid embedded player texture: {name}.");
    }

    private static FaceUvs Head(double x) => new(new(x, 8, 8, 8), new(x + 16, 8, 8, 8),
        new(x + 8, 0, 8, 8), new(x + 16, 0, 8, 8), new(x + 8, 8, 8, 8), new(x + 24, 8, 8, 8));
    private static FaceUvs Torso(double y) => new(new(16, y + 4, 4, 12), new(28, y + 4, 4, 12),
        new(20, y, 8, 4), new(28, y, 8, 4), new(20, y + 4, 8, 12), new(32, y + 4, 8, 12));
    private static FaceUvs Limb(double x, double y, double width) => new(new(x, y + 4, 4, 12),
        new(x + 4 + width, y + 4, 4, 12), new(x + 4, y, width, 4), new(x + 4 + width, y, width, 4),
        new(x + 4, y + 4, width, 12), new(x + 8 + width, y + 4, width, 12));

    private static void AddBox(List<Face> faces, Box b, FaceUvs uv, PngImage image, bool overlay)
    {
        Add(new(b.X0, b.Y1, b.Z0), new(b.X0, b.Y1, b.Z1), new(b.X0, b.Y0, b.Z0), new(-1, 0, 0), uv.NegativeX);
        Add(new(b.X1, b.Y1, b.Z1), new(b.X1, b.Y1, b.Z0), new(b.X1, b.Y0, b.Z1), new(1, 0, 0), uv.PositiveX);
        Add(new(b.X0, b.Y1, b.Z0), new(b.X1, b.Y1, b.Z0), new(b.X0, b.Y1, b.Z1), new(0, 1, 0), uv.Top);
        Add(new(b.X0, b.Y0, b.Z1), new(b.X1, b.Y0, b.Z1), new(b.X0, b.Y0, b.Z0), new(0, -1, 0), uv.Bottom);
        Add(new(b.X0, b.Y1, b.Z1), new(b.X1, b.Y1, b.Z1), new(b.X0, b.Y0, b.Z1), new(0, 0, 1), uv.Front);
        Add(new(b.X1, b.Y1, b.Z0), new(b.X0, b.Y1, b.Z0), new(b.X1, b.Y0, b.Z0), new(0, 0, -1), uv.Back);
        void Add(Vertex origin, Vertex across, Vertex down, Vertex normal, Uv texture)
            => faces.Add(new(origin, across, down, normal, texture, image, overlay));
    }

    private static XsrUiPoint Project(Vertex v, WardrobePlayerView view) => view switch
    {
        WardrobePlayerView.Isometric => new(.866025403784 * (v.X - v.Z), .5 * (v.X + v.Z) - v.Y),
        WardrobePlayerView.Back => new(-v.X, -v.Y),
        WardrobePlayerView.Left => new(-v.Z, -v.Y),
        WardrobePlayerView.Right => new(v.Z, -v.Y),
        WardrobePlayerView.Top => new(v.X, v.Z),
        WardrobePlayerView.Bottom => new(v.X, -v.Z),
        _ => new(v.X, -v.Y),
    };
    private static Vertex Camera(WardrobePlayerView view) => view switch
    {
        WardrobePlayerView.Isometric => new(1, 1, 1),
        WardrobePlayerView.Back => new(0, 0, -1),
        WardrobePlayerView.Left => new(1, 0, 0),
        WardrobePlayerView.Right => new(-1, 0, 0),
        WardrobePlayerView.Top => new(0, 1, 0),
        WardrobePlayerView.Bottom => new(0, -1, 0),
        _ => new(0, 0, 1),
    };
    private static double Dot(Vertex a, Vertex b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static XsrUiImageShade Shade(Vertex normal) => normal.Y > .5 ? XsrUiImageShade.Highlight
        : normal.Y < -.5 ? XsrUiImageShade.Bottom : normal.Z < -.5 ? XsrUiImageShade.Back
        : Math.Abs(normal.X) > .5 ? XsrUiImageShade.Side : XsrUiImageShade.None;

    private readonly record struct Vertex(double X, double Y, double Z)
    {
        public static Vertex operator +(Vertex a, Vertex b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vertex operator -(Vertex a, Vertex b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }
    private readonly record struct Box(double X0, double Y0, double Z0, double X1, double Y1, double Z1)
    {
        public Box Expanded(double amount) => new(X0 - amount, Y0 - amount, Z0 - amount, X1 + amount, Y1 + amount, Z1 + amount);
    }
    private readonly record struct Uv(XsrUiRect Region, bool Flip = false)
    {
        public Uv(double x, double y, double width, double height) : this(new(x, y, width, height)) { }
    }
    private readonly record struct FaceUvs(Uv NegativeX, Uv PositiveX, Uv Top, Uv Bottom, Uv Front, Uv Back)
    {
        public FaceUvs Mirror() => new(PositiveX with { Flip = true }, NegativeX with { Flip = true },
            Top with { Flip = true }, Bottom with { Flip = true }, Front with { Flip = true }, Back with { Flip = true });
    }
    private readonly record struct Face(Vertex Origin, Vertex Across, Vertex Down, Vertex Normal, Uv Texture, PngImage Image, bool Overlay)
    {
        public Vertex Center => new((Across.X + Down.X) / 2, (Across.Y + Down.Y) / 2, (Across.Z + Down.Z) / 2);
    }
}
