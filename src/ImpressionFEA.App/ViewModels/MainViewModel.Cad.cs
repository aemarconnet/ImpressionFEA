using System.IO;
using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Jobs sent from CAD (the SolidWorks add-in). A job is a STEP file exported by the add-in; its setup is kept beside it
/// as &lt;name&gt;.ifea.json and saved after every import and solve, so the next export from CAD reruns with the same
/// top die, material and settings.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Setup file of the CAD job currently open (saved automatically), or null.</summary>
    public string? JobSetupPath { get; private set; }

    private static string SetupBeside(string stepPath) => Path.ChangeExtension(stepPath, null) + RunSettings.FileExtension;

    /// <summary>Opens a STEP job from CAD: its saved setup when there is one (re-importing a changed file), else a fresh import.</summary>
    public async Task<string?> OpenJobAsync(string path, Func<ForgingAssembly, CadBody?> chooseTopDie)
    {
        ArgumentNullException.ThrowIfNull(chooseTopDie);
        if (!File.Exists(path)) return "Job file not found: " + path;
        bool isSetup = path.EndsWith(RunSettings.FileExtension, StringComparison.OrdinalIgnoreCase);
        string setup = isSetup ? path : SetupBeside(path);
        string? error;
        if (File.Exists(setup))
        {
            error = await OpenSetupAsync(setup);
            if (error is null && CurrentSource is not null) error = await ReimportAsync(solveAfter: false); // CAD may have changed it
        }
        else
        {
            error = await ImportStepAsync(path, chooseTopDie);
        }

        if (error is not null) return error;
        JobSetupPath = setup;
        SaveJobSetup();
        StatusText = "CAD job " + Path.GetFileNameWithoutExtension(setup.Replace(RunSettings.FileExtension, "")) + " ready. Press Solve (settings are saved with the job).";
        return null;
    }

    /// <summary>Saves the current setup to the open CAD job (relative STEP path, so the folder stays portable).</summary>
    private void SaveJobSetup()
    {
        if (JobSetupPath is null || CurrentSource is null) return;
        // Only while the job's own STEP file is the geometry in use (not after the user opened something else).
        string jobStem = JobSetupPath[..^RunSettings.FileExtension.Length];
        if (!string.Equals(Path.ChangeExtension(CurrentSource.Path, null), jobStem, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            SaveSetup(JobSetupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logQueue.Enqueue("Could not save the job setup: " + ex.Message);
        }
    }

    /// <summary>
    /// Runs every configuration exported by CAD (one STEP per configuration in <paramref name="folder"/>) as a separate
    /// run, labelled with the configuration name, for side-by-side comparison in RUNS. Settings come from each
    /// configuration's saved setup, else from <paramref name="templateSetup"/>, else from the first configuration
    /// imported interactively. Esc / Abort stops the batch.
    /// </summary>
    public async Task<string?> RunConfigurationsAsync(string folder, string? templateSetup, Func<ForgingAssembly, CadBody?> chooseTopDie)
    {
        if (!Directory.Exists(folder)) return "Configuration folder not found: " + folder;
        var steps = Directory.GetFiles(folder, "*.step").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (steps.Count == 0) return "No STEP files in " + folder;

        RunSettings? template = null;
        if (templateSetup is not null && File.Exists(templateSetup))
        {
            template = RunSettings.Load(templateSetup);
        }
        else
        {
            // No saved setup yet: import the first configuration (asks for the top die) and use the current inputs.
            string? err = await OpenJobAsync(steps[0], chooseTopDie);
            if (err is not null) return err;
            template = CaptureSettings();
        }

        int done = 0;
        IsSweeping = true;
        _sweepAbort = false;
        try
        {
            foreach (var step in steps)
            {
                if (_sweepAbort) break;
                string config = Path.GetFileNameWithoutExtension(step);
                string setupPath = SetupBeside(step);
                var s = File.Exists(setupPath) ? RunSettings.Load(setupPath) : template with { StepFile = step };
                SweepText = $"Configurations {done + 1}/{steps.Count}: {config}";
                string? err = await ApplySettingsAsync(s);
                if (err is null && CurrentSource is not null) err = await ReimportAsync(solveAfter: false);
                if (err is not null)
                {
                    _logQueue.Enqueue($"CONFIGURATION {config}: {err}");
                    continue;
                }

                JobSetupPath = setupPath;
                string label = DefaultLabel(CaptureSettings());
                _runLabelOverride = label.StartsWith(config, StringComparison.OrdinalIgnoreCase) ? label : $"{config} · {label}";
                await SolveAsync();
                _runLabelOverride = null;
                done++;
            }
        }
        finally
        {
            _runLabelOverride = null;
            IsSweeping = false;
            SweepText = "";
        }

        if (Runs.Count > 0)
        {
            try
            {
                SaveAllTelemetry(Path.Combine(folder, "results"));
            }
            catch (IOException ex)
            {
                _logQueue.Enqueue("Could not save the configuration results: " + ex.Message);
            }
        }

        StatusText = $"Configurations {(_sweepAbort ? "aborted" : "finished")}: {done} of {steps.Count} solved. Click a run in RUNS to compare; telemetry saved to {Path.Combine(folder, "results")}.";
        return null;
    }
}
