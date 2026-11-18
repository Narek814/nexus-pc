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

    /// <summary>
    /// Packaged (Store/MSIX) apps from the shell's "All apps" folder (shell:AppsFolder): name and
    /// AppUserModelID. Uses the Shell.Application automation object, so call it on an STA (UI) thread.
    /// </summary>
    public static IReadOnlyList<StartMenuItem> PackagedApps()
    {
        var items = new Dictionary<string, StartMenuItem>(StringComparer.OrdinalIgnoreCase);
        object? shell = null, folder = null, folderItems = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return [];
            }
            shell = Activator.CreateInstance(type);
            folder = shell is null ? null : Call(shell, "NameSpace", "shell:AppsFolder");
            folderItems = folder is null ? null : Call(folder, "Items");
            var count = folderItems is null ? 0 : Convert.ToInt32(Get(folderItems, "Count"), System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
            {
                var item = Call(folderItems!, "Item", i);
                if (item is null)
                {
                    continue;
                }
                try
                {
                    if (Get(item, "Name") is string name && Get(item, "Path") is string aumid
                        && !string.IsNullOrWhiteSpace(name) && Nexus.Core.Apps.PackagedAppIds.IsValid(aumid))
                    {
                        items.TryAdd(aumid, new StartMenuItem(name.Trim(), aumid));
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(item);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or InvalidCastException or MissingMethodException)
        {
            // Listing failed: show what was found so far.
        }
        finally
        {
            foreach (var com in new[] { folderItems, folder, shell })
            {
                if (com is not null && Marshal.IsComObject(com))
                {
                    Marshal.FinalReleaseComObject(com);
                }
            }
        }
        return [.. items.Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static object? Call(object target, string method, params object[] args) =>
        target.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod, null, target, args, System.Globalization.CultureInfo.InvariantCulture);

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, target, null, System.Globalization.CultureInfo.InvariantCulture);

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
