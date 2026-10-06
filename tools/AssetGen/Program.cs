using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardManager.Common;
using ClipboardManager.Core.Capture;
using ClipboardManager.Core.History;
using ClipboardManager.Core.Settings;
using ClipboardManager.Interop;
using ClipboardManager.Localization;
using ClipboardManager.Popup;
using ClipboardManager.Shell;
using QRCoder;
using ZXing;
using ZXing.Common;

namespace AssetGen;

/// <summary>
/// dotnet run --project tools/AssetGen -- [icon|shots|qr|verify-qr &lt;png&gt;|all]
/// Everything is rendered offscreen from the real app code; screenshots use only neutral demo data
/// (RFC 2606/5737 names and addresses), never real clipboard content.
/// </summary>
internal static class Program
{
    public const string SolanaAddress = "71ZN1AtBvFASLmbnh7WkfhzYYBC35h9tAm7Awvvk9FPt";

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "all";
        var root = FindRepositoryRoot();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var exit = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (command is "icon" or "all")
                {
                    Icons.Write(root);
                }

                if (command is "qr" or "all")
                {
                    Qr.Write(root, SolanaAddress);
                }

                if (command is "hero" or "all")
                {
                    Hero.Write(root);
                }

                if (command == "bench")
                {
                    Bench.Run();
                }

                if (command == "verify-qr")
                {
                    var decoded = Qr.Decode(args[1]);
                    Console.WriteLine("decoded: " + decoded);
                    exit = decoded == SolanaAddress ? 0 : 2;
                }

                if (command is "shots" or "all")
                {
                    await Screenshots.WriteAsync(app, root);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exit = 1;
            }

            app.Shutdown(exit);
        };
        app.Run();
        return exit;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClipboardManager.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }
}

/// <summary>Search latency on typical and worst-case data (numbers for docs/performance.md).</summary>
internal static class Bench
{
    public static void Run()
    {
        Measure("5,000 typical entries (~60 chars)", 5000, i => $"entry {i} git commit -m 'fix {i}' https://example.com/{i}");
        Measure("5,000 worst-case entries (4,096 chars each)", 5000, i => new string((char)('a' + (i % 26)), 4000) + $" tail {i}");
    }

    private static void Measure(string label, int count, Func<int, string> make)
    {
        var entries = Enumerable.Range(0, count).Select(i =>
        {
            var text = make(i);
            return new HistoryEntry(i, text, text.Length, 1, i, i, null);
        }).ToList();
        var snapshot = new HistorySnapshot([], entries);
        string[] queries = ["d", "do", "doc", "dock", "docker", "example 4999", "zz not found"];
        for (var warm = 0; warm < 3; warm++)
        {
            foreach (var q in queries)
            {
                ClipboardManager.Core.Search.HistorySearch.Filter(snapshot, q);
            }
        }

        var samples = new List<double>();
        for (var round = 0; round < 30; round++)
        {
            foreach (var q in queries)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                ClipboardManager.Core.Search.HistorySearch.Filter(snapshot, q);
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        samples.Sort();
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{label}: median {samples[samples.Count / 2]:F2} ms, p95 {samples[(int)(samples.Count * 0.95)]:F2} ms"));
    }
}

internal static class Icons
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static void Write(string root)
    {
        var brand = Path.Combine(root, "assets", "brand");
        Directory.CreateDirectory(brand);
        var color = BrandIcon.CreateColor();

        var images = Sizes.Select(size => (size, png: BrandIcon.RenderPng(color, size))).ToList();
        using (var stream = File.Create(Path.Combine(brand, "app.ico")))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)images.Count);
            var offset = 6 + (16 * images.Count);
            foreach (var (size, png) in images)
            {
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(png.Length);
                writer.Write(offset);
                offset += png.Length;
            }

            foreach (var (_, png) in images)
            {
                writer.Write(png);
            }
        }

        File.WriteAllBytes(Path.Combine(brand, "logo-512.png"), BrandIcon.RenderPng(color, 512));
        File.WriteAllBytes(Path.Combine(brand, "tray-dark-taskbar.png"), BrandIcon.RenderPng(BrandIcon.CreateGlyph(Colors.White, false), 64));
        File.WriteAllBytes(Path.Combine(brand, "tray-light-taskbar.png"), BrandIcon.RenderPng(BrandIcon.CreateGlyph(Color.FromRgb(0x1A, 0x1A, 0x1A), false), 64));
        Console.WriteLine("icon: assets/brand/app.ico, logo-512.png, tray-*.png");
    }
}

internal static class Qr
{
    public static void Write(string root, string address)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix.Select(row => row.Cast<bool>().ToArray()).ToArray();

        // Verify before writing: render the exact module matrix and decode it again.
        var decoded = DecodeMatrix(matrix);
        if (decoded != address)
        {
            throw new InvalidOperationException("Generated QR code does not decode to the address");
        }

        var path = ModulePath(matrix);
        var size = matrix.Length;
        var readme = Path.Combine(root, "assets", "readme");
        Directory.CreateDirectory(readme);
        File.WriteAllText(Path.Combine(readme, "solana-qr.svg"), $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size * 8}" height="{size * 8}" shape-rendering="crispEdges" role="img" aria-label="Solana address QR code">
              <rect width="{size}" height="{size}" fill="#ffffff"/>
              <path d="{path}" fill="#030806"/>
            </svg>
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(readme, "solana-qr.path.txt"), $"{size}\n{path}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(readme, "donate-card.svg"), DonateCard(size, path, address), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(readme, "donate-card-neon.svg"), NeonCard(size, path, address), new UTF8Encoding(false));
        Console.WriteLine($"qr: {size}x{size} modules, decodes to the address; donate cards written");
    }

    private static string Scale(int modules, double pixels) => (pixels / modules).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>README support card: dark, Solana gradient accent, scannable QR (dark modules on white).</summary>
    private static string DonateCard(int size, string path, string address)
    {
        var first = address[..(address.Length / 2 + 6)];
        var second = address[(address.Length / 2 + 6)..];
        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 880 300" width="880" height="300" role="img" aria-label="Support Clipboard Manager with Solana. Address {{address}}">
              <defs>
                <linearGradient id="sol" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#9945FF"/>
                  <stop offset="1" stop-color="#14F195"/>
                </linearGradient>
                <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#0B1220"/>
                  <stop offset="1" stop-color="#0A1A14"/>
                </linearGradient>
                <style>
                  .t { font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Helvetica, Arial, sans-serif; }
                  .m { font-family: 'Cascadia Mono', Consolas, 'SFMono-Regular', Menlo, monospace; }
                  @keyframes glow { 0%, 100% { opacity: .55; } 50% { opacity: 1; } }
                  .pulse { animation: glow 3.2s ease-in-out infinite; }
                </style>
              </defs>
              <rect x="1" y="1" width="878" height="298" rx="22" fill="url(#bg)" stroke="#1F2A37" stroke-width="2"/>
              <rect x="1" y="1" width="8" height="298" rx="4" fill="url(#sol)"/>
              <rect class="pulse" x="36" y="36" width="228" height="228" rx="20" fill="none" stroke="url(#sol)" stroke-width="3"/>
              <rect x="42" y="42" width="216" height="216" rx="16" fill="#FFFFFF"/>
              <g transform="translate(50 50) scale({{Scale(size, 200)}})" shape-rendering="crispEdges">
                <path d="{{path}}" fill="#030806"/>
              </g>
              <text class="t" x="300" y="86" font-size="30" font-weight="700" fill="#F3F6F9">Support Clipboard Manager</text>
              <text class="t" x="300" y="120" font-size="15" fill="#9DA7B3">Free and open source. If it saves you time, a tip in SOL keeps it going.</text>
              <text class="t" x="300" y="164" font-size="13" font-weight="700" letter-spacing="2" fill="url(#sol)">SOLANA · SOL</text>
              <text class="m" x="300" y="196" font-size="19" fill="#E6EDF3">{{first}}</text>
              <text class="m" x="300" y="224" font-size="19" fill="#E6EDF3">{{second}}</text>
              <text class="t" x="300" y="262" font-size="13" fill="#6E7781">Scan with any Solana wallet (Phantom, Solflare, …)</text>
            </svg>
            """;
    }

    /// <summary>Profile card in the neon terminal style of the GitHub profile.</summary>
    private static string NeonCard(int size, string path, string address) => $$"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 900 290" width="900" height="290" role="img" aria-label="Fuel the lab: Solana address {{address}}">
          <defs>
            <style>
              .m { font-family: 'Cascadia Mono', 'JetBrains Mono', Consolas, 'SFMono-Regular', Menlo, monospace; }
              @keyframes blink { 0%, 49% { opacity: 1; } 50%, 100% { opacity: 0; } }
              @keyframes scan { 0% { transform: translateY(-40px); } 100% { transform: translateY(300px); } }
              @keyframes glow { 0%, 100% { stroke-opacity: .45; } 50% { stroke-opacity: 1; } }
              .cursor { animation: blink 1.1s steps(1) infinite; }
              .scan { animation: scan 4.5s linear infinite; }
              .frame { animation: glow 2.8s ease-in-out infinite; }
            </style>
            <linearGradient id="beam" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0" stop-color="#4dffa6" stop-opacity="0"/>
              <stop offset=".5" stop-color="#4dffa6" stop-opacity=".12"/>
              <stop offset="1" stop-color="#4dffa6" stop-opacity="0"/>
            </linearGradient>
            <clipPath id="card"><rect x="2" y="2" width="896" height="286" rx="14"/></clipPath>
          </defs>
          <rect x="2" y="2" width="896" height="286" rx="14" fill="#030806" stroke="#16d67a" stroke-opacity=".55" stroke-width="2"/>
          <g clip-path="url(#card)"><rect class="scan" x="0" y="0" width="900" height="40" fill="url(#beam)"/></g>
          <rect class="frame" x="34" y="34" width="222" height="222" rx="10" fill="none" stroke="#4dffa6" stroke-width="2"/>
          <rect x="40" y="40" width="210" height="210" rx="6" fill="#FFFFFF"/>
          <g transform="translate(47 47) scale({{Scale(size, 196)}})" shape-rendering="crispEdges">
            <path d="{{path}}" fill="#030806"/>
          </g>
          <text class="m" x="290" y="72" font-size="15" fill="#16d67a">$ cat ./fuel.sol</text>
          <text class="m" x="290" y="112" font-size="24" font-weight="700" fill="#4dffa6">fuel the lab</text>
          <text class="m" x="290" y="144" font-size="14" fill="#7fb59a">tips in SOL keep the side projects (and the coffee) running</text>
          <text class="m" x="290" y="190" font-size="13" fill="#22d3ee">network: solana · asset: SOL</text>
          <text class="m" x="290" y="222" font-size="16" fill="#e6fff2">{{address}}</text>
          <text class="m" x="290" y="256" font-size="14" fill="#16d67a">$ <tspan class="cursor">▌</tspan></text>
        </svg>
        """;

    public static string? Decode(string pngPath)
    {
        var frame = BitmapDecoder.Create(new Uri(Path.GetFullPath(pngPath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        var pixels = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(pixels, gray.PixelWidth, 0);
        return DecodeGray(pixels, gray.PixelWidth, gray.PixelHeight);
    }

    private static string? DecodeMatrix(bool[][] matrix)
    {
        const int scale = 8;
        var width = matrix.Length * scale;
        var pixels = new byte[width * width];
        for (var y = 0; y < width; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = matrix[y / scale][x / scale] ? (byte)0 : (byte)255;
            }
        }

        return DecodeGray(pixels, width, width);
    }

    private static string? DecodeGray(byte[] pixels, int width, int height)
    {
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new MultiFormatReader();
        var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true, [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE } };
        return reader.decode(new BinaryBitmap(new HybridBinarizer(source)), hints)?.Text;
    }

    private static string ModulePath(bool[][] matrix)
    {
        var builder = new StringBuilder();
        for (var y = 0; y < matrix.Length; y++)
        {
            var x = 0;
            while (x < matrix[y].Length)
            {
                if (!matrix[y][x])
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < matrix[y].Length && matrix[y][x])
                {
                    x++;
                }

                builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"M{start} {y}h{x - start}v1h-{x - start}z");
            }
        }

        return builder.ToString();
    }
}

internal static class Screenshots
{
    private const double Scale = 2;

    public static async Task WriteAsync(Application app, string root)
    {
        Strings.Apply(LanguagePreference.English);
        var output = Path.Combine(root, "assets", "readme");
        Directory.CreateDirectory(output);

        foreach (var (theme, name) in new[] { (ThemePreference.Dark, "dark"), (ThemePreference.Light, "light") })
        {
            ThemeHelper.Apply(app, theme);
            BrandAccent(app, theme == ThemePreference.Dark);
            await RenderPopupAsync(Path.Combine(output, $"popup-{name}.png"), query: null);
            await RenderPopupAsync(Path.Combine(output, $"search-{name}.png"), query: "docker");
        }

        ThemeHelper.Apply(app, ThemePreference.Dark);
        BrandAccent(app, dark: true);
        await RenderDemoAsync(Path.Combine(output, "demo.gif"));
        Console.WriteLine("shots: assets/readme/popup-*.png, search-*.png, demo.gif");
    }

    /// <summary>The app follows the Windows accent colour; README images use the brand green so they look the same for everyone.</summary>
    private static void BrandAccent(Application app, bool dark)
    {
        (string Key, string Hex)[] palette = dark
            ?
            [
                ("ListBoxItemSelectedBackgroundThemeBrush", "#145C40"),
                ("ListBoxItemSelectedBackgroundPointerOverThemeBrush", "#18694A"),
                ("ListBoxItemSelectedBackgroundPressedThemeBrush", "#124F38"),
                ("AccentFillColorDefaultBrush", "#4DE3A0"),
                ("AccentTextFillColorPrimaryBrush", "#6EE7B7"),
                ("SystemAccentColor", "#4DE3A0"),
            ]
            :
            [
                ("ListBoxItemSelectedBackgroundThemeBrush", "#C9F2DE"),
                ("ListBoxItemSelectedBackgroundPointerOverThemeBrush", "#BDEBD4"),
                ("ListBoxItemSelectedBackgroundPressedThemeBrush", "#B0E4CA"),
                ("AccentFillColorDefaultBrush", "#078A5A"),
                ("AccentTextFillColorPrimaryBrush", "#047857"),
                ("SystemAccentColor", "#078A5A"),
            ];
        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            app.Resources[key] = key.EndsWith("Brush", StringComparison.Ordinal) ? new SolidColorBrush(color) : color;
        }
    }

    private static PopupViewModel DemoModel(out HistorySnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long id = 1;
        HistoryEntry Entry(string text, double minutesAgo, double? pinnedDaysAgo = null) =>
            new(id++, text, text.Length, TextMetrics.CountLines(text), now - (long)(minutesAgo * 60_000), now - (long)(minutesAgo * 60_000),
                pinnedDaysAgo is { } d ? now - (long)(d * 86_400_000) : null);

        var pinned = new List<HistoryEntry>
        {
            Entry("ssh admin@203.0.113.10", 600, 1),
            Entry("https://example.com/docs/getting-started", 900, 3),
            Entry("Best regards,\nAlex", 4000, 7),
        };
        var history = new List<HistoryEntry>
        {
            Entry("docker compose up -d", 0.2),
            Entry("git commit -m \"Add search highlighting\"", 2),
            Entry("https://example.org/blog/2026/keyboard-first-tools", 9),
            Entry("{\n  \"name\": \"demo-app\",\n  \"version\": \"1.0.0\",\n  \"private\": true\n}", 14),
            Entry("C:\\Projects\\demo\\appsettings.json", 25),
            Entry("SELECT id, name FROM users WHERE active = 1;", 48),
            Entry("docker logs -f --tail 100 web", 75),
            Entry("192.0.2.44", 130),
            Entry("npm run build -- --watch", 300),
            Entry("Ship v1.0 on Friday 🚀", 1500),
        };
        snapshot = new HistorySnapshot(pinned, history);
        var model = new PopupViewModel();
        model.Load(snapshot, currentEntryId: history[0].Id, SkipReason.None, isLoaded: true, pinnedExpanded: false);
        model.SetStatus(Strings.StatusActive, paused: false, pausedUntil: null);
        return model;
    }

    private static async Task RenderPopupAsync(string file, string? query)
    {
        var model = DemoModel(out _);
        if (query is not null)
        {
            model.Query = query;
        }

        var window = new PopupWindow(model);
        await ShowCloakedAsync(window);
        if (query is not null)
        {
            window.Search.CaretIndex = query.Length;
        }

        await Idle();
        File.WriteAllBytes(file, Encode(new PngBitmapEncoder(), Compose(window)));
        Close(window);
    }

    private static async Task RenderDemoAsync(string file)
    {
        var model = DemoModel(out _);
        var window = new PopupWindow(model);
        await ShowCloakedAsync(window);
        var frames = new List<(BitmapSource Frame, int DelayCs)> { (Compose(window, 1), 120) };
        foreach (var step in new[] { "d", "do", "doc", "dock", "docke", "docker" })
        {
            model.Query = step;
            window.Search.CaretIndex = step.Length;
            await Idle();
            frames.Add((Compose(window, 1), step == "docker" ? 90 : 22));
        }

        model.MoveSelection(1);
        await Idle();
        frames.Add((Compose(window, 1), 160));
        model.Query = string.Empty;
        await Idle();
        frames.Add((Compose(window, 1), 60));
        Close(window);

        GifWriter.Write(file, frames);
    }

    private static async Task ShowCloakedAsync(Window window)
    {
        window.ShowActivated = false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Dwm.Set(hwnd, Dwm.DWMWA_CLOAK, 1);
        var rendered = new TaskCompletionSource();
        window.ContentRendered += (_, _) => rendered.TrySetResult();
        window.Show();
        await rendered.Task;
        await Idle();
    }

    private static void Close(PopupWindow window)
    {
        window.AllowCloseForShutdown();
        window.Close();
    }

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    /// <summary>Window content with rounded corners and a soft shadow on a transparent canvas.</summary>
    private static RenderTargetBitmap Compose(Window window, double scale = Scale)
    {
        var content = (FrameworkElement)window.Content;
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        const double margin = 36;
        const double radius = 10;

        var snapshot = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }

        snapshot.Render(background);

        var composed = new DrawingVisual();
        using (var dc = composed.RenderOpen())
        {
            var shape = new RectangleGeometry(new Rect(margin, margin, width, height), radius, radius);
            dc.PushClip(shape);
            dc.DrawImage(snapshot, new Rect(margin, margin, width, height));
            dc.Pop();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1), shape);
        }

        var shadow = new System.Windows.Controls.Border
        {
            Width = width + (2 * margin),
            Height = height + (2 * margin),
            Child = new System.Windows.Controls.Border
            {
                Margin = new Thickness(margin),
                CornerRadius = new CornerRadius(radius),
                Background = Brushes.Black,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.35, Direction = 270 },
            },
        };
        shadow.Measure(new Size(shadow.Width, shadow.Height));
        shadow.Arrange(new Rect(0, 0, shadow.Width, shadow.Height));

        var result = new RenderTargetBitmap((int)((width + (2 * margin)) * scale), (int)((height + (2 * margin)) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        result.Render(shadow);
        result.Render(composed);
        result.Freeze();
        return result;
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>Animated GIF from WPF frames (WPF's encoder lacks delay/loop metadata, so it is patched in).</summary>
internal static class GifWriter
{
    public static void Write(string file, List<(BitmapSource Frame, int DelayCs)> frames)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var (frame, _) in frames)
        {
            // Flatten onto a solid background; GIF has no alpha gradients.
            var flat = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0D, 0x11, 0x17)), null, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
                dc.DrawImage(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
            }

            flat.Render(visual);
            encoder.Frames.Add(BitmapFrame.Create(flat));
        }

        using var memory = new MemoryStream();
        encoder.Save(memory);
        var bytes = memory.ToArray();
        File.WriteAllBytes(file, Patch(bytes, frames.Select(f => f.DelayCs).ToArray()));
    }

    /// <summary>Inserts a NETSCAPE2.0 loop block and sets per-frame delays in the graphic control extensions.</summary>
    private static byte[] Patch(byte[] gif, int[] delays)
    {
        var output = new List<byte>(gif.Length + 64);
        var header = 13;
        var packed = gif[10];
        if ((packed & 0x80) != 0)
        {
            header += 3 * (1 << ((packed & 0x07) + 1));
        }

        output.AddRange(gif.AsSpan(0, header).ToArray());
        output.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 0x03, 0x01, 0x00, 0x00, 0x00]);

        // Walk the block structure (never scan raw bytes: LZW data can contain any byte sequence).
        var frame = 0;
        var lastWasControl = false;
        var i = header;
        while (i < gif.Length)
        {
            switch (gif[i])
            {
                case 0x21:
                    {
                        var label = gif[i + 1];
                        var start = i;
                        i += 2;
                        i = SkipSubBlocks(gif, i);
                        if (label == 0xF9)
                        {
                            var block = gif[start..i];
                            SetDelay(block, Delay(delays, frame));
                            output.AddRange(block);
                            lastWasControl = true;
                        }
                        else if (label != 0xFF)
                        {
                            output.AddRange(gif[start..i]);
                        }

                        break;
                    }

                case 0x2C:
                    {
                        if (!lastWasControl)
                        {
                            var control = new byte[] { 0x21, 0xF9, 0x04, 0x00, 0, 0, 0x00, 0x00 };
                            SetDelay(control, Delay(delays, frame));
                            output.AddRange(control);
                        }

                        var start = i;
                        var localTable = gif[i + 9];
                        i += 10;
                        if ((localTable & 0x80) != 0)
                        {
                            i += 3 * (1 << ((localTable & 0x07) + 1));
                        }

                        i++; // LZW minimum code size
                        i = SkipSubBlocks(gif, i);
                        output.AddRange(gif[start..i]);
                        frame++;
                        lastWasControl = false;
                        break;
                    }

                case 0x3B:
                    output.Add(0x3B);
                    return [.. output];
                default:
                    i++;
                    break;
            }
        }

        output.Add(0x3B);
        return [.. output];

        static int SkipSubBlocks(byte[] data, int position)
        {
            while (data[position] != 0)
            {
                position += data[position] + 1;
            }

            return position + 1;
        }

        static int Delay(int[] all, int index) => index < all.Length ? all[index] : 10;

        static void SetDelay(byte[] control, int delay)
        {
            control[4] = (byte)(delay & 0xFF);
            control[5] = (byte)(delay >> 8);
        }
    }
}
