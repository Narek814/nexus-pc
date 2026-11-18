using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Core.Apps;
using Nexus.Core.Power;
using Nexus.Windows;

namespace Nexus.Tray;

/// <summary>Desktop actions in the signed-in person's own session (as that person, never as SYSTEM).</summary>
internal sealed partial class WindowsDesktop : IDesktop
{
    private readonly WindowsPowerController _power = new(forceOtherSessions: false);

    public DesktopResult Launch(AppEntry app)
    {
        if (app.Kind == AppLaunchKind.PackagedApp && !PackagedAppIds.IsValid(app.Target))
        {
            return new DesktopResult(false, $"{app.DisplayName}: invalid Store app id.");
        }
        var start = app.Kind switch
        {
            // Packaged (Store) apps start through the shell by AppUserModelID.
            AppLaunchKind.PackagedApp => new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.Target) { UseShellExecute = false },
            _ => new ProcessStartInfo(app.Target)
            {
                UseShellExecute = true, // .exe, .lnk and .url (e.g. steam://) all go through the shell
                WorkingDirectory = app.Kind == AppLaunchKind.Executable ? Path.GetDirectoryName(app.Target) ?? string.Empty : string.Empty,
            },
        };
        try
        {
            using var process = Process.Start(start);
            return DesktopResult.Success;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new DesktopResult(false, $"Could not start {app.DisplayName}: {ex.Message}");
        }
    }

    public DesktopResult RequestClose(AppEntry app)
    {
        if (app.ProcessPath is null)
        {
            return new DesktopResult(false, "No executable is known for this app.");
        }
        var asked = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.ProcessPath)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, app.ProcessPath, StringComparison.OrdinalIgnoreCase)
                        && process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                    {
                        asked++;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Elevated or already exited: not ours to close.
                }
            }
        }
        return asked > 0
            ? new DesktopResult(true, $"Asked {asked} window(s) to close.")
            : new DesktopResult(false, $"{app.DisplayName} is not running (or has no window).");
    }

    public DesktopResult Lock() =>
        LockWorkStation() ? DesktopResult.Success : new DesktopResult(false, $"LockWorkStation failed ({Marshal.GetLastPInvokeError()}).");

    public PowerOutcome SchedulePower(PowerAction action, TimeSpan delay, bool force, string reason) =>
        _power.Schedule(action, delay, force, reason);

    public DesktopResult SendInput(IReadOnlyList<Nexus.Core.Input.InputStep> steps)
    {
        var inputs = new INPUT[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            inputs[i] = s.IsMouse
                ? new INPUT { Type = InputMouse, U = new InputUnion { Mouse = new MOUSEINPUT { Dx = s.Dx, Dy = s.Dy, MouseData = unchecked((uint)s.MouseData), Flags = s.Flags } } }
                : new INPUT { Type = InputKeyboard, U = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = s.VirtualKey, Scan = s.Scan, Flags = s.Flags } } };
        }
        var sent = SendInputNative((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        // Windows rejects input while the secure desktop (lock screen, UAC prompt) is active. Input into
        // windows of elevated apps is dropped silently (UIPI) and cannot be detected here.
        return sent == inputs.Length
            ? DesktopResult.Success
            : new DesktopResult(false, $"Windows не принял ввод: экран ПК заблокирован или открыт запрос администратора (код {Marshal.GetLastPInvokeError()}).");
    }

    public ClipboardRead GetClipboardText() =>
        OnStaThread(() => Clipboard.ContainsText(TextDataFormat.UnicodeText)
            ? new ClipboardRead(true, Clipboard.GetText(TextDataFormat.UnicodeText))
            : new ClipboardRead(true, null))
        ?? new ClipboardRead(false, null, "Буфер обмена занят другой программой — попробуйте ещё раз.");

    public DesktopResult SetClipboardText(string text) =>
        OnStaThread(() =>
        {
            // Retries for a moment: another app may hold the clipboard open.
            Clipboard.SetDataObject(text, copy: true, retryTimes: 10, retryDelay: 100);
            return DesktopResult.Success;
        })
        ?? new DesktopResult(false, "Буфер обмена занят другой программой — попробуйте ещё раз.");

    /// <summary>The clipboard API needs a single-threaded apartment; pipe requests arrive on pool threads.</summary>
    private static T? OnStaThread<T>(Func<T> work)
        where T : class
    {
        T? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (ExternalException)
            {
                result = null; // clipboard locked by another process
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>Set by the tray: shows the question on its UI thread and returns the answer.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public bool ConfirmScreenShare(string deviceName) => Confirm?.Invoke(deviceName) ?? false;

    /// <summary>
    /// GDI capture of the primary monitor with the mouse pointer drawn in, scaled and JPEG-encoded
    /// (no extra libraries). Some fullscreen games and protected video come out black: Windows hides them from GDI.
    /// </summary>
    public Nexus.Core.Screen.ScreenCapture CaptureScreen(int maxWidth, int quality, string? knownHash)
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        if (bounds.Width == 0)
        {
            return new(false, null, 0, 0, null, Message: "Нет экрана.");
        }
        try
        {
            using var full = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                DrawPointer(g, bounds);
            }
            var width = Math.Min(maxWidth, bounds.Width);
            var height = Math.Max(1, (int)((long)bounds.Height * width / bounds.Width));
            using var scaled = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(full, 0, 0, width, height);
            }
            var hash = PixelHash(scaled);
            if (hash == knownHash)
            {
                return new(true, null, width, height, hash, Unchanged: true);
            }
            using var jpeg = new MemoryStream();
            using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
            parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            scaled.Save(jpeg, JpegCodec, parameters);
            return new(true, jpeg.ToArray(), width, height, hash);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
            // CopyFromScreen fails while the secure desktop (lock screen, UAC prompt) is shown.
            return new(false, null, 0, 0, null, Message: "Экран ПК сейчас недоступен: он заблокирован или открыт запрос администратора.");
        }
    }

    private static readonly System.Drawing.Imaging.ImageCodecInfo JpegCodec =
        System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);

    private static string PixelHash(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<byte>((void*)data.Scan0, Math.Abs(data.Stride) * data.Height);
                return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(span));
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public Nexus.Core.Screen.IScreenVideo? StartVideo(int maxWidth, int bitrateKbps, out string? problem) =>
        ScreenVideoStream.Start(maxWidth, bitrateKbps, out problem);

    internal static void DrawPointer(Graphics g, Rectangle bounds)
    {
        var info = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0)
        {
            return; // hidden pointer (e.g. while typing in some apps)
        }
        var hdc = g.GetHdc();
        try
        {
            DrawIcon(hdc, info.X - bounds.X, info.Y - bounds.Y, info.Cursor);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CURSORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIcon(nint hdc, int x, int y, nint icon);

    /// <summary>The user's Downloads folder, wherever they moved it.</summary>
    public static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(downloads, 0, 0, out var path) == 0)
        {
            try
            {
                return Marshal.PtrToStringUni(path) ?? DefaultDownloads();
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        return DefaultDownloads();
    }

    private static string DefaultDownloads() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Mark of the Web, like a browser download: Windows SmartScreen and Office then treat a file from
    /// the phone as coming from outside (an .exe asks before running, documents open in Protected View).
    /// </summary>
    public static void MarkAsDownloaded(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // FAT/exFAT drives have no alternate data streams.
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static partial uint SendInputNative(uint count, [In] INPUT[] inputs, int size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();
}
