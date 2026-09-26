namespace Jiaolong.Core.Fan;

/// <summary>
/// Six-point temperature → RPM fan curve.
///
/// Default RPM preset: [1984, 2189, 2311, 2513, 4311, 5703] RPM at
/// [50, 60, 70, 80, 90, 100] ℃.
/// </summary>
public sealed class FanCurve
{
    public const int PointCount = 6;

    public int[] Temps { get; set; } = { 50, 60, 70, 80, 90, 100 };
    public int[] Rpm { get; set; } = { 1984, 2189, 2311, 2513, 4311, 5703 };

    /// <summary>False when the points are not strictly ascending (temp) or out of the EC range.</summary>
    public bool IsValid => Temps is { Length: PointCount } && Rpm is { Length: PointCount }
        && IsAscending(Temps)
        && Rpm.All(r => r is >= 1800 and <= 5800);

    private static bool IsAscending(int[] v)
    {
        for (int i = 1; i < v.Length; i++)
            if (v[i] <= v[i - 1]) return false;
        return true;
    }

    public FanCurve Clone() => new() { Temps = (int[])Temps.Clone(), Rpm = (int[])Rpm.Clone() };

    /// <summary>Linear interpolation, clamped outside the end points.</summary>
    public int Interpolate(int temp)
    {
        if (!IsValid) return 0;

        if (temp <= Temps[0]) return Rpm[0];
        if (temp >= Temps[^1]) return Rpm[^1];

        for (int i = 1; i < Temps.Length; i++)
        {
            if (temp > Temps[i]) continue;
            double span = Temps[i] - Temps[i - 1];
            if (span <= 0) return Rpm[i];
            double t = (temp - Temps[i - 1]) / span;
            return (int)Math.Round(Rpm[i - 1] + t * (Rpm[i] - Rpm[i - 1]));
        }
        return Rpm[^1];
    }
}
