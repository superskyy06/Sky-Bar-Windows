using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SkyBar.SystemIntegration;

public static class ShortcutResolver
{
    public static string? ResolveExecutablePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return filePath;

        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            try
            {
                dynamic shortcut = ((dynamic)shell).CreateShortcut(filePath);
                string? targetPath = shortcut.TargetPath as string;
                if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath)) return targetPath;
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        return null;
    }
}
