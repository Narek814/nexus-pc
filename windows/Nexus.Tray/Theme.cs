using System.Drawing.Drawing2D;
using Nexus.Core.Pairing;

namespace Nexus.Tray;

/// <summary>
/// Nexus PC look on the desktop (cyberpunk, like the iPhone app): near-black background with a soft
/// violet glow, neon buttons with a violet→cyan outline, dark Windows title bar with rounded corners
/// and a violet border (Windows 11), dark tray menu.
/// </summary>
internal static partial class Theme
{
    public static readonly Color Background = Color.FromArgb(7, 7, 11);
    public static readonly Color Surface = Color.FromArgb(22, 22, 30);
    public static readonly Color SurfaceHover = Color.FromArgb(34, 30, 52);
    public static readonly Color Text = Color.FromArgb(235, 235, 245);
    public static readonly Color Muted = Color.FromArgb(150, 150, 170);
    public static readonly Color Violet = Color.FromArgb(155, 92, 255);
    public static readonly Color Cyan = Color.FromArgb(0, 220, 255);

    private static readonly HashSet<Button> Primary = [];

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = new Font("Segoe UI", 10f);
        form.DoubleBuffered(true);
        form.Paint += PaintBackdrop;
        form.Resize += (_, _) => form.Invalidate();
        form.HandleCreated += (_, _) => DarkWindow(form.Handle);
        if (form.IsHandleCreated)
        {
            DarkWindow(form.Handle);
        }
        foreach (Control control in form.Controls)
        {
            Style(control);
        }
    }

    /// <summary>The main action of a window: filled with the violet→cyan gradient.</summary>
    public static void MakePrimary(Button button)
    {
        Primary.Add(button);
        button.Disposed += (_, _) => Primary.Remove(button);
        button.Invalidate();
    }

    private static void Style(Control control)
    {
        switch (control)
        {
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.MouseOverBackColor = Background;
                button.FlatAppearance.MouseDownBackColor = Background;
                button.BackColor = Background;
                button.ForeColor = Text;
                button.Cursor = Cursors.Hand;
                button.Paint += PaintButton;
                button.MouseEnter += (_, _) => button.Invalidate();
                button.MouseLeave += (_, _) => button.Invalidate();
                button.EnabledChanged += (_, _) => button.Invalidate();
                break;
            case TextBox or ListBox or CheckedListBox or ListView or ComboBox:
                control.BackColor = Surface;
                control.ForeColor = Text;
                if (control is ListBox list)
                {
                    list.BorderStyle = BorderStyle.None;
                }
                break;
            case TabControl or TabPage:
                control.BackColor = Background;
                control.ForeColor = Text;
                break;
        }
        foreach (Control child in control.Controls)
        {
            Style(child);
        }
    }

    /// <summary>Soft violet glow in the top corner and a thin neon line along the top edge.</summary>
    private static void PaintBackdrop(object? sender, PaintEventArgs e)
    {
        if (sender is not Form form || form.ClientSize.Width == 0)
        {
            return;
        }
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = new GraphicsPath())
        {
            var w = form.ClientSize.Width;
            path.AddEllipse(-w * 0.2f, -w * 0.45f, w * 1.0f, w * 0.7f);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(38, Violet),
                SurroundColors = [Color.FromArgb(0, Violet)],
            };
            g.FillPath(glow, path);
        }
        using var line = new LinearGradientBrush(new Rectangle(0, 0, form.ClientSize.Width, 3), Violet, Cyan, 0f);
        g.FillRectangle(line, 0, 0, form.ClientSize.Width, 3);
    }

    private static void PaintButton(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(button.Parent?.BackColor ?? Background);
        var hover = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
        var rect = new RectangleF(1.5f, 1.5f, button.Width - 3, button.Height - 3);
        using var path = Rounded(rect, Math.Min(10, rect.Height / 2));
        using var gradient = new LinearGradientBrush(button.ClientRectangle, Violet, Cyan, 0f);
        var primary = Primary.Contains(button);
        if (!button.Enabled)
        {
            using var fill = new SolidBrush(Surface);
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(60, Muted), 1.5f);
            g.DrawPath(border, path);
        }
        else if (primary)
        {
            g.FillPath(gradient, path);
            if (hover)
            {
                using var shine = new SolidBrush(Color.FromArgb(40, Color.White));
                g.FillPath(shine, path);
            }
        }
        else
        {
            using var fill = new SolidBrush(hover ? SurfaceHover : Surface);
            g.FillPath(fill, path);
            if (hover)
            {
                // Neon glow: a wider, faint outline under the sharp one.
                using var glow = new Pen(Color.FromArgb(70, Cyan), 4f);
                g.DrawPath(glow, path);
            }
            using var border = new Pen(gradient, 1.6f);
            g.DrawPath(border, path);
        }
        var color = !button.Enabled ? Muted : primary ? Color.FromArgb(10, 8, 20) : Text;
        var font = primary ? new Font(button.Font, FontStyle.Bold) : button.Font;
        try
        {
            TextRenderer.DrawText(g, button.Text, font, button.ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        finally
        {
            if (primary)
            {
                font.Dispose();
            }
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Dark title bar, rounded corners and a violet border (Windows 11; ignored on Windows 10).</summary>
    internal static void DarkWindow(IntPtr handle)
    {
        var on = 1;
        _ = DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        var border = ColorRef(Violet);
        _ = DwmSetWindowAttribute(handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        var caption = ColorRef(Background);
        _ = DwmSetWindowAttribute(handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [System.Runtime.InteropServices.LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void DoubleBuffered(this Control control, bool on) =>
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(control, on);

    /// <summary>Tab headers drawn dark, the selected one with a neon underline (Windows draws them white).</summary>
    public static void StyleTabs(TabControl tabs)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(150, 34);
        tabs.DrawItem += (_, e) =>
        {
            var g = e.Graphics;
            var selected = e.Index == tabs.SelectedIndex;
            using (var fill = new SolidBrush(selected ? SurfaceHover : Background))
            {
                g.FillRectangle(fill, e.Bounds);
            }
            if (selected)
            {
                using var line = new LinearGradientBrush(e.Bounds, Violet, Cyan, 0f);
                g.FillRectangle(line, e.Bounds.X, e.Bounds.Bottom - 3, e.Bounds.Width, 3);
            }
            TextRenderer.DrawText(g, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, selected ? Text : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
    }

    /// <summary>Dark tray menu with a violet highlight, in the app's style.</summary>
    public static void StyleMenu(ContextMenuStrip menu)
    {
        menu.Renderer = new NeonMenuRenderer();
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Font = new Font("Segoe UI", 10f);
        menu.ShowImageMargin = false;
        menu.Opened += (_, _) => DarkWindow(menu.Handle);
    }

    private sealed class NeonMenuRenderer : ToolStripProfessionalRenderer
    {
        public NeonMenuRenderer()
            : base(new NeonColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(rect, 6);
                using var fill = new LinearGradientBrush(rect, Color.FromArgb(90, Violet), Color.FromArgb(60, Cyan), 0f);
                e.Graphics.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(160, Violet));
                e.Graphics.DrawPath(border, path);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A cyan dot instead of the default check box.
            var r = new Rectangle(e.ImageRectangle.X + 4, e.ImageRectangle.Y + 4, 8, 8);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Cyan);
            e.Graphics.FillEllipse(brush, r);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var line = new LinearGradientBrush(new Rectangle(0, y, e.Item.Width, 1), Color.FromArgb(120, Violet), Color.FromArgb(40, Cyan), 0f);
            e.Graphics.FillRectangle(line, 10, y, e.Item.Width - 20, 1);
        }
    }

    private sealed class NeonColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Color.FromArgb(120, Violet);
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => SurfaceHover;
        public override Color SeparatorDark => Color.FromArgb(60, Violet);
        public override Color SeparatorLight => Color.Transparent;
    }

    /// <summary>
    /// Tray icon drawn in code, like the app icon: a neon "N" (violet→cyan) on a dark rounded tile.
    /// Drawn at 64 px so it stays sharp at 150–200 % scaling. Grey when not connected to the service.
    /// </summary>
    public static Icon CreateIcon(bool connected)
    {
        const int size = 64;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var tile = Rounded(new RectangleF(2, 2, size - 4, size - 4), 14))
            using (var fill = new SolidBrush(Color.FromArgb(16, 13, 28)))
            {
                g.FillPath(fill, tile);
            }
            var start = connected ? Violet : Color.FromArgb(120, 120, 130);
            var end = connected ? Cyan : Color.FromArgb(80, 80, 90);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, size, size), start, end, 45f);
            using var n = new GraphicsPath();
            // The N as one polyline: up the left bar, down the diagonal, up the right bar.
            n.AddLines(new PointF[] { new(18, 48), new(18, 16), new(46, 48), new(46, 16) });
            if (connected)
            {
                using var glow = new Pen(Color.FromArgb(70, Cyan), 13f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawPath(glow, n);
            }
            using var pen = new Pen(brush, 7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, n);
        }
        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        NativeIcons.DestroyIcon(handle);
        return icon;
    }

    /// <summary>Black-on-white QR with a 4-module quiet zone (what phone cameras expect).</summary>
    public static Bitmap RenderQr(QrCode qr, int pixelsPerModule)
    {
        const int quiet = 4;
        var size = (qr.Size + quiet * 2) * pixelsPerModule;
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var dark = new SolidBrush(Color.Black);
        for (var y = 0; y < qr.Size; y++)
        {
            for (var x = 0; x < qr.Size; x++)
            {
                if (qr[x, y])
                {
                    g.FillRectangle(dark, (x + quiet) * pixelsPerModule, (y + quiet) * pixelsPerModule, pixelsPerModule, pixelsPerModule);
                }
            }
        }
        return bitmap;
    }
}

internal static partial class NativeIcons
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr handle);
}
