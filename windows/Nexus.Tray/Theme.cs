using System.Drawing.Drawing2D;
using Nexus.Core.Pairing;

namespace Nexus.Tray;

/// <summary>Nexus PC look on the desktop: near-black background, violet and cyan accents.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(7, 7, 11);
    public static readonly Color Surface = Color.FromArgb(22, 22, 30);
    public static readonly Color Text = Color.FromArgb(235, 235, 245);
    public static readonly Color Muted = Color.FromArgb(150, 150, 170);
    public static readonly Color Violet = Color.FromArgb(155, 92, 255);
    public static readonly Color Cyan = Color.FromArgb(0, 220, 255);

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = new Font("Segoe UI", 10f);
        foreach (Control control in form.Controls)
        {
            Style(control);
        }
    }

    private static void Style(Control control)
    {
        switch (control)
        {
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Violet;
                button.BackColor = Surface;
                button.ForeColor = Text;
                break;
            case TextBox or ListBox or CheckedListBox or ListView:
                control.BackColor = Surface;
                control.ForeColor = Text;
                break;
        }
        foreach (Control child in control.Controls)
        {
            Style(child);
        }
    }

    /// <summary>Tray icon drawn in code: a violet-to-cyan ring with "N". Grey when not connected.</summary>
    public static Icon CreateIcon(bool connected)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ring = connected
                ? new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Violet, Cyan, 45f)
                : new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.Gray, Color.DimGray, 45f);
            g.FillEllipse(ring, 1, 1, 30, 30);
            using var inner = new SolidBrush(Background);
            g.FillEllipse(inner, 5, 5, 22, 22);
            using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Text);
            var size = g.MeasureString("N", font);
            g.DrawString("N", font, text, (32 - size.Width) / 2, (32 - size.Height) / 2);
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
