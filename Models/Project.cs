namespace GcwSheetOptimizer.Models;

/// <summary>
/// A "project" is one cutlist job (e.g. "Kitchen cabinets").
/// It owns a list of parts and remembers its own kerf setting,
/// so you can save a job and reopen it later.
/// </summary>
public class Project
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>When the project was created. Stored in UTC.</summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Saw blade thickness in inches. This much material is lost on every cut,
    /// so the optimizer leaves this gap between adjacent parts.
    /// 0.125 = 1/8", which is typical for a table saw blade.
    /// </summary>
    public decimal KerfWidth { get; set; } = 0.125m;

    // --- Navigation properties (EF Core fills these in) ---

    /// <summary>All cutlist rows belonging to this project.</summary>
    public List<Part> Parts { get; set; } = new();

    /// <summary>Saved optimizer runs, so layouts can be reprinted later.</summary>
    public List<NestingResult> NestingResults { get; set; } = new();
}
