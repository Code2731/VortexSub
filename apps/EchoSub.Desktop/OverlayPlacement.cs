namespace EchoSub.Desktop;

// Coordinates and work areas are physical pixels; window sizes are DIPs.
public sealed record OverlayArea(double X, double Y, double Width, double Height, double Scale, bool Primary = false);
public sealed record OverlayPlacement(double X, double Y, double Width, double Height, double MinimumWidth, double MinimumHeight);
public static class OverlayPlacements
{
    public static OverlayPlacement? Recover(double x, double y, double width, double height, double scale,
        double minimumWidth, double minimumHeight, IReadOnlyList<OverlayArea> areas)
    {
        bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 10_000_000;
        if (!Finite(x) || !Finite(y) || !Finite(width) || !Finite(height) || width <= 0 || height <= 0 ||
            !Finite(scale) || scale <= 0 || !Finite(minimumWidth) || !Finite(minimumHeight) ||
            minimumWidth <= 0 || minimumHeight <= 0 || areas.Count > 64) return null;
        var valid = areas.Where(a => Finite(a.X) && Finite(a.Y) && Finite(a.Width) && Finite(a.Height) &&
            a.Width >= 32 && a.Height >= 32 && Finite(a.Scale) && a.Scale > 0 && a.Scale <= 16).ToArray();
        if (valid.Length == 0) return null;
        double Overlap(OverlayArea a) => Math.Max(0, Math.Min(x + width * scale, a.X + a.Width) - Math.Max(x, a.X)) *
            Math.Max(0, Math.Min(y + height * scale, a.Y + a.Height) - Math.Max(y, a.Y));
        var target = valid.OrderByDescending(Overlap).ThenByDescending(a => a.Primary).First();
        if (Overlap(target) == 0) target = valid.FirstOrDefault(a => a.Primary) ?? valid[0];
        const double margin = 8;
        double maxWidth = (target.Width - 2 * margin) / target.Scale;
        double maxHeight = (target.Height - 2 * margin) / target.Scale;
        double minWidth = Math.Min(minimumWidth, maxWidth), minHeight = Math.Min(minimumHeight, maxHeight);
        double recoveredWidth = Math.Clamp(width, minWidth, maxWidth), recoveredHeight = Math.Clamp(height, minHeight, maxHeight);
        return new(Math.Clamp(x, target.X + margin, Math.Max(target.X + margin, target.X + target.Width - margin - recoveredWidth * target.Scale)),
            Math.Clamp(y, target.Y + margin, Math.Max(target.Y + margin, target.Y + target.Height - margin - recoveredHeight * target.Scale)),
            recoveredWidth, recoveredHeight, minWidth, minHeight);
    }
}
