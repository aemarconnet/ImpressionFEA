using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Solver;

namespace ImpressionFEA.Core.Tests;

public class NumericsTests
{
    [Fact]
    public void BandCholeskyMatchesDenseSolution()
    {
        const int n = 40, bw = 5;
        var rng = new Random(7);
        var dense = new double[n, n];
        var band = new SymmetricBandMatrix(n, bw);
        for (int i = 0; i < n; i++)
        {
            for (int j = Math.Max(0, i - bw); j < i; j++)
            {
                double v = rng.NextDouble() - 0.5;
                dense[i, j] = dense[j, i] = v;
                band.Add(i, j, v);
            }

            dense[i, i] = 4 + rng.NextDouble();
            band.Add(i, i, dense[i, i]);
        }

        var x = Enumerable.Range(0, n).Select(i => Math.Sin(i)).ToArray();
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) b[i] += dense[i, j] * x[j];
        }

        band.Factorize();
        band.Solve(b);
        for (int i = 0; i < n; i++) Assert.Equal(x[i], b[i], 10);
    }

    [Fact]
    public void NonPositiveDefiniteMatrixIsReported()
    {
        var m = new SymmetricBandMatrix(3, 1);
        m.Add(0, 0, 1);
        m.Add(1, 1, -1);
        m.Add(2, 2, 1);
        Assert.Throws<MatrixNotPositiveDefiniteException>(m.Factorize);
    }

    [Fact]
    public void DieProjectionAndClearance()
    {
        var die = new DieSurface(DieSide.Bottom, [new Vec2(0, -10), new Vec2(10, -10), new Vec2(11, 0), new Vec2(30, 0)]);
        Assert.Equal(-10, die.HeightAt(5), 12);
        Assert.Equal(-5, die.HeightAt(10.5), 12);
        Assert.True(die.Clearance(new Vec2(5, -9), 0) > 0);
        Assert.True(die.Clearance(new Vec2(5, -11), 0) < 0);
        var q = die.Project(new Vec2(20, -0.2), 0, out var nrm, out double d);
        Assert.Equal(0, q.Y, 12);
        Assert.Equal(0.2, d, 12);
        Assert.Equal(1, nrm.Y, 12);
    }

    [Fact]
    public void DieProfileRejectsVerticalWalls() =>
        Assert.Throws<ArgumentException>(() => new DieSurface(DieSide.Bottom, [new Vec2(0, 0), new Vec2(5, 0), new Vec2(5, 5)]));

    [Fact]
    public void FilletKeepsProfileMonotonic()
    {
        foreach (var die in DiePresets.All)
        {
            Assert.All(new[] { die.Bottom, die.TopClosed }, s =>
            {
                for (int i = 1; i < s.Points.Count; i++) Assert.True(s.Points[i].X > s.Points[i - 1].X);
            });
        }
    }
}
