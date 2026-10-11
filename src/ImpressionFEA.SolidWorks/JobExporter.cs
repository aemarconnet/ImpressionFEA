using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ImpressionFEA.SolidWorks;

/// <summary>Exports SOLIDWORKS models to STEP for ImpressionFEA: one file per configuration, named after it.</summary>
internal static class JobExporter
{
    /// <summary>Exports the active configuration to &lt;folder&gt;\&lt;configuration&gt;.step and returns the path.</summary>
    public static string ExportActive(IModelDoc2 doc, string folder)
    {
        var config = doc.ConfigurationManager.ActiveConfiguration;
        string path = Path.Combine(folder, SafeName(config.Name) + ".step");
        Export(doc, path);
        return path;
    }

    /// <summary>Exports every configuration (switching to each and back) and returns the STEP paths.</summary>
    public static IReadOnlyList<string> ExportAllConfigurations(IModelDoc2 doc, string folder)
    {
        var names = (doc.GetConfigurationNames() as string[]) ?? [];
        string active = doc.ConfigurationManager.ActiveConfiguration.Name;
        var paths = new List<string>();
        try
        {
            foreach (var name in names)
            {
                if (!doc.ShowConfiguration2(name)) continue;
                string path = Path.Combine(folder, SafeName(name) + ".step");
                Export(doc, path);
                paths.Add(path);
            }
        }
        finally
        {
            doc.ShowConfiguration2(active);
        }

        // Remove STEP files of configurations that no longer exist, so the batch matches the model.
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Directory.GetFiles(folder, "*.step").Where(f => !keep.Contains(f))) File.Delete(stale);
        return paths;
    }

    private static void Export(IModelDoc2 doc, string path)
    {
        int errors = 0, warnings = 0;
        bool ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref errors, ref warnings);
        if (!ok || !File.Exists(path)) throw new IOException($"STEP export failed (SOLIDWORKS error {errors}, warning {warnings}): {path}");
    }

    /// <summary>File-name-safe version of a model or configuration name.</summary>
    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "Default" : s;
    }
}
