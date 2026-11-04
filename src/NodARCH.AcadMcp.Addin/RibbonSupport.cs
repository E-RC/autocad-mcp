using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace NodARCH.AcadMcp.Addin;

/// <summary>
/// Adds a "NodARCH MCP" panel to the Add-ins ribbon tab, so the bridge can be opened and closed
/// without typing commands.
///
/// WHY THIS EXISTS AT ALL: the bridge is opt-in per session, which means the user has to do
/// something deliberate every time AutoCAD starts. Making that "click a button" rather than
/// "remember a command name" is what keeps the safe default usable — a security control people
/// route around is not a security control.
///
/// TIMING: the ribbon does not exist yet when Initialize() runs. ComponentManager raises
/// ItemInitialized as the UI comes up, so we subscribe and build on the first raise where the
/// ribbon is actually present, then unsubscribe.
/// </summary>
internal static class RibbonSupport
{
    private const string PanelSourceId = "NODARCH_MCP_PANEL";
    private const string PanelTitle = "NodARCH MCP";

    private static bool _built;
    private static RibbonButton? _startButton;
    private static RibbonButton? _stopButton;

    /// <summary>
    /// Safe to call from Initialize(). Never throws: a missing or disabled ribbon must not stop
    /// the add-in loading, because the commands still work without it.
    /// </summary>
    internal static void Install()
    {
        try { InstallCore(); }
        catch
        {
            // Ribbon is cosmetic. NODARCHSTART still works if this fails.
        }
    }

    // Isolated so the Autodesk.Windows type references are only resolved if we get here.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallCore()
    {
        if (ComponentManager.Ribbon is not null)
        {
            Build();
            return;
        }

        ComponentManager.ItemInitialized += OnItemInitialized;
    }

    private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon is null) return;

        ComponentManager.ItemInitialized -= OnItemInitialized;
        try { Build(); } catch { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Build()
    {
        if (_built) return;

        var ribbon = ComponentManager.Ribbon;
        if (ribbon is null) return;

        var tab = FindAddInsTab(ribbon);
        if (tab is null)
        {
            // No Add-ins tab (it can be turned off, and its id/title are localised). Rather than
            // give up, make our own so the buttons are always reachable.
            tab = new RibbonTab { Title = "NodARCH", Id = "NODARCH_MCP_TAB" };
            ribbon.Tabs.Add(tab);
        }

        // A reload must not stack duplicate panels.
        foreach (var existing in tab.Panels)
        {
            if (existing.Source?.Id == PanelSourceId)
            {
                _built = true;
                return;
            }
        }

        var source = new RibbonPanelSource { Title = PanelTitle, Id = PanelSourceId };

        _startButton = Button("Start", "NODARCHSTART", Icons.Play,
            "Open the MCP bridge for this AutoCAD session.\n\n" +
            "This lets an MCP client drive AutoCAD, including tools that execute arbitrary " +
            "code. It is deliberately not opened automatically.");
        source.Items.Add(_startButton);

        _stopButton = Button("Stop", "NODARCHSTOP", Icons.Stop,
            "Close the bridge and invalidate this session's token.");
        source.Items.Add(_stopButton);

        source.Items.Add(Button("Status", "NODARCH", Icons.Status,
            "Open the status window: whether the bridge is listening, how to connect an agent, " +
            "and Start and Stop."));

        tab.Panels.Add(new RibbonPanel { Source = source });
        _built = true;

        // Only the action that applies is enabled, and it follows the bridge however it was
        // started or stopped: these buttons, a typed command, or the status window.
        HttpBridge.StateChanged += ShowBridgeState;
        ShowBridgeState();
    }

    private static void ShowBridgeState()
    {
        if (_startButton is not null) _startButton.IsEnabled = !HttpBridge.IsRunning;
        if (_stopButton is not null) _stopButton.IsEnabled = HttpBridge.IsRunning;
    }

    /// <summary>
    /// The Add-ins tab's id and title are both unreliable to hard-code — the id differs between
    /// releases and the title is localised — so match either, loosely.
    /// </summary>
    private static RibbonTab? FindAddInsTab(RibbonControl ribbon)
    {
        foreach (var tab in ribbon.Tabs)
        {
            var id = tab.Id ?? string.Empty;
            var title = (tab.Title ?? string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

            if (id.IndexOf("ADDINS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.Equals("AddIns", StringComparison.OrdinalIgnoreCase))
                return tab;
        }
        return null;
    }

    private static RibbonButton Button(string text, string command, Func<int, BitmapSource> icon, string tooltip) => new()
    {
        Id = "NODARCH_MCP_" + command,
        Text = text,
        ShowText = true,
        ShowImage = true,
        Image = icon(16),
        LargeImage = icon(32),
        Size = RibbonItemSize.Large,
        Orientation = System.Windows.Controls.Orientation.Vertical,
        // Leading _ and trailing space: locale-independent, and the space submits the command.
        CommandParameter = "_" + command + " ",
        CommandHandler = new CommandRelay("_" + command + " "),
        ToolTip = new RibbonToolTip { Title = text, Content = tooltip },
    };

    /// <summary>
    /// Sends one fixed command to the command line, exactly as if typed.
    ///
    /// Each button gets its OWN relay carrying its own macro, rather than a shared handler that
    /// reads the macro back off the clicked item. That is deliberate: the object the ribbon
    /// hands to Execute is the RibbonItem, not the CommandParameter string, and a handler that
    /// assumes otherwise silently does nothing on every click. Holding the macro here removes
    /// the guess entirely.
    /// </summary>
    private sealed class CommandRelay(string macro) : ICommand
    {
        // Always executable; no state to notify about.
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc is null) return;

            // Queued, not immediate: a ribbon click arrives on the UI thread while AutoCAD may
            // still be in a command, and SendStringToExecute is the supported way in.
            doc.SendStringToExecute(macro, true, false, true);
        }
    }

    /// <summary>
    /// Icons are drawn in code rather than shipped as resources: three glyphs do not justify
    /// binary assets, and drawing them means they scale to whatever size is asked for.
    ///
    /// Each is a round badge in a NodARCH palette colour carrying a white glyph. The badge brings
    /// its own contrast, so one bitmap reads on AutoCAD's light and dark ribbons alike, where a
    /// bare coloured glyph is always too faint on one of them. A rim a shade darker than the
    /// badge keeps its edge crisp on a light ribbon. Bitmaps are rendered at twice their size
    /// in pixels, so they stay sharp on high-DPI screens.
    /// </summary>
    internal static class Icons
    {
        // Top and bottom of each badge's gradient, then its rim.
        private static readonly Color[] Green = [Rgb(0x45, 0xC9, 0x8F), Rgb(0x26, 0xA0, 0x6B), Rgb(0x1B, 0x7F, 0x54)];
        private static readonly Color[] Red = [Rgb(0xEF, 0x62, 0x66), Rgb(0xD6, 0x3A, 0x40), Rgb(0xA9, 0x2B, 0x30)];
        private static readonly Color[] Blue = [Rgb(0x4A, 0x80, 0xE6), Rgb(0x25, 0x59, 0xC2), Rgb(0x1C, 0x46, 0x9C)];

        internal static BitmapSource Play(int size) => Badge(size, Green, (dc, s, white) =>
        {
            // Rounded by stroking the filled triangle with a round-joined pen of the same white.
            // Its centroid sits a touch right of centre, where a play triangle looks centred.
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(s * 0.41, s * 0.31), true, true);
                c.LineTo(new Point(s * 0.71, s * 0.50), true, true);
                c.LineTo(new Point(s * 0.41, s * 0.69), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(white, RoundPen(white, s * 0.08), g);
        });

        internal static BitmapSource Stop(int size) => Badge(size, Red, (dc, s, white) =>
            dc.DrawRoundedRectangle(white, null, new Rect(s * 0.34, s * 0.34, s * 0.32, s * 0.32), s * 0.06, s * 0.06));

        /// <summary>A pulse line: the bridge's state at a glance.</summary>
        internal static BitmapSource Status(int size) => Badge(size, Blue, (dc, s, white) =>
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(s * 0.22, s * 0.53), false, false);
                c.PolyLineTo(
                [
                    new Point(s * 0.37, s * 0.53), new Point(s * 0.45, s * 0.33), new Point(s * 0.56, s * 0.70),
                    new Point(s * 0.64, s * 0.53), new Point(s * 0.78, s * 0.53),
                ], true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, RoundPen(white, s * 0.085), g);
        });

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static Pen RoundPen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            return pen;
        }

        private static BitmapSource Badge(int size, Color[] colours, Action<DrawingContext, double, Brush> glyph)
        {
            var fill = new LinearGradientBrush(colours[0], colours[1], 90);
            fill.Freeze();
            var rim = new Pen(new SolidColorBrush(colours[2]), size / 32.0);
            rim.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawEllipse(fill, rim, new Point(size * 0.5, size * 0.5), size * 0.46, size * 0.46);
                glyph(dc, size, Brushes.White);
            }

            // 192 dpi: twice the pixels, the same size on screen.
            var bmp = new RenderTargetBitmap(size * 2, size * 2, 192, 192, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();   // the ribbon reads these off its own thread
            return bmp;
        }
    }
}
