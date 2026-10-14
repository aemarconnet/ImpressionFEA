using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using Environment = System.Environment;

[assembly: ComVisible(false)]

namespace ImpressionFEA.SolidWorks;

/// <summary>
/// ImpressionFEA add-in for SOLIDWORKS: adds an "ImpressionFEA" CommandManager tab (parts and assemblies) that exports
/// the model to STEP and hands it to the ImpressionFEA app, which runs the forging simulation out of process (the
/// solver never loads into SOLIDWORKS). Commands:
/// <list type="bullet">
/// <item>Simulate: export the active configuration and open it in ImpressionFEA (settings are kept per configuration).</item>
/// <item>Send Update: re-export after editing; ImpressionFEA re-imports it with every setting kept.</item>
/// <item>All Configurations: export every configuration and solve each as a run, for comparison.</item>
/// <item>Job Folder: open the folder with the exported STEP files, setups and results.</item>
/// </list>
/// </summary>
[ComVisible(true)]
[Guid(AddInGuid)]
[ProgId("ImpressionFEA.SolidWorksAddIn")]
[ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class AddIn : ISwAddin
{
    public const string AddInGuid = "9E53A9B9-AFAE-45A0-B90A-FB51F8CE3A65";
    public const string Title = "ImpressionFEA";
    public const string Description = "Closed-die impression forging simulation (Marconnet Technologies LLC)";

    private const int GroupId = 51770;
    private const string TabName = "ImpressionFEA";

    // Command user ids (stable: SOLIDWORKS remembers toolbar customisation by them).
    private const int CmdSimulate = 1, CmdUpdate = 2, CmdConfigurations = 3, CmdFolder = 4;

    private ISldWorks? _sw;
    private ICommandManager? _commands;
    private int _cookie;
    private string _status = "not connected";

    // ------------------------------------------------------------------ ISwAddin

    public bool ConnectToSW(object ThisSW, int Cookie)
    {
        _sw = (ISldWorks)ThisSW;
        _cookie = Cookie;
        Log("connected to SOLIDWORKS " + _sw.RevisionNumber());
        _sw.SetAddinCallbackInfo2(0, this, Cookie);
        _commands = _sw.GetCommandManager(Cookie);
        try
        {
            CreateCommands();
            Log(_status);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _status = "CreateCommands failed: " + ex;
            Log(_status);
            Message("ImpressionFEA could not create its commands: " + ex.Message, swMessageBoxIcon_e.swMbStop);
        }

        return true;
    }

    public bool DisconnectFromSW()
    {
        try
        {
            RemoveCommands();
        }
        catch (COMException)
        {
            // SOLIDWORKS is shutting down; nothing to clean.
        }

        if (_commands is not null) Marshal.ReleaseComObject(_commands);
        _commands = null;
        if (_sw is not null) Marshal.ReleaseComObject(_sw);
        _sw = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return true;
    }

    // ------------------------------------------------------------------ CommandManager tab

    private static readonly (int Id, string Name, string Hint, string Callback)[] Commands =
    [
        (CmdSimulate, "Simulate", "Export the active configuration to STEP and open it in ImpressionFEA (top die, material and settings are kept per configuration)", nameof(OnSimulate)),
        (CmdUpdate, "Send Update", "Re-export the active configuration after editing; ImpressionFEA re-imports it with every setting kept", nameof(OnSendUpdate)),
        (CmdConfigurations, "All Configurations", "Export every configuration and solve each one as a run in ImpressionFEA, for comparison", nameof(OnAllConfigurations)),
        (CmdFolder, "Job Folder", "Open the folder with this model's exported STEP files, setups and results", nameof(OnJobFolder)),
    ];

    private void CreateCommands()
    {
        var cm = _commands ?? throw new InvalidOperationException("No command manager.");
        var icons = IconFactory.Write(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImpressionFEA", "SolidWorks", "icons"));

        // Recreate the group when the command set changed since SOLIDWORKS last stored it.
        bool ignorePrevious = false;
        if (cm.GetGroupDataFromRegistry(GroupId, out object registryIds) && registryIds is int[] known)
        {
            ignorePrevious = !Commands.Select(c => c.Id).OrderBy(i => i).SequenceEqual(known.OrderBy(i => i));
        }

        int errors = 0;
        var group = cm.CreateCommandGroup2(GroupId, Title, Description, Description, -1, ignorePrevious, ref errors);
        group.IconList = icons.Strips;
        group.MainIconList = icons.Main;
        int menuToolbar = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
        var index = new int[Commands.Length];
        for (int i = 0; i < Commands.Length; i++)
        {
            var c = Commands[i];
            index[i] = group.AddCommandItem2(c.Name, -1, c.Hint, c.Name, i, c.Callback, nameof(EnableForModels), c.Id, menuToolbar);
        }

        group.HasToolbar = true;
        group.HasMenu = true;
        group.Activate();

        var ids = index.Select(i => group.get_CommandID(i)).ToArray();
        _status = $"group errors {errors}, ignorePrevious {ignorePrevious}, command ids [{string.Join(",", ids)}]";
        var text = Enumerable.Repeat((int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow, ids.Length).ToArray();
        foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY })
        {
            var tab = cm.GetCommandTab(docType, TabName);
            if (tab is not null && ignorePrevious)
            {
                cm.RemoveCommandTab(tab);
                tab = null;
            }

            if (tab is null)
            {
                tab = cm.AddCommandTab(docType, TabName);
                var box = tab.AddCommandTabBox();
                bool added = box.AddCommands(ids, text);
                _status += $"; tab {docType} created (commands added {added})";
            }
            else
            {
                _status += $"; tab {docType} existed";
            }
        }
    }

    private void RemoveCommands()
    {
        var cm = _commands;
        if (cm is null) return;
        foreach (int docType in new[] { (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY })
        {
            var tab = cm.GetCommandTab(docType, TabName);
            if (tab is not null) cm.RemoveCommandTab(tab);
        }

        cm.RemoveCommandGroup(GroupId);
    }

    // ------------------------------------------------------------------ callbacks (called by SOLIDWORKS by name)

    /// <summary>Enable state: 1 when a part or an assembly is active, else 0.</summary>
    public int EnableForModels()
    {
        int type = (_sw?.ActiveDoc as IModelDoc2)?.GetType() ?? 0;
        return type == (int)swDocumentTypes_e.swDocPART || type == (int)swDocumentTypes_e.swDocASSEMBLY ? 1 : 0;
    }

    public void OnSimulate() => Guard(() =>
    {
        var doc = ActiveModel();
        string step = JobExporter.ExportActive(doc, JobFolder(doc));
        LaunchApp($"--job \"{step}\"");
    });

    public void OnSendUpdate() => Guard(() =>
    {
        var doc = ActiveModel();
        string step = JobExporter.ExportActive(doc, JobFolder(doc));
        // The app notices the changed file; launching with the job also brings it to the front and re-imports.
        LaunchApp($"--job \"{step}\"");
    });

    public void OnAllConfigurations() => Guard(() =>
    {
        var doc = ActiveModel();
        string folder = JobFolder(doc);
        string active = JobExporter.SafeName(doc.ConfigurationManager.ActiveConfiguration.Name);
        var exported = JobExporter.ExportAllConfigurations(doc, folder);
        if (exported.Count == 0) throw new InvalidOperationException("No configuration could be exported.");
        string template = Path.Combine(folder, active + ".ifea.json");
        LaunchApp(File.Exists(template) ? $"--configs \"{folder}\" --template \"{template}\"" : $"--configs \"{folder}\"");
    });

    public void OnJobFolder() => Guard(() =>
    {
        string folder = JobFolder(ActiveModel());
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    });

    /// <summary>Diagnostics: what the last connection did (CommandManager group and tabs).</summary>
    public string Status() => _status;

    /// <summary>Appends to %LOCALAPPDATA%\ImpressionFEA\SolidWorks\addin.log (load diagnostics; never throws).</summary>
    private static void Log(string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImpressionFEA", "SolidWorks");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "addin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only.
        }
    }

    // ------------------------------------------------------------------ helpers

    private IModelDoc2 ActiveModel()
    {
        if (_sw?.ActiveDoc is IModelDoc2 doc && EnableForModels() == 1) return doc;
        throw new InvalidOperationException("Open a part or an assembly with the bottom die, the workpiece and the top die (or just the two dies).");
    }

    /// <summary>Documents\ImpressionFEA\SolidWorks\&lt;model name&gt; (kept out of the CAD / PDM folders).</summary>
    private static string JobFolder(IModelDoc2 doc)
    {
        string path = doc.GetPathName();
        string name = JobExporter.SafeName(string.IsNullOrEmpty(path) ? doc.GetTitle() : Path.GetFileNameWithoutExtension(path));
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ImpressionFEA", "SolidWorks", name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>ImpressionFEA.App.exe: from the registration (registry), else next to / above this add-in.</summary>
    private static string FindApp()
    {
        // Per user (Register-ImpressionFEA-SolidWorks.ps1), then machine-wide (the installer).
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Marconnet Technologies\ImpressionFEA");
            if (key?.GetValue("AppPath") is string p && File.Exists(p)) return p;
        }

        string here = Path.GetDirectoryName(typeof(AddIn).Assembly.Location) ?? "";
        foreach (var candidate in new[] { Path.Combine(here, "ImpressionFEA.App.exe"), Path.Combine(here, "..", "ImpressionFEA.App.exe") })
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException("ImpressionFEA.App.exe was not found. Reinstall ImpressionFEA with the SOLIDWORKS add-in, or run Register-ImpressionFEA-SolidWorks.ps1 from the ImpressionFEA folder.");
    }

    private static void LaunchApp(string arguments)
    {
        string exe = FindApp();
        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception)
        {
            Message(ex.Message, swMessageBoxIcon_e.swMbWarning);
        }
    }

    private void Message(string text, swMessageBoxIcon_e icon) =>
        _sw?.SendMsgToUser2("ImpressionFEA: " + text, (int)icon, (int)swMessageBoxBtn_e.swMbOk);
}
