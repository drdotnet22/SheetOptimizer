namespace GcwSheetOptimizer.Models;

/// <summary>
/// One row of the cutlist: "I need {Quantity} pieces of {Width} x {Length}
/// out of {Material}".
///
/// Dimension convention used throughout the app:
///   - A full sheet is 48" wide x 96" long.
///   - "Length" is the dimension that runs ALONG the grain (the 96" direction).
///   - "Width" runs across the grain (the 48" direction).
/// If GrainMatters is true, the optimizer keeps the part's Length aligned
/// with the sheet's length and will never rotate it 90 degrees.
/// </summary>
public class Part
{
    public int Id { get; set; }

    /// <summary>How many identical copies of this part are needed.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Width in inches (across the grain).</summary>
    public decimal Width { get; set; }

    /// <summary>Length in inches (along the grain).</summary>
    public decimal Length { get; set; }

    /// <summary>Material name, e.g. "3/4 Birch Plywood". Parts are only
    /// nested together with other parts of the exact same material.</summary>
    public string Material { get; set; } = "";

    /// <summary>If true, the part may NOT be rotated 90 degrees.</summary>
    public bool GrainMatters { get; set; }

    /// <summary>Optional friendly name, e.g. "Cabinet Side".</summary>
    public string? Label { get; set; }

    // --- Relationship to Project ---
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
}
