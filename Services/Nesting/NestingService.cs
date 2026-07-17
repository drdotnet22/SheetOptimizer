using GcwSheetOptimizer.Models;

namespace GcwSheetOptimizer.Services.Nesting;

/// <summary>
/// The guillotine nesting optimizer.
///
/// HOW IT WORKS (overview)
/// -----------------------
/// The core is a classic "guillotine free-rectangle" bin-packing heuristic:
///
///  1. Parts are grouped by material (parts never share a sheet with a
///     different material).
///  2. Every sheet keeps a list of empty rectangles ("free rects"). A new
///     sheet starts with one free rect covering the whole sheet.
///  3. Each part is placed into the best free rect across all open sheets,
///     trying both orientations when GrainMatters is false.
///  4. After placing a part in a free rect's corner, the remaining L-shaped
///     space is split into TWO smaller rectangles with one straight cut.
///     Because every split is a single edge-to-edge cut, every part can be
///     freed from the sheet with straight, full-length cuts - exactly what
///     a table saw / panel saw can do. This is the "guillotine constraint",
///     and it holds by construction.
///  5. The saw blade's kerf is added to the part's footprint when splitting,
///     so neighbouring parts are spaced one blade-width apart.
///  6. When a part fits on no open sheet, a new sheet is opened. The
///     partial-sheet inventory is checked FIRST: among partial sheets of
///     the right material that can hold the part, the SMALLEST one is used
///     (best-fit - this preserves your big leftovers for big parts).
///     Only if no partial sheet fits do we open a fresh 48x96 sheet.
///
/// THE BATCH ("try many strategies, keep the winner")
/// --------------------------------------------------
/// A single greedy heuristic is fast but short-sighted: its choices early
/// on can force an extra sheet later, and no single rule wins on every
/// cutlist. So instead of one run, the optimizer does a BATCH of runs per
/// material - every combination of:
///
///   - 4 part sort orders   (by area / longest side / shortest side / perimeter)
///   - 3 placement rules    (best area fit / best short-side fit / best long-side fit)
///   - 2 split rules        (keep the bigger leftover whole / keep the smaller one whole)
///   - with and without the partial-sheet inventory (when inventory exists)
///
/// ...up to 48 candidate layouts. Runs are independent, so this is still
/// effectively instant for cutlists of a few hundred parts.
///
/// The winner is chosen by comparing, in order (earlier rules win ties):
///   1. fewest parts that failed to fit
///   2. fewest FULL sheets consumed          (full sheets cost money)
///   3. least total sheet area consumed      (prefers using small partials)
///   4. least "small scrap" area             (leftovers too small to reuse,
///                                            i.e. under the min offcut size)
///   5. biggest single leftover piece        (waste consolidated into one
///                                            useful offcut, not confetti)
///
/// Rules 4 and 5 are what "minimize small leftover pieces" means in code:
/// between two layouts that use the same sheets, we prefer the one whose
/// waste is gathered into large, reusable pieces.
///
/// KNOWN LIMITATIONS
/// -----------------
/// Each individual run is still greedy (no backtracking), so the batch
/// winner is not guaranteed to be the true optimum - just the best of 48
/// good attempts, which in practice is close.
/// </summary>
public class NestingService
{
    // =======================================================================
    // Strategy knobs - each combination of these is one candidate run.
    // =======================================================================

    /// <summary>In what order do we try to place the parts?</summary>
    private enum PartSortRule
    {
        AreaDesc,           // biggest area first (the classic default)
        LongestSideDesc,    // longest single edge first (helps long thin parts)
        ShortestSideDesc,   // widest "short edge" first (helps chunky parts)
        PerimeterDesc,      // biggest perimeter first (a blend of the above)
    }

    /// <summary>Among all free rects a part fits in, which one do we pick?</summary>
    private enum PlacementRule
    {
        BestAreaFit,        // rect that wastes the least area
        BestShortSideFit,   // rect where the tightest dimension is tightest
        BestLongSideFit,    // rect where the loosest dimension is tightest
    }

    /// <summary>After placing a part, how do we split the leftover L-shape?</summary>
    private enum SplitRule
    {
        KeepBiggerLeftoverWhole,  // the larger leftover stays one piece
        KeepSmallerLeftoverWhole, // the smaller leftover stays one piece
    }

    /// <summary>One full strategy = one candidate run of the optimizer.</summary>
    private sealed record Strategy(
        PartSortRule Sort,
        PlacementRule Placement,
        SplitRule Split,
        bool UsePartialSheets)
    {
        public string DisplayName =>
            $"{SortName} / {PlacementName} / {SplitName}" +
            (UsePartialSheets ? "" : " / ignoring partials");

        private string SortName => Sort switch
        {
            PartSortRule.AreaDesc => "sort by area",
            PartSortRule.LongestSideDesc => "sort by longest side",
            PartSortRule.ShortestSideDesc => "sort by shortest side",
            _ => "sort by perimeter",
        };

        private string PlacementName => Placement switch
        {
            PlacementRule.BestAreaFit => "best area fit",
            PlacementRule.BestShortSideFit => "best short-side fit",
            _ => "best long-side fit",
        };

        private string SplitName => Split switch
        {
            SplitRule.KeepBiggerLeftoverWhole => "keep big offcut",
            _ => "keep small offcut",
        };
    }

    // =======================================================================
    // Public entry point
    // =======================================================================

    /// <summary>
    /// Runs the optimizer (the whole strategy batch) and returns the best
    /// layout found for each material.
    /// </summary>
    /// <param name="parts">The cutlist rows to nest.</param>
    /// <param name="kerfWidth">Saw blade thickness in inches.</param>
    /// <param name="partialSheetInventory">
    /// Available leftover sheets. This list is NOT modified - the result
    /// reports which partial sheets were used, and the UI asks the user
    /// to confirm consuming them.
    /// </param>
    /// <param name="options">Sheet size and offcut settings.</param>
    public NestingSolution Nest(
        List<Part> parts,
        decimal kerfWidth,
        List<PartialSheet> partialSheetInventory,
        NestingOptions options)
    {
        var solution = new NestingSolution
        {
            KerfWidth = kerfWidth,
            GeneratedDate = DateTime.UtcNow,
        };

        var materialGroups = parts
            .Where(p => p.Quantity > 0)
            .GroupBy(p => p.Material.Trim())
            .OrderBy(g => g.Key);

        foreach (var group in materialGroups)
        {
            // Does this material even have partial sheets in inventory?
            // If not, the "ignore partials" variants would be identical
            // runs, so we skip them.
            var hasPartials = partialSheetInventory.Any(ps =>
                ps.Material.Trim().Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                && ps.Quantity > 0 && ps.Width > 0 && ps.Length > 0);

            MaterialNesting? bestLayout = null;
            LayoutScore? bestScore = null;

            // --- THE BATCH: try every strategy, keep the best result. ---
            foreach (var strategy in AllStrategies(hasPartials))
            {
                var candidate = NestOneMaterial(
                    group.Key, group.ToList(), kerfWidth,
                    partialSheetInventory, options, strategy);

                var score = LayoutScore.Of(candidate, options);

                if (bestScore is null || score.IsBetterThan(bestScore))
                {
                    bestScore = score;
                    bestLayout = candidate;
                }
            }

            solution.Materials.Add(bestLayout!); // batch always has >= 1 run
        }

        return solution;
    }

    /// <summary>Every strategy combination the batch will try.</summary>
    private static IEnumerable<Strategy> AllStrategies(bool includeNoPartialsVariants)
    {
        foreach (var sort in Enum.GetValues<PartSortRule>())
            foreach (var placement in Enum.GetValues<PlacementRule>())
                foreach (var split in Enum.GetValues<SplitRule>())
                {
                    yield return new Strategy(sort, placement, split, UsePartialSheets: true);

                    // Occasionally squeezing parts onto small partial sheets
                    // fragments the layout and costs a full sheet overall, so
                    // also try skipping the inventory entirely.
                    if (includeNoPartialsVariants)
                        yield return new Strategy(sort, placement, split, UsePartialSheets: false);
                }
    }

    // =======================================================================
    // Scoring: how we decide which candidate layout "wins".
    // =======================================================================

    private sealed class LayoutScore
    {
        public int UnplacedCount;        // lower is better
        public int FullSheets;           // lower is better
        public decimal SheetAreaUsed;    // lower is better
        public decimal SmallScrapArea;   // lower is better
        public decimal LargestLeftover;  // HIGHER is better

        public static LayoutScore Of(MaterialNesting layout, NestingOptions options)
        {
            var score = new LayoutScore { UnplacedCount = layout.UnplacedParts.Count };

            foreach (var sheet in layout.Sheets)
            {
                if (!sheet.IsPartialSheet) score.FullSheets++;
                score.SheetAreaUsed += sheet.SheetWidth * sheet.SheetLength;

                foreach (var leftover in sheet.Leftovers)
                {
                    var area = leftover.Width * leftover.Length;
                    var reusable = leftover.Width >= options.MinOffcutWidth
                                && leftover.Length >= options.MinOffcutLength;

                    if (!reusable) score.SmallScrapArea += area;
                    if (area > score.LargestLeftover) score.LargestLeftover = area;
                }
            }
            return score;
        }

        /// <summary>Lexicographic comparison - earlier criteria win ties.</summary>
        public bool IsBetterThan(LayoutScore other)
        {
            if (UnplacedCount != other.UnplacedCount) return UnplacedCount < other.UnplacedCount;
            if (FullSheets != other.FullSheets) return FullSheets < other.FullSheets;
            if (SheetAreaUsed != other.SheetAreaUsed) return SheetAreaUsed < other.SheetAreaUsed;
            if (SmallScrapArea != other.SmallScrapArea) return SmallScrapArea < other.SmallScrapArea;
            return LargestLeftover > other.LargestLeftover;
        }
    }

    // =======================================================================
    // A single run for a single material with a fixed strategy.
    // =======================================================================

    /// <summary>A rectangle of empty space on a sheet (internal bookkeeping).</summary>
    private sealed class FreeRect
    {
        public decimal X;
        public decimal Y;
        public decimal W;   // extent in X (across sheet width)
        public decimal H;   // extent in Y (along sheet length)
    }

    /// <summary>A sheet currently being filled (internal bookkeeping).</summary>
    private sealed class OpenSheet
    {
        public required SheetLayout Layout;
        public List<FreeRect> FreeRects = new();
    }

    /// <summary>One physical piece to cut (a Part row expanded by Quantity).</summary>
    private sealed class PieceToPlace
    {
        public int PartId;
        public string? Label;
        public decimal Width;       // across grain
        public decimal Length;      // along grain
        public bool GrainMatters;
    }

    /// <summary>Where the best placement so far was found.</summary>
    private sealed class Candidate
    {
        public required OpenSheet Sheet;
        public required FreeRect Rect;
        public decimal PlacedW;     // X extent as placed
        public decimal PlacedH;     // Y extent as placed
        public bool Rotated;
        public decimal Score1;      // primary fit score (lower = better)
        public decimal Score2;      // tie-breaker (lower = better)
    }

    private sealed class PartialStock
    {
        public required PartialSheet Sheet;
        public int Remaining;
    }

    private static MaterialNesting NestOneMaterial(
        string material,
        List<Part> parts,
        decimal kerf,
        List<PartialSheet> inventory,
        NestingOptions options,
        Strategy strategy)
    {
        var result = new MaterialNesting
        {
            Material = material,
            StrategyName = strategy.DisplayName,
        };

        // --- Expand quantities into individual pieces ------------------------
        var pieces = new List<PieceToPlace>();
        foreach (var part in parts)
        {
            // Guard against bad data - never let it crash the optimizer.
            if (part.Width <= 0 || part.Length <= 0)
            {
                result.UnplacedParts.Add(new UnplacedPart
                {
                    PartId = part.Id,
                    Label = part.Label,
                    Width = part.Width,
                    Length = part.Length,
                    Reason = "Invalid dimensions (width and length must be greater than zero).",
                });
                continue;
            }

            for (var i = 0; i < part.Quantity; i++)
            {
                pieces.Add(new PieceToPlace
                {
                    PartId = part.Id,
                    Label = part.Label,
                    Width = part.Width,
                    Length = part.Length,
                    GrainMatters = part.GrainMatters,
                });
            }
        }

        // --- Sort according to this run's strategy ---------------------------
        pieces = SortPieces(pieces, strategy.Sort);

        // --- Track how many of each partial sheet are still available --------
        var partialStock = strategy.UsePartialSheets
            ? inventory
                .Where(ps => ps.Material.Trim().Equals(material, StringComparison.OrdinalIgnoreCase)
                             && ps.Quantity > 0
                             && ps.Width > 0 && ps.Length > 0)
                .Select(ps => new PartialStock { Sheet = ps, Remaining = ps.Quantity })
                .ToList()
            : new List<PartialStock>();

        var openSheets = new List<OpenSheet>();

        // --- Main loop: place each piece --------------------------------------
        foreach (var piece in pieces)
        {
            // 1) Try to fit it on a sheet that is already open.
            var best = FindBestPlacement(openSheets, piece, strategy.Placement);

            // 2) No room anywhere? Open a new sheet (partial inventory first).
            if (best is null)
            {
                var newSheet = OpenNewSheet(piece, partialStock, options, openSheets.Count + 1);
                if (newSheet is null)
                {
                    // The part doesn't even fit on a fresh full sheet.
                    result.UnplacedParts.Add(new UnplacedPart
                    {
                        PartId = piece.PartId,
                        Label = piece.Label,
                        Width = piece.Width,
                        Length = piece.Length,
                        Reason = $"Too large for a {options.SheetWidth}\" x {options.SheetLength}\" sheet" +
                                 (piece.GrainMatters ? " (rotation not allowed because grain matters)." : "."),
                    });
                    continue;
                }

                openSheets.Add(newSheet);
                best = FindBestPlacement(new List<OpenSheet> { newSheet }, piece, strategy.Placement);

                if (best is null)
                {
                    // Should never happen (OpenNewSheet already checked fit),
                    // but never crash - report instead.
                    result.UnplacedParts.Add(new UnplacedPart
                    {
                        PartId = piece.PartId,
                        Label = piece.Label,
                        Width = piece.Width,
                        Length = piece.Length,
                        Reason = "Internal error: could not place on a freshly opened sheet.",
                    });
                    continue;
                }
            }

            // 3) Commit the placement.
            PlacePiece(best, piece, kerf, strategy.Split);
        }

        // --- Collect leftovers --------------------------------------------------
        foreach (var sheet in openSheets)
        {
            foreach (var rect in sheet.FreeRects)
            {
                // Ignore slivers thinner than 1/4" - they are sawdust-adjacent.
                if (rect.W < 0.25m || rect.H < 0.25m) continue;

                sheet.Layout.Leftovers.Add(new FreeRegion
                {
                    X = rect.X,
                    Y = rect.Y,
                    Width = rect.W,
                    Length = rect.H,
                });
            }
            result.Sheets.Add(sheet.Layout);
        }

        return result;
    }

    private static List<PieceToPlace> SortPieces(List<PieceToPlace> pieces, PartSortRule rule)
    {
        return rule switch
        {
            PartSortRule.AreaDesc => pieces
                .OrderByDescending(p => p.Width * p.Length)
                .ThenByDescending(p => Math.Max(p.Width, p.Length))
                .ToList(),

            PartSortRule.LongestSideDesc => pieces
                .OrderByDescending(p => Math.Max(p.Width, p.Length))
                .ThenByDescending(p => p.Width * p.Length)
                .ToList(),

            PartSortRule.ShortestSideDesc => pieces
                .OrderByDescending(p => Math.Min(p.Width, p.Length))
                .ThenByDescending(p => p.Width * p.Length)
                .ToList(),

            _ => pieces // PerimeterDesc
                .OrderByDescending(p => p.Width + p.Length)
                .ThenByDescending(p => p.Width * p.Length)
                .ToList(),
        };
    }

    /// <summary>
    /// Searches every free rectangle on every open sheet for the placement
    /// this run's placement rule likes best. Returns null if the piece
    /// fits nowhere.
    /// </summary>
    private static Candidate? FindBestPlacement(
        List<OpenSheet> sheets, PieceToPlace piece, PlacementRule rule)
    {
        Candidate? best = null;

        foreach (var sheet in sheets)
        {
            foreach (var rect in sheet.FreeRects)
            {
                // Orientation 1: as drawn (Width across, Length along grain).
                TryOrientation(sheet, rect, piece.Width, piece.Length, rotated: false, rule, ref best);

                // Orientation 2: rotated 90 degrees - only if grain allows it,
                // and skip for squares (rotating a square changes nothing).
                if (!piece.GrainMatters && piece.Width != piece.Length)
                {
                    TryOrientation(sheet, rect, piece.Length, piece.Width, rotated: true, rule, ref best);
                }
            }
        }

        return best;
    }

    private static void TryOrientation(
        OpenSheet sheet, FreeRect rect,
        decimal placedW, decimal placedH, bool rotated,
        PlacementRule rule,
        ref Candidate? best)
    {
        // Does it fit at all? (The kerf is NOT required here: a part may
        // touch the edge of its free rect - the kerf is applied between
        // parts when the rect is split, see PlacePiece.)
        if (placedW > rect.W || placedH > rect.H) return;

        var wastedArea = rect.W * rect.H - placedW * placedH;
        var shortSide = Math.Min(rect.W - placedW, rect.H - placedH);
        var longSide = Math.Max(rect.W - placedW, rect.H - placedH);

        // Each placement rule ranks candidate rects by a different measure.
        var (score1, score2) = rule switch
        {
            PlacementRule.BestAreaFit => (wastedArea, shortSide),
            PlacementRule.BestShortSideFit => (shortSide, longSide),
            _ => (longSide, shortSide), // BestLongSideFit
        };

        if (best is null
            || score1 < best.Score1
            || (score1 == best.Score1 && score2 < best.Score2))
        {
            best = new Candidate
            {
                Sheet = sheet,
                Rect = rect,
                PlacedW = placedW,
                PlacedH = placedH,
                Rotated = rotated,
                Score1 = score1,
                Score2 = score2,
            };
        }
    }

    /// <summary>
    /// Opens a new sheet for a piece that fits nowhere. Checks the partial
    /// sheet inventory first (smallest partial that fits = best-fit), then
    /// falls back to a fresh full sheet. Returns null if the piece doesn't
    /// fit even on a full sheet.
    /// </summary>
    private static OpenSheet? OpenNewSheet(
        PieceToPlace piece,
        List<PartialStock> partialStock,
        NestingOptions options,
        int nextSheetNumber)
    {
        // Can this piece fit on a sheet of size (w x l)?
        bool Fits(decimal sheetW, decimal sheetL) =>
            (piece.Width <= sheetW && piece.Length <= sheetL) ||
            (!piece.GrainMatters && piece.Length <= sheetW && piece.Width <= sheetL);

        // --- Try partial sheets first (BEST-FIT: smallest usable one) ------
        var candidate = partialStock
            .Where(s => s.Remaining > 0 && Fits(s.Sheet.Width, s.Sheet.Length))
            .OrderBy(s => s.Sheet.Width * s.Sheet.Length)
            .FirstOrDefault();

        if (candidate is not null)
        {
            candidate.Remaining--; // reserve one piece of this stock for the run
            return MakeOpenSheet(
                candidate.Sheet.Width, candidate.Sheet.Length,
                isPartial: true, sourceId: candidate.Sheet.Id, nextSheetNumber);
        }

        // --- Fall back to a fresh full sheet --------------------------------
        if (Fits(options.SheetWidth, options.SheetLength))
        {
            return MakeOpenSheet(
                options.SheetWidth, options.SheetLength,
                isPartial: false, sourceId: null, nextSheetNumber);
        }

        return null; // piece is simply too big
    }

    private static OpenSheet MakeOpenSheet(
        decimal width, decimal length, bool isPartial, int? sourceId, int sheetNumber)
    {
        return new OpenSheet
        {
            Layout = new SheetLayout
            {
                SheetNumber = sheetNumber,
                SheetWidth = width,
                SheetLength = length,
                IsPartialSheet = isPartial,
                SourcePartialSheetId = sourceId,
            },
            FreeRects =
            {
                // A new sheet is one big free rectangle.
                new FreeRect { X = 0, Y = 0, W = width, H = length },
            },
        };
    }

    /// <summary>
    /// Records the part on the sheet layout and splits the free rectangle
    /// it was placed in (this is where the guillotine constraint and the
    /// kerf spacing are enforced).
    /// </summary>
    private static void PlacePiece(Candidate best, PieceToPlace piece, decimal kerf, SplitRule splitRule)
    {
        var rect = best.Rect;
        var sheet = best.Sheet;

        // The part goes in the corner of the free rect nearest the origin.
        sheet.Layout.Parts.Add(new PlacedPart
        {
            PartId = piece.PartId,
            Label = piece.Label,
            X = rect.X,
            Y = rect.Y,
            Width = best.PlacedW,
            Length = best.PlacedH,
            Rotated = best.Rotated,
        });

        // --- Split the remaining space with ONE straight cut ---------------
        // The part occupies its own size PLUS one kerf on the far sides
        // (that's the material the saw blade eats when cutting it free).
        // Clamp to the rect so a part that exactly fills the rect works.
        var usedW = Math.Min(best.PlacedW + kerf, rect.W);
        var usedH = Math.Min(best.PlacedH + kerf, rect.H);

        var remW = rect.W - usedW;  // space left to the right of the part
        var remH = rect.H - usedH;  // space left below the part

        // Two legal guillotine splits exist:
        //
        //  Horizontal cut (full width):        Vertical cut (full height):
        //  +------+---------+                  +------+---------+
        //  | part | right   |                  | part |         |
        //  +------+---------+                  +------+  right  |
        //  |     bottom     |                  |bottom|         |
        //  +----------------+                  +------+---------+
        //
        // Which one to take is this run's split rule:
        //   KeepBiggerLeftoverWhole:  the larger of the two leftovers stays
        //                             one uncut piece (good for big offcuts).
        //   KeepSmallerLeftoverWhole: the smaller one stays whole (sometimes
        //                             packs long runs of parts better).
        var bottomIsBigger = remH >= remW;
        var keepBottomWhole = splitRule == SplitRule.KeepBiggerLeftoverWhole
            ? bottomIsBigger
            : !bottomIsBigger;

        // Note the smaller strip is trimmed to the PART's size (PlacedW/PlacedH),
        // not the part+kerf size: the full-length cut that separates the two
        // leftover strips eats one kerf out of the smaller strip as well.
        FreeRect right, bottom;
        if (keepBottomWhole)
        {
            // Horizontal cut: bottom strip gets the full rect width.
            right  = new FreeRect { X = rect.X + usedW, Y = rect.Y,         W = remW,   H = best.PlacedH };
            bottom = new FreeRect { X = rect.X,         Y = rect.Y + usedH, W = rect.W, H = remH };
        }
        else
        {
            // Vertical cut: right strip gets the full rect height.
            right  = new FreeRect { X = rect.X + usedW, Y = rect.Y,         W = remW,         H = rect.H };
            bottom = new FreeRect { X = rect.X,         Y = rect.Y + usedH, W = best.PlacedW, H = remH };
        }

        // Replace the consumed rect with the (up to two) new smaller ones.
        sheet.FreeRects.Remove(rect);
        if (right.W > 0 && right.H > 0) sheet.FreeRects.Add(right);
        if (bottom.W > 0 && bottom.H > 0) sheet.FreeRects.Add(bottom);
    }
}
