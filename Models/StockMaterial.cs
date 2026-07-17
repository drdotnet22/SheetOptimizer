namespace GcwSheetOptimizer.Models;

/// <summary>
/// A standard stock material you buy - e.g. "3/4 Birch Plywood" - with the
/// actual sheet size it comes in. Some plywood is oversized (48.5" x 96.5"),
/// some is exactly 48" x 96", so the optimizer needs to know per material.
///
/// Matching rule: when the optimizer processes a material group from the
/// cutlist, it looks for a stock material whose Name matches the part's
/// Material EXACTLY (ignoring case and surrounding spaces). If there is no
/// match, the default 48" x 96" sheet size is used.
/// </summary>
public class StockMaterial
{
    public int Id { get; set; }

    /// <summary>
    /// The material name - must match the Material text used on cutlist
    /// parts exactly (case-insensitive) for the size to apply.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>Sheet width in inches (across the grain).</summary>
    public decimal SheetWidth { get; set; } = 48m;

    /// <summary>Sheet length in inches (along the grain).</summary>
    public decimal SheetLength { get; set; } = 96m;

    public string? Notes { get; set; }
}
