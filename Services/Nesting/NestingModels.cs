namespace GcwSheetOptimizer.Services.Nesting;

// ---------------------------------------------------------------------------
// These classes describe the OUTPUT of the optimizer. The whole
// NestingSolution is serialized to JSON and stored in the NestingResult
// table, so keep these as plain data classes (no logic, no EF entities).
//
// Coordinate system (per sheet):
//   - Origin (0,0) is one corner of the sheet.
//   - X runs across the sheet's WIDTH  (the 48" direction, across the grain).
//   - Y runs along  the sheet's LENGTH (the 96" direction, along the grain).
// ---------------------------------------------------------------------------

/// <summary>Settings for one optimizer run.</summary>
public class NestingOptions
{
    /// <summary>
    /// Full sheet width in inches (across the grain). This is the DEFAULT,
    /// used for materials that have no entry in the stock material list -
    /// matched stock materials use their own sheet size instead.
    /// </summary>
    public decimal SheetWidth { get; set; } = 48m;

    /// <summary>Full sheet length in inches (along the grain). Default -
    /// see <see cref="SheetWidth"/> for how stock materials override it.</summary>
    public decimal SheetLength { get; set; } = 96m;

    /// <summary>
    /// Leftover regions smaller than this (in BOTH dimensions) are not
    /// offered as offcuts to save to inventory. Default: 12" x 12".
    /// </summary>
    public decimal MinOffcutWidth { get; set; } = 12m;
    public decimal MinOffcutLength { get; set; } = 12m;
}

/// <summary>The complete result of one optimizer run.</summary>
public class NestingSolution
{
    /// <summary>One entry per distinct material in the cutlist.</summary>
    public List<MaterialNesting> Materials { get; set; } = new();

    /// <summary>Kerf that was used, so the printout can show it.</summary>
    public decimal KerfWidth { get; set; }

    public DateTime GeneratedDate { get; set; } = DateTime.UtcNow;
}

/// <summary>All sheets used for one material (parts never mix materials).</summary>
public class MaterialNesting
{
    public string Material { get; set; } = "";

    /// <summary>
    /// Which strategy combination won the batch for this material
    /// (e.g. "sort by area / best short-side fit / keep big offcut").
    /// Purely informational - shown on the results page.
    /// </summary>
    public string StrategyName { get; set; } = "";

    public List<SheetLayout> Sheets { get; set; } = new();

    /// <summary>Parts that could not be placed (e.g. bigger than a sheet).</summary>
    public List<UnplacedPart> UnplacedParts { get; set; } = new();
}

/// <summary>One physical sheet (full or partial) and everything cut from it.</summary>
public class SheetLayout
{
    /// <summary>1-based number within this material, for the printout.</summary>
    public int SheetNumber { get; set; }

    public decimal SheetWidth { get; set; }
    public decimal SheetLength { get; set; }

    /// <summary>True if this sheet came from the partial-sheet inventory.</summary>
    public bool IsPartialSheet { get; set; }

    /// <summary>Inventory record this sheet came from (if IsPartialSheet).</summary>
    public int? SourcePartialSheetId { get; set; }

    public List<PlacedPart> Parts { get; set; } = new();

    /// <summary>
    /// Unused rectangular regions left over after all cuts.
    /// The ones big enough (see NestingOptions.MinOffcut*) can be
    /// saved back to the partial-sheet inventory.
    /// </summary>
    public List<FreeRegion> Leftovers { get; set; } = new();
}

/// <summary>A part placed at a specific position on a specific sheet.</summary>
public class PlacedPart
{
    /// <summary>Database Id of the Part row this piece came from.</summary>
    public int PartId { get; set; }

    public string? Label { get; set; }

    /// <summary>Position of the part's corner nearest the sheet origin.</summary>
    public decimal X { get; set; }
    public decimal Y { get; set; }

    /// <summary>Size AS PLACED: if Rotated is true these are already swapped,
    /// so Width is always the X extent and Length the Y extent.</summary>
    public decimal Width { get; set; }
    public decimal Length { get; set; }

    /// <summary>True if the part was turned 90 degrees from its cutlist
    /// orientation (only allowed when GrainMatters is false).</summary>
    public bool Rotated { get; set; }
}

/// <summary>An unused rectangular region of a sheet.</summary>
public class FreeRegion
{
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Width { get; set; }
    public decimal Length { get; set; }
}

/// <summary>A part that could not be placed on any sheet, and why.</summary>
public class UnplacedPart
{
    public int PartId { get; set; }
    public string? Label { get; set; }
    public decimal Width { get; set; }
    public decimal Length { get; set; }
    public string Reason { get; set; } = "";
}
