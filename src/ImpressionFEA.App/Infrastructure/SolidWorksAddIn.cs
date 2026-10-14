using Microsoft.Win32;

namespace ImpressionFEA.App.Infrastructure;

/// <summary>
/// Completes a machine-wide add-in installation for the current user. The installer runs elevated and writes only
/// machine keys (an elevated process may belong to another account), so the per-user "load at startup" flag that
/// SOLIDWORKS reads is set here, once, as the real user. A value the user already has (on or off) is left alone.
/// </summary>
public static class SolidWorksAddIn
{
    public const string Guid = "{9E53A9B9-AFAE-45A0-B90A-FB51F8CE3A65}";

    public static void EnsureUserStartup()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks\AddIns\" + Guid);
            if (machine is null) return;
            using var startup = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + Guid);
            if (startup.GetValue(null) is null) startup.SetValue(null, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Preferences only: SOLIDWORKS still lists the add-in under Tools > Add-Ins.
        }
    }
}
