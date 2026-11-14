using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Nexus.Tray;

/// <summary>Start menu shortcuts: listing them and reading a .lnk's target (to be able to close the app later).</summary>
internal static class ShellLinks
{
    public sealed record StartMenuItem(string Name, string Path);

    public static IReadOnlyList<StartMenuItem> StartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var items = new Dictionary<string, StartMenuItem>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                var extension = System.IO.Path.GetExtension(file);
                if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("удал", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                items.TryAdd(name, new StartMenuItem(name, file));
            }
        }
        return [.. items.Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The .exe a .lnk points to, or null (for .url files, or links to non-executables).</summary>
    public static string? ResolveExecutable(string shortcutPath)
    {
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var buffer = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            var target = buffer.ToString();
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? target : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    // Only the first method is used; it must stay first to match the COM vtable.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
    }
}
