using System.Text;

namespace AssetGen;

/// <summary>
/// Animated README banner (pure SVG + CSS keyframes: GitHub renders SVG as an image, so no script
/// and no web fonts). One template, two palettes, switched with &lt;picture&gt; in the README.
/// The 8-second loop: keys press → popup opens → "docker" is typed → list filters → entry copied.
/// </summary>
internal static class Hero
{
    private sealed record Palette(
        string Name,
        string Background1,
        string Background2,
        string Grid,
        string Title,
        string Text,
        string Muted,
        string KeyFill,
        string KeyStroke,
        string KeyText,
        string Card,
        string CardStroke,
        string Row,
        string Selected,
        string Accent,
        string AccentText,
        string Shadow);

    private static readonly Palette Dark = new(
        "dark", "#040A08", "#082019", "#16d67a", "#F2FFF8", "#C9D8D1", "#7F9A8E",
        "#0E1A16", "#1E3A2F", "#E6FFF2", "#0D1513", "#1F302A", "#C9D8D1", "#14523A", "#4DE3A0", "#062018", "#000000");

    private static readonly Palette Light = new(
        "light", "#F4FBF7", "#E3F4EB", "#0B8F57", "#0B1F17", "#2E4A3E", "#5F7A6D",
        "#FFFFFF", "#C8DDD2", "#0B1F17", "#FFFFFF", "#D5E6DD", "#2E4A3E", "#C9F2DE", "#078A5A", "#FFFFFF", "#0B3D2A");

    public static void Write(string root)
    {
        var output = Path.Combine(root, "assets", "readme");
        Directory.CreateDirectory(output);
        foreach (var palette in new[] { Dark, Light })
        {
            File.WriteAllText(Path.Combine(output, $"hero-{palette.Name}.svg"), Render(palette), new UTF8Encoding(false));
        }

        Console.WriteLine("hero: assets/readme/hero-dark.svg, hero-light.svg");
    }

    private static string Render(Palette p) => $$"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 440" width="1200" height="440" role="img" aria-label="Clipboard Manager: press Ctrl+Shift+V, type, press Enter.">
          <defs>
            <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="{{p.Background1}}"/>
              <stop offset="1" stop-color="{{p.Background2}}"/>
            </linearGradient>
            <radialGradient id="glow" cx="0.18" cy="0.42" r="0.55">
              <stop offset="0" stop-color="#16d67a" stop-opacity="0.22"/>
              <stop offset="1" stop-color="#16d67a" stop-opacity="0"/>
            </radialGradient>
            <linearGradient id="brand" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#16D67A"/>
              <stop offset="1" stop-color="#0EA5A4"/>
            </linearGradient>
            <pattern id="grid" width="40" height="40" patternUnits="userSpaceOnUse">
              <path d="M40 0H0V40" fill="none" stroke="{{p.Grid}}" stroke-opacity="0.07" stroke-width="1"/>
            </pattern>
            <filter id="shadow" x="-20%" y="-20%" width="140%" height="160%">
              <feDropShadow dx="0" dy="18" stdDeviation="22" flood-color="{{p.Shadow}}" flood-opacity="0.35"/>
            </filter>
            <clipPath id="typing"><rect class="typing" x="0" y="-30" width="0" height="40"/></clipPath>
            <clipPath id="card"><rect x="0" y="0" width="500" height="330" rx="16"/></clipPath>
            <style>
              .t { font-family: 'Segoe UI', 'Segoe UI Variable', -apple-system, BlinkMacSystemFont, 'Helvetica Neue', Arial, sans-serif; }
              .m { font-family: 'Cascadia Mono', Consolas, 'SFMono-Regular', Menlo, monospace; }
              @keyframes key { 0%, 4% { transform: translateY(0); } 6%, 14% { transform: translateY(4px); } 16%, 100% { transform: translateY(0); } }
              @keyframes keyglow { 0%, 4% { fill: {{p.KeyFill}}; } 6%, 14% { fill: {{p.Accent}}; } 16%, 100% { fill: {{p.KeyFill}}; } }
              @keyframes keytext { 0%, 4% { fill: {{p.KeyText}}; } 6%, 14% { fill: {{p.AccentText}}; } 16%, 100% { fill: {{p.KeyText}}; } }
              @keyframes popup { 0%, 10% { opacity: 0; transform: translateY(14px) scale(.985); } 13%, 92% { opacity: 1; transform: translateY(0) scale(1); } 97%, 100% { opacity: 0; transform: translateY(8px) scale(.99); } }
              @keyframes type { 0%, 22% { width: 0; } 24% { width: 13px; } 26% { width: 27px; } 28% { width: 41px; } 30% { width: 55px; } 32% { width: 69px; } 34%, 100% { width: 84px; } }
              @keyframes caret { 0%, 49% { opacity: 1; } 50%, 100% { opacity: 0; } }
              @keyframes caretmove { 0%, 22% { transform: translateX(0); } 24% { transform: translateX(13px); } 26% { transform: translateX(27px); } 28% { transform: translateX(41px); } 30% { transform: translateX(55px); } 32% { transform: translateX(69px); } 34%, 100% { transform: translateX(86px); } }
              .caretpos { animation: caretmove 8s infinite steps(1); }
              @keyframes placeholder { 0%, 21% { opacity: 1; } 22%, 100% { opacity: 0; } }
              @keyframes all { 0%, 35% { opacity: 1; } 38%, 100% { opacity: 0; } }
              @keyframes filtered { 0%, 35% { opacity: 0; } 38%, 100% { opacity: 1; } }
              @keyframes select { 0%, 48% { transform: translateY(0); } 52%, 100% { transform: translateY(52px); } }
              @keyframes copied { 0%, 62% { opacity: 0; transform: translateY(6px); } 65%, 88% { opacity: 1; transform: translateY(0); } 92%, 100% { opacity: 0; } }
              .k1 { animation: key 8s infinite; animation-delay: .1s; transform-box: fill-box; }
              .k2 { animation: key 8s infinite; animation-delay: .25s; transform-box: fill-box; }
              .k3 { animation: key 8s infinite; animation-delay: .4s; transform-box: fill-box; }
              .k1 rect { animation: keyglow 8s infinite; animation-delay: .1s; }
              .k2 rect { animation: keyglow 8s infinite; animation-delay: .25s; }
              .k3 rect { animation: keyglow 8s infinite; animation-delay: .4s; }
              .k1 text { animation: keytext 8s infinite; animation-delay: .1s; }
              .k2 text { animation: keytext 8s infinite; animation-delay: .25s; }
              .k3 text { animation: keytext 8s infinite; animation-delay: .4s; }
              .popup { animation: popup 8s infinite cubic-bezier(.2,.8,.2,1); transform-box: fill-box; transform-origin: center; }
              .typing { animation: type 8s infinite steps(1); }
              .caret { animation: caret 1s infinite steps(1); }
              .placeholder { animation: placeholder 8s infinite; }
              .all { animation: all 8s infinite; }
              .filtered { animation: filtered 8s infinite; }
              .select { animation: select 8s infinite cubic-bezier(.3,.7,.2,1); }
              .copied { animation: copied 8s infinite; }
            </style>
          </defs>

          <rect width="1200" height="440" fill="url(#bg)"/>
          <rect width="1200" height="440" fill="url(#grid)"/>
          <rect width="1200" height="440" fill="url(#glow)"/>

          <!-- logo -->
          <g transform="translate(72 70) scale(0.36)">
            <rect x="30" y="42" width="196" height="198" rx="40" fill="url(#brand)"/>
            <rect x="86" y="16" width="84" height="46" rx="16" fill="#061A14"/>
            <rect x="104" y="28" width="48" height="12" rx="6" fill="#16D67A"/>
            <rect x="62" y="102" width="132" height="24" rx="12" fill="#FFFFFF"/>
            <rect x="62" y="146" width="104" height="24" rx="12" fill="#FFFFFF" fill-opacity=".82"/>
            <rect x="62" y="190" width="72" height="24" rx="12" fill="#FFFFFF" fill-opacity=".62"/>
          </g>

          <text class="t" x="72" y="214" font-size="52" font-weight="700" fill="{{p.Title}}">Clipboard Manager</text>
          <text class="t" x="74" y="256" font-size="21" fill="{{p.Text}}">Everything you copied – one shortcut away.</text>

          <!-- keys -->
          <g class="k1"><rect x="72" y="292" width="86" height="48" rx="10" fill="{{p.KeyFill}}" stroke="{{p.KeyStroke}}" stroke-width="1.5"/><text class="t" x="115" y="323" font-size="18" font-weight="600" text-anchor="middle" fill="{{p.KeyText}}">Ctrl</text></g>
          <text class="t" x="170" y="324" font-size="20" fill="{{p.Muted}}">+</text>
          <g class="k2"><rect x="188" y="292" width="96" height="48" rx="10" fill="{{p.KeyFill}}" stroke="{{p.KeyStroke}}" stroke-width="1.5"/><text class="t" x="236" y="323" font-size="18" font-weight="600" text-anchor="middle" fill="{{p.KeyText}}">Shift</text></g>
          <text class="t" x="296" y="324" font-size="20" fill="{{p.Muted}}">+</text>
          <g class="k3"><rect x="314" y="292" width="56" height="48" rx="10" fill="{{p.KeyFill}}" stroke="{{p.KeyStroke}}" stroke-width="1.5"/><text class="t" x="342" y="323" font-size="18" font-weight="600" text-anchor="middle" fill="{{p.KeyText}}">V</text></g>

          <text class="t" x="74" y="388" font-size="15" fill="{{p.Muted}}">local only  ·  no telemetry  ·  instant search  ·  open source</text>

          <!-- popup -->
          <g transform="translate(640 56)">
            <g class="popup">
              <rect x="0" y="0" width="500" height="330" rx="16" fill="{{p.Card}}" stroke="{{p.CardStroke}}" stroke-width="1.5" filter="url(#shadow)"/>
              <g clip-path="url(#card)">
                <circle cx="34" cy="38" r="8" fill="none" stroke="{{p.Muted}}" stroke-width="2"/>
                <path d="M40 44l6 6" stroke="{{p.Muted}}" stroke-width="2" stroke-linecap="round"/>
                <text class="t placeholder" x="58" y="45" font-size="17" fill="{{p.Muted}}">Search history…</text>
                <g transform="translate(58 45)">
                  <g clip-path="url(#typing)"><text class="t" x="0" y="0" font-size="17" fill="{{p.Title}}">docker</text></g>
                </g>
                <g class="caretpos"><rect class="caret" x="59" y="29" width="2" height="21" fill="{{p.Accent}}"/></g>
                <circle cx="400" cy="39" r="4" fill="{{p.Accent}}"/>
                <text class="t" x="410" y="44" font-size="13" fill="{{p.Muted}}">Recording</text>
                <rect x="0" y="68" width="500" height="1" fill="{{p.CardStroke}}"/>

                <!-- all entries -->
                <g class="all">
                  <rect x="12" y="132" width="476" height="44" rx="8" fill="{{p.Selected}}"/>
                  {{Row(p, 104, "git status", "just now", current: true)}}
                  {{Row(p, 156, "git commit -m \"Add search highlighting\"", "2 min")}}
                  {{Row(p, 208, "https://example.org/blog/keyboard-first", "9 min", icon: "link")}}
                  {{Row(p, 260, "docker logs -f --tail 100 web", "1 h")}}
                  {{Row(p, 312, "docker compose up -d", "3 h")}}
                </g>

                <!-- filtered for "docker" -->
                <g class="filtered">
                  <g class="select"><rect x="12" y="80" width="476" height="44" rx="8" fill="{{p.Selected}}"/></g>
                  {{Row(p, 104, "docker logs -f --tail 100 web", "1 h", bold: "docker")}}
                  {{Row(p, 156, "docker compose up -d", "3 h", bold: "docker")}}
                </g>

                <g class="copied">
                  <rect x="150" y="262" width="200" height="40" rx="20" fill="{{p.Accent}}"/>
                  <text class="t" x="250" y="288" font-size="15" font-weight="600" text-anchor="middle" fill="{{p.AccentText}}">✓ Copied – press Ctrl+V</text>
                </g>
              </g>
            </g>
          </g>
        </svg>
        """;

    private static string Row(Palette p, int baseline, string text, string time, bool current = false, string? bold = null, string icon = "doc")
    {
        var glyph = icon == "link"
            ? $"""<path d="M30 {baseline - 10}h6a5 5 0 0 1 0 10h-6M44 {baseline - 10}h-6a5 5 0 0 0 0 10h6" fill="none" stroke="{p.Muted}" stroke-width="1.8"/>"""
            : $"""<path d="M29 {baseline - 16}h9l5 5v13h-14z" fill="none" stroke="{p.Muted}" stroke-width="1.6" stroke-linejoin="round"/>""";
        var escaped = System.Security.SecurityElement.Escape(text);
        var content = bold is not null && text.StartsWith(bold, StringComparison.Ordinal)
            ? $"""<tspan font-weight="700" text-decoration="underline">{System.Security.SecurityElement.Escape(bold)}</tspan>{System.Security.SecurityElement.Escape(text[bold.Length..])}"""
            : escaped;
        var badge = current
            ? $"""<rect x="344" y="{baseline - 15}" width="70" height="20" rx="5" fill="{p.Accent}"/><text class="t" x="379" y="{baseline}" font-size="11" font-weight="700" text-anchor="middle" fill="{p.AccentText}">CURRENT</text>"""
            : string.Empty;
        return $"""
            {glyph}
                      <text class="t" x="58" y="{baseline}" font-size="16" fill="{p.Row}">{content}</text>
                      {badge}<text class="t" x="478" y="{baseline}" font-size="13" text-anchor="end" fill="{p.Muted}">{time}</text>
            """;
    }
}
