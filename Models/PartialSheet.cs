namespace GcwSheetOptimizer.Models;

/// <summary>
/// A leftover piece of sheet material in your shop.
/// The optimizer checks this inventory before reaching for a new full sheet.
///
/// Same dimension convention as Part: "Length" is along the grain.
/// (We assume offcuts keep the grain direction of the sheet they came from.)
/// </summary>
public class PartialSheet
{
    public int Id { get; set; }

    /// <summary>Width in inches (across the grain).</summary>
    public decimal Width { get; set; }

    /// <summary>Length in inches (along the grain).</summary>
    public decimal Length { get; set; }

    public string Material { get; set; } = "";

    /// <summary>How many identical pieces of this size you have.</summary>
    public int Quantity { get; set; } = 1;

    public string? Notes { get; set; }

    /// <summary>When this piece was added to inventory. Stored in UTC.</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// If this offcut came from a cut job, which project produced it.
    /// Nullable because manually-added pieces have no source project.
    /// If the source project is deleted, this becomes null (the offcut
    /// itself is kept - it still exists in your shop!).
    /// </summary>
    public int? SourceProjectId { get; set; }
    public Project? SourceProject { get; set; }
}
