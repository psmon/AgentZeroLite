using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shapes = System.Windows.Shapes;

namespace AgentZeroWpf.Services;

/// <summary>
/// Dynamic renderer: parses Pencil .pen (JSON) files at runtime and produces
/// WPF UIElements. Also provides Analyze() for stats without WPF rendering.
/// </summary>
public sealed class PencilRenderer
{
    private readonly Dictionary<string, string> _colorTokens = [];
    private readonly List<PenFrame> _frames = [];
    private PenRenderStats _stats = new();

    public IReadOnlyList<PenFrame> Frames => _frames;
    public IReadOnlyDictionary<string, string> ColorTokens => _colorTokens;
    public PenRenderStats LastStats => _stats;

    public record PenFrame(string Name, int Index, double Width, double Height, JsonElement Element);

    public static PencilRenderer? TryLoad(string penFilePath)
    {
        if (!File.Exists(penFilePath)) return null;
        try
        {
            var json = File.ReadAllText(penFilePath, System.Text.Encoding.UTF8);
            var doc = JsonDocument.Parse(json);
            var renderer = new PencilRenderer();
            renderer.Parse(doc.RootElement);
            return renderer;
        }
        catch { return null; }
    }

    private void Parse(JsonElement root)
    {
        if (root.TryGetProperty("variables", out var vars))
        {
            foreach (var v in vars.EnumerateObject())
            {
                if (v.Value.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
                    _colorTokens[v.Name] = val.GetString() ?? "";
            }
        }

        if (root.TryGetProperty("children", out var children))
        {
            int idx = 0;
            foreach (var child in children.EnumerateArray())
            {
                if (GetStr(child, "type") == "frame")
                {
                    var name = GetStr(child, "name");
                    if (string.IsNullOrEmpty(name)) name = $"Frame {idx + 1}";
                    var w = GetDouble(child, "width", 1920);
                    var h = GetDouble(child, "height", 1080);
                    _frames.Add(new PenFrame(name, idx, w, h, child));
                    idx++;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Analyze — walk all elements, collect stats (no WPF rendering)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Walk the entire .pen tree and count elements by type.
    /// Returns stats without creating any WPF UIElements.
    /// </summary>
    public PenRenderStats Analyze()
    {
        var stats = new PenRenderStats
        {
            FrameCount = _frames.Count,
            ColorTokenCount = _colorTokens.Count,
        };

        foreach (var frame in _frames)
            AnalyzeElement(frame.Element, stats);

        return stats;
    }

    private static void AnalyzeElement(JsonElement el, PenRenderStats stats)
    {
        var type = GetStr(el, "type");
        var name = GetStr(el, "name");

        if (!string.IsNullOrEmpty(type))
        {
            stats.CountType(type);

            // Check renderability
            switch (type)
            {
                case "text":
                    var content = GetStr(el, "content");
                    if (string.IsNullOrEmpty(content))
                        stats.Skip(type, name, "empty content");
                    else
                        stats.Rendered(type, name);
                    break;

                case "rectangle":
                case "ellipse":
                case "icon_font":
                    stats.Rendered(type, name);
                    break;

                case "frame":
                    stats.Rendered(type, name);
                    break;

                default:
                    stats.Skip(type, name, $"unsupported type '{type}'");
                    break;
            }
        }

        // Recurse into children
        if (el.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                AnalyzeElement(child, stats);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Render — produces WPF UIElements + collects stats
    // ══════════════════════════════════════════════════════════════

    public UIElement RenderFrame(PenFrame frame, double maxWidth = 800)
    {
        _stats = new PenRenderStats
        {
            FrameCount = _frames.Count,
            ColorTokenCount = _colorTokens.Count,
        };

        var scale = maxWidth / Math.Max(1, frame.Width);

        var container = new Border
        {
            Width = maxWidth,
            MinHeight = 100,
            ClipToBounds = true,
            Background = ResolveBrush(GetStr(frame.Element, "fill"), "#FAFAFA"),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42)),
            BorderThickness = new Thickness(1),
        };

        if (frame.Height > 0)
            container.Height = frame.Height * scale;

        container.Child = RenderContainer(frame.Element, scale);
        return container;
    }

    // ── Container (decides layout strategy) ──

    private UIElement RenderContainer(JsonElement el, double scale)
    {
        var layout = GetStr(el, "layout");
        var gap = GetDouble(el, "gap", 0) * scale;
        var padding = GetPadding(el, scale);
        var justifyContent = GetStr(el, "justifyContent");
        var alignItems = GetStr(el, "alignItems");

        if (!el.TryGetProperty("children", out var children))
            return new Border();

        var childElements = children.EnumerateArray().ToList();

        if (layout == "vertical")
            return RenderVertical(childElements, gap, padding, justifyContent, alignItems, scale);
        if (layout == "horizontal")
            return RenderHorizontal(childElements, gap, padding, justifyContent, alignItems, scale);

        // No explicit layout but has gap → infer horizontal (Pencil default)
        if (gap > 0)
            return RenderHorizontal(childElements, gap, padding, justifyContent, alignItems, scale);

        return RenderAbsolute(childElements, scale);
    }

    private UIElement RenderVertical(List<JsonElement> children, double gap,
        Thickness padding, string justify, string align, double scale)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var wrapper = new Border { Padding = padding, Child = panel };

        if (justify == "center")
            panel.VerticalAlignment = VerticalAlignment.Center;

        for (int i = 0; i < children.Count; i++)
        {
            var element = RenderElement(children[i], scale);
            if (element is null) continue;

            if (align == "center")
                element.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            if (i > 0 && gap > 0)
                element.SetValue(FrameworkElement.MarginProperty, new Thickness(0, gap, 0, 0));

            panel.Children.Add(element);
        }

        return wrapper;
    }

    private UIElement RenderHorizontal(List<JsonElement> children, double gap,
        Thickness padding, string justify, string align, double scale)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var wrapper = new Border { Padding = padding, Child = panel };

        if (justify == "center")
            panel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;

        for (int i = 0; i < children.Count; i++)
        {
            var element = RenderElement(children[i], scale);
            if (element is null) continue;

            if (align == "center")
                element.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            if (i > 0 && gap > 0)
                element.SetValue(FrameworkElement.MarginProperty, new Thickness(gap, 0, 0, 0));

            panel.Children.Add(element);
        }

        return wrapper;
    }

    private UIElement RenderAbsolute(List<JsonElement> children, double scale)
    {
        var canvas = new Canvas();

        foreach (var child in children)
        {
            var element = RenderElement(child, scale);
            if (element is null) continue;

            Canvas.SetLeft(element, GetDouble(child, "x", 0) * scale);
            Canvas.SetTop(element, GetDouble(child, "y", 0) * scale);
            canvas.Children.Add(element);
        }

        return canvas;
    }

    // ── Element dispatcher ──

    private UIElement? RenderElement(JsonElement el, double scale)
    {
        var type = GetStr(el, "type");
        var name = GetStr(el, "name");

        if (string.IsNullOrEmpty(type)) return null;

        _stats.CountType(type);

        try
        {
            UIElement? result = type switch
            {
                "text" => RenderText(el, scale),
                "rectangle" => RenderRectangle(el, scale),
                "ellipse" => RenderEllipse(el, scale),
                "frame" => RenderNestedFrame(el, scale),
                "icon_font" => RenderIconFont(el, scale),
                _ => null,
            };

            if (result is null)
                _stats.Skip(type, name, type == "text" ? "empty/tiny content" : $"unsupported type '{type}'");
            else
                _stats.Rendered(type, name);

            return result;
        }
        catch (Exception ex)
        {
            _stats.Error(type, name, ex.Message);
            return null;
        }
    }

    // ── Text ──

    private UIElement? RenderText(JsonElement el, double scale)
    {
        var content = GetStr(el, "content");
        if (string.IsNullOrEmpty(content)) return null;

        var fontSize = GetDouble(el, "fontSize", 14) * scale;
        if (fontSize < 4) return null;

        var tb = new TextBlock
        {
            Text = content,
            FontSize = Math.Max(6, fontSize),
            Foreground = ResolveBrush(GetStr(el, "fill"), "#1A1A1A"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = ParseFontWeight(GetStr(el, "fontWeight")),
        };

        var fontFamily = GetStr(el, "fontFamily");
        if (!string.IsNullOrEmpty(fontFamily))
            tb.FontFamily = new FontFamily($"{fontFamily}, Segoe UI, Consolas");

        var (w, wFill) = GetDimension(el, "width", 1.0); // no scale yet
        if (wFill)
            tb.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        else if (w > 0)
            tb.MaxWidth = w * scale;

        return tb;
    }

    // ── Rectangle ──

    private UIElement RenderRectangle(JsonElement el, double scale)
    {
        var (w, wFill) = GetDimension(el, "width", scale);
        var (h, hFill) = GetDimension(el, "height", scale);
        var cornerRadius = GetDouble(el, "cornerRadius", 0) * scale;

        var border = new Border
        {
            Background = ResolveBrush(GetStr(el, "fill"), "Transparent"),
            CornerRadius = new CornerRadius(cornerRadius),
        };

        if (wFill) border.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        else if (w > 0) border.Width = w;
        if (hFill) border.VerticalAlignment = VerticalAlignment.Stretch;
        else if (h > 0) border.Height = h;

        var stroke = GetStr(el, "stroke");
        if (!string.IsNullOrEmpty(stroke))
        {
            border.BorderBrush = ResolveBrush(stroke, "#E5E5E5");
            border.BorderThickness = new Thickness(Math.Max(0.5, GetDouble(el, "strokeWidth", 0) * scale));
        }

        if (el.TryGetProperty("children", out _))
            border.Child = RenderContainer(el, scale);

        return border;
    }

    // ── Ellipse ──

    private UIElement RenderEllipse(JsonElement el, double scale)
    {
        var w = GetDouble(el, "width", 20) * scale;
        var h = GetDouble(el, "height", 20) * scale;

        var ellipse = new Shapes.Ellipse
        {
            Width = w, Height = h,
            Fill = ResolveBrush(GetStr(el, "fill"), "Transparent"),
        };

        var stroke = GetStr(el, "stroke");
        if (!string.IsNullOrEmpty(stroke))
        {
            ellipse.Stroke = ResolveBrush(stroke, "#E5E5E5");
            ellipse.StrokeThickness = Math.Max(0.5, GetDouble(el, "strokeWidth", 1) * scale);
        }

        return ellipse;
    }

    // ── Nested frame ──

    private UIElement RenderNestedFrame(JsonElement el, double scale)
    {
        var cornerRadius = GetDouble(el, "cornerRadius", 0) * scale;
        var (w, wFill) = GetDimension(el, "width", scale);
        var (h, hFill) = GetDimension(el, "height", scale);

        var border = new Border
        {
            Background = ResolveBrush(GetStr(el, "fill"), "Transparent"),
            CornerRadius = new CornerRadius(cornerRadius),
            ClipToBounds = true,
        };

        if (wFill)
            border.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        else if (w > 0)
            border.Width = w;

        if (hFill)
            border.VerticalAlignment = VerticalAlignment.Stretch;
        else if (h > 0)
            border.Height = h;

        var stroke = GetStr(el, "stroke");
        if (!string.IsNullOrEmpty(stroke))
        {
            border.BorderBrush = ResolveBrush(stroke, "#E5E5E5");
            border.BorderThickness = new Thickness(Math.Max(0.5, GetDouble(el, "strokeWidth", 1) * scale));
        }

        border.Child = RenderContainer(el, scale);
        return border;
    }

    // ── Icon Font (Lucide → Segoe MDL2 Assets fallback) ──

    private UIElement? RenderIconFont(JsonElement el, double scale)
    {
        var iconName = GetStr(el, "iconFontName");
        var w = GetDouble(el, "width", 24) * scale;
        var h = GetDouble(el, "height", 24) * scale;
        var fill = GetStr(el, "fill");
        var fontSize = Math.Min(w, h) * 0.7;
        if (fontSize < 4) return null;

        var glyph = MapLucideToMdl2(iconName);

        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = fontSize,
            Foreground = ResolveBrush(fill, "#666666"),
            Width = w,
            Height = h,
            TextAlignment = System.Windows.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        return tb;
    }

    /// <summary>
    /// Best-effort mapping from Lucide icon names to Segoe MDL2 Assets glyphs.
    /// </summary>
    private static string MapLucideToMdl2(string lucideName) => lucideName switch
    {
        "activity" => "\uE9D2",        // Activity
        "arrow-right" => "\uE72A",     // ChevronRight
        "book-open" => "\uE736",       // Library
        "bot" => "\uE99A",             // Robot
        "box" => "\uE7B8",             // Package
        "boxes" => "\uF168",           // Packages
        "brain" => "\uE945",           // Lightbulb (closest)
        "bug" => "\uEBE8",             // Bug
        "check" => "\uE73E",           // CheckMark
        "chevron-down" => "\uE70D",    // ChevronDown
        "chevron-right" => "\uE76C",   // ChevronRight
        "circle" => "\uEA3A",          // RadioBtnOff
        "clipboard-list" => "\uE77F",  // Paste
        "database" => "\uEE94",        // Database
        "file-pen" => "\uE70F",        // Edit
        "file-spreadsheet" => "\uE9F9",// ExcelDocument
        "file-text" => "\uE8A5",       // Document
        "folder" => "\uE8B7",          // Folder
        "folder-open" => "\uE838",     // FolderOpen
        "git-merge" => "\uE8D8",       // BranchFork2
        "globe" => "\uE774",           // Globe
        "headphones" => "\uE7F6",      // Headphone
        "layers" => "\uE81E",          // MapLayers
        "layout-dashboard" => "\uF246",// GridView
        "link" => "\uE71B",            // Link
        "log-in" => "\uE740",          // Forward
        "message-circle" => "\uE8BD",  // Message
        "monitor-play" => "\uE7F4",    // TVMonitor
        "network" => "\uE968",         // NetworkTower
        "notebook" => "\uE70B",        // NotepadSolid
        "palette" => "\uE790",         // Color
        "play" => "\uE768",            // Play
        "refresh-cw" => "\uE72C",      // Refresh
        "rocket" => "\uE7C8",          // Send (closest)
        "rotate-cw" => "\uE72C",       // Refresh
        "scan-search" => "\uE773",     // Zoom
        "search" => "\uE721",          // Search
        "search-code" => "\uE721",     // Search
        "send" => "\uE724",            // Send
        "settings" => "\uE713",        // Settings
        "shield" => "\uE83D",          // Shield
        "shield-check" => "\uE83D",    // Shield
        "table" => "\uE80A",           // CalendarDay (grid-like)
        "target" => "\uE7B7",          // Target
        "thermometer" => "\uE9CA",     // Diagnostic
        "triangle-alert" => "\uE7BA",  // Warning
        "user" => "\uE77B",            // Contact
        "users" => "\uE716",           // People
        "workflow" => "\uE8C1",        // Org
        "wrench" => "\uE90F",          // Repair
        "zap" => "\uE945",             // Flashlight
        _ => "\uE735",                 // StatusCircleQuestion (fallback)
    };

    // ── Color resolution ──

    private System.Windows.Media.Brush ResolveBrush(string? colorRef, string fallback)
    {
        if (string.IsNullOrEmpty(colorRef)) colorRef = fallback;

        if (colorRef.StartsWith('$'))
        {
            var token = colorRef[1..];
            colorRef = _colorTokens.TryGetValue(token, out var resolved) ? resolved : fallback;
        }

        if (string.IsNullOrEmpty(colorRef) || colorRef == "Transparent")
            return Brushes.Transparent;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorRef)!;
            return new SolidColorBrush(color);
        }
        catch { return Brushes.Transparent; }
    }

    // ── JSON helpers (internal for testing) ──

    internal static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    /// <summary>
    /// Returns (scaledValue, isFillContainer). "fill_container" → (0, true).
    /// </summary>
    private static (double value, bool fill) GetDimension(JsonElement el, string prop, double scale)
    {
        if (el.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number)
                return (v.GetDouble() * scale, false);
            if (v.ValueKind == JsonValueKind.String && v.GetString() == "fill_container")
                return (0, true);
        }
        return (0, false);
    }

    internal static double GetDouble(JsonElement el, string prop, double fallback)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return fallback;
    }

    private static FontWeight ParseFontWeight(string fw)
        => fw switch
        {
            "700" or "800" or "900" or "bold" => FontWeights.Bold,
            "600" or "semibold" => FontWeights.SemiBold,
            "300" or "light" => FontWeights.Light,
            _ => FontWeights.Normal,
        };

    private static Thickness GetPadding(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("padding", out var p)) return new Thickness(0);

        if (p.ValueKind == JsonValueKind.Number)
            return new Thickness(p.GetDouble() * scale);

        if (p.ValueKind == JsonValueKind.Array)
        {
            var arr = p.EnumerateArray().Select(x => x.GetDouble() * scale).ToArray();
            return arr.Length switch
            {
                1 => new Thickness(arr[0]),
                2 => new Thickness(arr[1], arr[0], arr[1], arr[0]),
                4 => new Thickness(arr[3], arr[0], arr[1], arr[2]),
                _ => new Thickness(0),
            };
        }

        return new Thickness(0);
    }
}
