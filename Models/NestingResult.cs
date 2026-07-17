namespace GcwSheetOptimizer.Models;

/// <summary>
/// A saved optimizer run for a project, so layouts can be viewed and
/// reprinted without re-running the optimizer.
///
/// The layout itself (which parts go on which sheet, at what position)
/// is stored as JSON in ResultDataJson rather than as many small database
/// rows - it's only ever read/written as one blob, so JSON keeps it simple.
/// The JSON shape is the NestingSolution class in Services/Nesting.
/// </summary>
public class NestingResult
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Serialized NestingSolution (see Services/Nesting/NestingModels.cs).</summary>
    public string ResultDataJson { get; set; } = "";

    /// <summary>When the optimizer produced this result. Stored in UTC.</summary>
    public DateTime GeneratedDate { get; set; } = DateTime.UtcNow;
}
