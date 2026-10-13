using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Simulation;
using OxyPlot.Axes;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Display units. Inputs bind to the *U twins (shown in the current unit system); the model keeps mm, MPa, °C and
/// metric tonnes internally, so setups, the solver and the CSV files are unit-independent.
/// </summary>
public sealed partial class MainViewModel
{
    private static UnitSet U => Units.Current;

    public IReadOnlyList<UnitSystem> UnitOptions { get; } = Enum.GetValues<UnitSystem>();

    public UnitSystem SelectedUnits
    {
        get => U.System;
        set => Units.Set(value);
    }

    private void InitUnits() => Units.Changed += (_, _) => OnUnitsChanged();

    // ---------------------------------------------------------------- inputs in display units

    public double BilletRadiusU { get => U.L(_billetRadius); set => BilletRadius = U.LToMm(value); }

    public double BilletHeightU { get => U.L(_billetHeight); set => BilletHeight = U.LToMm(value); }

    public double BilletLengthU { get => U.L(_billetLength); set => BilletLength = U.LToMm(value); }

    public double ElementSizeU { get => U.L(_elementSize); set => ElementSize = U.LToMm(value); }

    public double StrokeIncrementU { get => U.L(_strokeIncrement); set => StrokeIncrement = U.LToMm(value); }

    public double RamSpeedU { get => U.L(_ramSpeed); set => RamSpeed = U.LToMm(value); }

    public double TemperatureU { get => U.T(_temperature); set => Temperature = U.TToCelsius(value); }

    public double PressCapacityU { get => U.LoadFromTonnes(_pressCapacity); set => PressCapacity = U.LoadToTonnes(value); }

    // ---------------------------------------------------------------- unit labels and formats for the view

    public string LengthUnit => U.Length;

    public string SpeedUnit => U.Speed;

    public string TemperatureUnit => U.Temperature;

    public string LoadUnit => U.Load;

    public string LengthFormat => U.LengthFormat;

    public string FineLengthFormat => U.FineLengthFormat;

    public string LoadInputFormat => U.System == UnitSystem.US ? "F1" : "F0";

    /// <summary>Divide internal field values by this for display (stress fields: MPa → ksi).</summary>
    public double FieldLabelFactor => IsStress(_selectedField.Field) ? U.MpaPerStress : 1;

    /// <summary>Decimals of colour-bar labels (stress fields follow the unit system; others are adaptive = −1).</summary>
    public int FieldLabelDecimals => IsStress(_selectedField.Field) ? U.StressDecimals : -1;

    public double VelocityUnitMm => U.MmPerLength;

    public string VelocityUnitName => U.Length;

    private static bool IsStress(ResultField f) => f is ResultField.VonMisesStress or ResultField.MeanStress;

    /// <summary>Unit of a result field in the current system.</summary>
    public static string DisplayUnit(ResultField f) => f switch
    {
        ResultField.VonMisesStress or ResultField.MeanStress => U.Stress,
        ResultField.EffectiveStrainRate => "1/s",
        _ => "–",
    };

    private static readonly string[] UnitBound =
    [
        nameof(SelectedUnits), nameof(BilletRadiusU), nameof(BilletHeightU), nameof(BilletLengthU), nameof(ElementSizeU),
        nameof(StrokeIncrementU), nameof(RamSpeedU), nameof(TemperatureU), nameof(PressCapacityU), nameof(LengthUnit),
        nameof(SpeedUnit), nameof(TemperatureUnit), nameof(LoadUnit), nameof(LengthFormat), nameof(FineLengthFormat),
        nameof(LoadInputFormat), nameof(FieldLabelFactor), nameof(FieldLabelDecimals), nameof(VelocityUnitMm),
        nameof(VelocityUnitName), nameof(FieldTitle),
    ];

    private void OnUnitsChanged()
    {
        foreach (var n in UnitBound) OnPropertyChanged(n);
        RaiseWorkpiece();
        foreach (var a in LoadPlot.Axes)
        {
            if (a.Key == "stroke") a.Title = StrokeAxisTitle;
            else if (a.Key == "load") a.Title = "Press load (" + U.Load + ")";
        }

        foreach (var r in Runs) r.ApplyUnits();
        RebuildSeries();
        UpdateCursor();
        UpdateReadouts();
        RefreshDerived();
        LoadPlot.InvalidatePlot(true);
    }
}
