using EchoSub.Desktop;

internal static class OverlayPlacementSmoke
{
    public static CaptionScheduleContractSmoke.Case[] Run()
    {
        int checks = 0; var failures = new List<string>();
        void Check(bool ok, string name) { checks++; if (!ok) failures.Add(name); }
        var primary = new OverlayArea(0, 0, 1920, 1040, 1, true);
        var left = new OverlayArea(-2560, -200, 2560, 1400, 1.5);
        var kept = OverlayPlacements.Recover(100, 100, 760, 220, 1, 420, 180, [primary])!;
        Check(kept.X == 100 && kept.Y == 100 && kept.Width == 760 && kept.Height == 220, "valid placement unchanged");
        var removed = OverlayPlacements.Recover(-2200, 100, 760, 220, 1.5, 420, 180, [primary])!;
        Check(removed.X == 8 && removed.Width == 760, "removed monitor falls back to primary");
        var keptLeft = OverlayPlacements.Recover(-2200, 100, 760, 220, 1.5, 420, 180, [primary, left])!;
        Check(keptLeft.X == -2200 && keptLeft.Width == 760, "negative origin and mixed scaling retained");
        var dpi = OverlayPlacements.Recover(1500, 900, 760, 220, 2, 420, 180, [primary with { Scale = 2 }])!;
        Check(dpi.X + dpi.Width * 2 <= 1912 && dpi.Y + dpi.Height * 2 <= 1032, "higher DPI clamped within work area");
        var tiny = OverlayPlacements.Recover(5000, 5000, 1100, 600, 1, 420, 350, [new(0, 0, 320, 200, 2, true)])!;
        Check(tiny.Width == 152 && tiny.Height == 92 && tiny.MinimumWidth == 152 && tiny.MinimumHeight == 92, "small display can override minimum without leaving window unreachable");
        Check(OverlayPlacements.Recover(0, 0, 760, 220, 1, 420, 180, []) is null, "transient empty screen list makes no move");
        Check(OverlayPlacements.Recover(double.NaN, 0, 760, 220, 1, 420, 180, [primary]) is null, "invalid input rejected");
        Check(OverlayPlacements.Recover(0, 0, 760, 220, 1, 420, 180, [primary with { Scale = 0 }]) is null, "invalid monitor rejected");
        Check(OverlayPlacements.Recover(0, 0, 760, 220, 1, 420, 180, Enumerable.Repeat(primary, 65).ToArray()) is null, "screen count bounded");
        foreach (double scale in new[] { .75, 1, 1.25, 1.5, 2, 3 })
            foreach (int width in new[] { 320, 800, 1920 })
                foreach (int x in new[] { -5000, -1000, 0, 1000, 5000 })
                {
                    var area = new OverlayArea(-1000, -100, width, 600, scale, true);
                    var result = OverlayPlacements.Recover(x, 1500, 1100, 600, 1, 420, 350, [area])!;
                    Check(result.X >= area.X + 8 && result.Y >= area.Y + 8 &&
                        result.X + result.Width * scale <= area.X + area.Width - 8 + .0001 &&
                        result.Y + result.Height * scale <= area.Y + area.Height - 8 + .0001, "recovery stays inside selected work area");
                    var again = OverlayPlacements.Recover(result.X, result.Y, result.Width, result.Height, scale, 420, 350, [area]);
                    Check(again == result, "repeated notification is idempotent");
                }
        return [new("overlay-placement-geometry", checks, failures.ToArray())];
    }
}
