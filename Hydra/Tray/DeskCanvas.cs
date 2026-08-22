using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hydra.Desk;

namespace Hydra.Tray;

// The desk, drawn the way the operating system draws its display arrangement: every monitor to
// scale, in its place, labelled with the computer that is on it. Dragging a monitor moves it on the
// desk; picking a computer on it switches the monitor's input there and then.
internal sealed class DeskCanvas : Border
{
    private const double Pad = 24;
    private const int SnapPixels = 60;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4C, 0x9A, 0xFF),
        Color.FromRgb(0x6C, 0xC6, 0x4B),
        Color.FromRgb(0xE0, 0x8A, 0x3C),
        Color.FromRgb(0xB4, 0x7C, 0xE6),
        Color.FromRgb(0xE0, 0x5C, 0x7A),
    ];

    private readonly Canvas _canvas = new();
    private readonly TextBlock _placeholder = new()
    {
        Text = "Waiting for the computers to report their screens…",
        Opacity = 0.6,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Func<string, string, Task> _onHostPicked;
    private readonly Func<IReadOnlyList<DeskPlacement>, Task> _onArranged;

    private IReadOnlyList<DeskMonitorView> _monitors = [];
    private IReadOnlyList<string> _hosts = [];
    private readonly Dictionary<string, Rect> _desk = [];
    private double _scale = 0.1;

    private Control? _dragging;
    private string? _draggingId;
    private Point _grabOffset;

    internal DeskCanvas(Func<string, string, Task> onHostPicked, Func<IReadOnlyList<DeskPlacement>, Task> onArranged)
    {
        _onHostPicked = onHostPicked;
        _onArranged = onArranged;
        MinHeight = 260;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1C));
        BorderBrush = Brushes.DimGray;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        Child = new Panel { Children = { _placeholder, _canvas } };
        SizeChanged += (_, _) => Layout();
    }

    internal void Update(IReadOnlyList<DeskMonitorView> monitors, IReadOnlyList<string> hosts)
    {
        _monitors = monitors;
        _hosts = hosts;
        _desk.Clear();
        foreach (var monitor in monitors)
            _desk[monitor.Id] = new Rect(monitor.DeskX, monitor.DeskY, Math.Max(monitor.Width, 1), Math.Max(monitor.Height, 1));
        Layout();
    }

    private void Layout()
    {
        _canvas.Children.Clear();
        _placeholder.IsVisible = _monitors.Count == 0;
        if (_monitors.Count == 0 || Bounds.Width <= 1) return;

        var bounds = DeskBounds();
        var available = new Size(Math.Max(Bounds.Width - 2 * Pad, 80), Math.Max(Bounds.Height - 2 * Pad, 80));
        _scale = Math.Min(available.Width / Math.Max(bounds.Width, 1), available.Height / Math.Max(bounds.Height, 1));
        _scale = Math.Clamp(_scale, 0.01, 0.5);

        foreach (var monitor in _monitors)
        {
            var rect = _desk[monitor.Id];
            var tile = BuildTile(monitor, rect);
            Canvas.SetLeft(tile, Pad + (rect.X - bounds.X) * _scale);
            Canvas.SetTop(tile, Pad + (rect.Y - bounds.Y) * _scale);
            _canvas.Children.Add(tile);
        }
    }

    private Rect DeskBounds()
    {
        var left = _desk.Values.Min(r => r.X);
        var top = _desk.Values.Min(r => r.Y);
        var right = _desk.Values.Max(r => r.Right);
        var bottom = _desk.Values.Max(r => r.Bottom);
        return new Rect(left, top, Math.Max(right - left, 1), Math.Max(bottom - top, 1));
    }

    private Control BuildTile(DeskMonitorView monitor, Rect rect)
    {
        var accent = HostColour(monitor.ActiveHost);
        // Strictly proportional. Giving a tile a minimum size while positioning it at the true scale
        // is what made monitors overlap on screen even when the desk itself was laid out correctly.
        var width = rect.Width * _scale;
        var height = rect.Height * _scale;

        var title = new TextBlock
        {
            Text = monitor.Label,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var size = new TextBlock
        {
            Text = monitor.Width > 0 ? $"{monitor.Width} × {monitor.Height}" : "size unknown",
            Opacity = 0.6,
            FontSize = 11,
        };

        Control picker;
        // Every computer wired to this monitor is offered, including one whose input code is not
        // known yet: choosing it fails with the sentence that says how to teach it, which is far
        // more use than a tile that silently offers nothing.
        var choices = monitor.Sources.Select(s => s.Host).ToList();
        if (choices.Count > 1)
        {
            var combo = new ComboBox { ItemsSource = choices, MinWidth = 96, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectedItem = choices.FirstOrDefault(c => string.Equals(c, monitor.ActiveHost, StringComparison.OrdinalIgnoreCase));
            combo.SelectionChanged += async (_, _) =>
            {
                if (combo.SelectedItem is not string picked) return;
                if (string.Equals(picked, monitor.ActiveHost, StringComparison.OrdinalIgnoreCase)) return;
                await _onHostPicked(monitor.Id, picked);
            };
            picker = combo;
        }
        else
        {
            picker = new TextBlock
            {
                Text = monitor.ActiveHost ?? "no computer",
                Foreground = new SolidColorBrush(accent),
                FontSize = 12,
            };
        }

        var tile = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            ClipToBounds = true,
            Child = new StackPanel { Spacing = 3, Children = { title, size, picker } },
        };

        tile.PointerPressed += (_, e) => BeginDrag(tile, monitor.Id, e);
        tile.PointerMoved += (_, e) => Drag(e);
        tile.PointerReleased += (_, e) => EndDrag(e);
        return tile;
    }

    private void BeginDrag(Control tile, string monitorId, PointerPressedEventArgs e)
    {
        // A press that lands on the computer picker is a choice, not a drag.
        if (e.Source is ComboBox || (e.Source as Visual)?.FindAncestorOfType<ComboBox>() != null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = tile;
        _draggingId = monitorId;
        var position = e.GetPosition(_canvas);
        _grabOffset = new Point(position.X - Canvas.GetLeft(tile), position.Y - Canvas.GetTop(tile));
        e.Pointer.Capture(tile);
        e.Handled = true;
    }

    private void Drag(PointerEventArgs e)
    {
        if (_dragging == null) return;
        var position = e.GetPosition(_canvas);
        Canvas.SetLeft(_dragging, position.X - _grabOffset.X);
        Canvas.SetTop(_dragging, position.Y - _grabOffset.Y);
    }

    private void EndDrag(PointerReleasedEventArgs e)
    {
        if (_dragging == null || _draggingId == null) return;
        var tile = _dragging;
        var id = _draggingId;
        _dragging = null;
        _draggingId = null;
        e.Pointer.Capture(null);

        var bounds = DeskBounds();
        var rect = _desk[id];
        var deskX = (int)Math.Round((Canvas.GetLeft(tile) - Pad) / _scale + bounds.X);
        var deskY = (int)Math.Round((Canvas.GetTop(tile) - Pad) / _scale + bounds.Y);
        _desk[id] = Snap(id, new Rect(deskX, deskY, rect.Width, rect.Height));

        Layout();
        _ = _onArranged(_desk.Select(kv => new DeskPlacement(
            kv.Key, (int)kv.Value.X, (int)kv.Value.Y, (int)kv.Value.Width, (int)kv.Value.Height)).ToList());
    }

    // Pulls a dropped monitor flush against its neighbours. Without this a stray few pixels would
    // leave a gap the pointer can never cross, and the arrangement would look right but not work.
    private Rect Snap(string id, Rect moved)
    {
        double x = moved.X, y = moved.Y;
        foreach (var (otherId, other) in _desk)
        {
            if (otherId == id) continue;
            foreach (var (candidate, reference) in new[] { (other.Right, x), (other.X - moved.Width, x) })
                if (Math.Abs(candidate - reference) <= SnapPixels) x = candidate;
            foreach (var (candidate, reference) in new[] { (other.Bottom, y), (other.Y - moved.Height, y), (other.Y, y) })
                if (Math.Abs(candidate - reference) <= SnapPixels) y = candidate;
            if (Math.Abs(other.X - x) <= SnapPixels) x = other.X;
        }
        return new Rect(x, y, moved.Width, moved.Height);
    }

    private Color HostColour(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return Color.FromRgb(0x70, 0x70, 0x78);
        var index = _hosts.ToList().FindIndex(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        return Palette[(index < 0 ? Math.Abs(host.GetHashCode()) : index) % Palette.Length];
    }
}
