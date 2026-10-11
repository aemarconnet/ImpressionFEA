using System.Windows.Input;
using ImpressionFEA.App.Infrastructure;
using ImpressionFEA.Core.Cad;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.App.ViewModels;

/// <summary>
/// Die-only imports: the workpiece is defined here (shape, size, rotation about the press axis, plan position) and
/// regenerated live; it is always seated on the bottom die and the top die starts at first contact.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<UserBilletShape> WorkpieceShapes { get; } = Enum.GetValues<UserBilletShape>();

    /// <summary>The selected geometry is a die-only import with a user-defined workpiece.</summary>
    public bool IsUserBillet => _analysis3D && _selectedDie3D.UserBillet is not null;

    private UserBillet Wp => _selectedDie3D.UserBillet ?? new UserBillet();

    public UserBilletShape WorkpieceShape { get => Wp.Shape; set => UpdateWorkpiece(Wp with { Shape = value }); }

    public double WorkpieceDiameterU { get => U.L(Wp.Diameter); set => UpdateWorkpiece(Wp with { Diameter = U.LToMm(value) }); }

    public double WorkpieceLengthU { get => U.L(Wp.Length); set => UpdateWorkpiece(Wp with { Length = U.LToMm(value) }); }

    public double WorkpieceWidthU { get => U.L(Wp.Width); set => UpdateWorkpiece(Wp with { Width = U.LToMm(value) }); }

    public double WorkpieceDepthU { get => U.L(Wp.Depth); set => UpdateWorkpiece(Wp with { Depth = U.LToMm(value) }); }

    public double WorkpieceHeightU { get => U.L(Wp.Height); set => UpdateWorkpiece(Wp with { Height = U.LToMm(value) }); }

    public double WorkpieceAngle { get => Wp.AngleDeg; set => UpdateWorkpiece(Wp with { AngleDeg = value }); }

    public double WorkpieceOffsetXU { get => U.L(Wp.OffsetX); set => UpdateWorkpiece(Wp with { OffsetX = U.LToMm(value) }); }

    public double WorkpieceOffsetYU { get => U.L(Wp.OffsetY); set => UpdateWorkpiece(Wp with { OffsetY = U.LToMm(value) }); }

    public bool WorkpieceIsBar => Wp.Shape != UserBilletShape.Block;

    public bool WorkpieceIsBlock => Wp.Shape == UserBilletShape.Block;

    public bool WorkpieceCanRotate => Wp.Shape != UserBilletShape.VerticalBar;

    public string WorkpieceLengthLabel => Wp.Shape == UserBilletShape.VerticalBar ? "Height H" : "Length L";

    public string WorkpieceVolumeText => IsUserBillet
        ? $"Workpiece {U.Vf(Wp.Volume)} · {F($"{(Cavity3D(_selectedDie3D) > 0 ? Wp.Volume / Cavity3D(_selectedDie3D) : 0):P0}")} of the cavity"
        : "";

    /// <summary>Scales the workpiece (length, or block height) to 110 % of the closed cavity volume, the usual flash allowance.</summary>
    public ICommand SizeWorkpieceFromCavityCommand => new RelayCommand(
        () => UpdateWorkpiece(Wp.WithVolume(1.10 * Cavity3D(_selectedDie3D))),
        () => IsUserBillet && CanEdit);

    private static readonly string[] WorkpieceBound =
    [
        nameof(IsUserBillet), nameof(WorkpieceShape), nameof(WorkpieceDiameterU), nameof(WorkpieceLengthU), nameof(WorkpieceWidthU),
        nameof(WorkpieceDepthU), nameof(WorkpieceHeightU), nameof(WorkpieceAngle), nameof(WorkpieceOffsetXU), nameof(WorkpieceOffsetYU),
        nameof(WorkpieceIsBar), nameof(WorkpieceIsBlock), nameof(WorkpieceCanRotate), nameof(WorkpieceLengthLabel), nameof(WorkpieceVolumeText),
    ];

    private void RaiseWorkpiece()
    {
        foreach (var n in WorkpieceBound) OnPropertyChanged(n);
    }

    private void UpdateWorkpiece(UserBillet b)
    {
        if (!IsUserBillet || b == _selectedDie3D.UserBillet) return;
        if (b.Validate().Count > 0)
        {
            RaiseWorkpiece(); // reject: show the previous values again
            return;
        }

        _selectedDie3D.SetUserBillet(b);
        ClearResults();
        RaiseWorkpiece();
        RefreshDerived();
    }

    /// <summary>A starting workpiece for a die-only import: a vertical bar about a third of the impression wide, 110 % of the cavity volume.</summary>
    private static UserBillet DefaultWorkpiece(DieSet3D die)
    {
        double d = Math.Max(5, Math.Round(0.35 * 2 * Math.Sqrt(die.ImpressionArea() / Math.PI)));
        return new UserBillet { Shape = UserBilletShape.VerticalBar, Diameter = d, Length = 50 }.WithVolume(1.10 * die.CavityVolume());
    }
}
