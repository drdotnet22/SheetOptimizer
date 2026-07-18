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

        // The two RANDOMIZED orders (used by the extra restart runs):
        NoisyAreaDesc,      // area order, but each area is nudged +/-20% randomly
        Shuffled,           // completely random order
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

    /// <summary>One full strategy = one candidate run of the optimizer.
    /// Seed only matters for the randomized sort rules.</summary>
    private sealed record Strategy(
        PartSortRule Sort,
        PlacementRule Placement,
        SplitRule Split,
        bool UsePartialSheets,
        int Seed = 0)
    {
        public string DisplayName =>
            $"{SortName} / {PlacementName} / {SplitName}" +
            (UsePartialSheets ? "" : " / ignoring partials");

        private string SortName => Sort switch
        {
            PartSortRule.AreaDesc => "sort by area",
            PartSortRule.LongestSideDesc => "sort by longest side",
            PartSortRule.ShortestSideDesc => "sort by shortest side",
            PartSortRule.PerimeterDesc => "sort by perimeter",
            PartSortRule.NoisyAreaDesc => "randomized area order",
            _ => "random order",
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
    /// <param name="options">Default sheet size and offcut settings.</param>
    /// <param name="stockMaterials">
    /// The standard stock list. If a cutlist material's name exactly matches
    /// a stock material (ignoring case/spaces), that stock entry's sheet size
    /// is used for new full sheets - e.g. oversized 48.5" x 96.5" plywood.
    /// Materials with no match fall back to the default in
    /// <paramref name="options"/> (48" x 96").
    /// </param>
    public NestingSolution Nest(
        List<Part> parts,
        decimal kerfWidth,
        List<PartialSheet> partialSheetInventory,
        NestingOptions options,
        List<StockMaterial>? stockMaterials = null)
    {
        stockMaterials ??= new List<StockMaterial>();
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
            // --- Resolve the full-sheet size for this material -------------
            // Exact name match (trimmed, case-insensitive) against the stock
            // list; no match means the default 48 x 96.
            var stock = stockMaterials.FirstOrDefault(s =>
                s.Name.Trim().Equals(group.Key, StringComparison.OrdinalIgnoreCase));

            // Copy the options so each material can have its own sheet size
            // without the materials interfering with each other.
            var materialOptions = new NestingOptions
            {
                SheetWidth = stock?.SheetWidth ?? options.SheetWidth,
                SheetLength = stock?.SheetLength ?? options.SheetLength,
                MinOffcutWidth = options.MinOffcutWidth,
                MinOffcutLength = options.MinOffcutLength,
            };

            // Does this material even have partial sheets in inventory?
            // If not, the "ignore partials" variants would be identical
            // runs, so we skip them.
            var hasPartials = partialSheetInventory.Any(ps =>
                ps.Material.Trim().Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                && ps.Quantity > 0 && ps.Width > 0 && ps.Length > 0);

            MaterialNesting? bestLayout = null;
            LayoutScore? bestScore = null;

            // --- THE BATCH: try every strategy, keep the best result. ---
            foreach (var strategy in AllStrategies(hasPartials, options.ExtraRandomRuns))
            {
                var candidate = NestOneMaterial(
                    group.Key, group.ToList(), kerfWidth,
                    partialSheetInventory, materialOptions, strategy);

                var score = LayoutScore.Of(candidate, materialOptions);

                if (bestScore is null || score.IsBetterThan(bestScore))
                {
                    bestScore = score;
                    bestLayout = candidate;
                }
            }

            // --- Also try the SEQUENTIAL (sheet-by-sheet) mode. ---
            // It reliably consolidates waste onto the last sheet; the
            // scoring decides whether it beats the global mode's winner.
            foreach (var usePartials in hasPartials ? new[] { true, false } : new[] { true })
            {
                var sequential = NestOneMaterialSequential(
                    group.Key, group.ToList(), kerfWidth,
                    partialSheetInventory, materialOptions, usePartials);

                var score = LayoutScore.Of(sequential, materialOptions);

                if (bestScore is null || score.IsBetterThan(bestScore))
                {
                    bestScore = score;
                    bestLayout = sequential;
                }
            }

            solution.Materials.Add(bestLayout!); // batch always has >= 1 run
        }

        return solution;
    }

    /// <summary>The four deterministic (non-random) sort rules.</summary>
    private static readonly PartSortRule[] DeterministicSorts =
    {
        PartSortRule.AreaDesc,
        PartSortRule.LongestSideDesc,
        PartSortRule.ShortestSideDesc,
        PartSortRule.PerimeterDesc,
    };

    /// <summary>
    /// Every run the batch will try: first the full deterministic grid
    /// (up to 48 combinations), then a wave of RANDOMIZED restarts.
    ///
    /// The randomized runs shake up the part order (and pick random
    /// placement/split rules), which lets the batch escape layouts that
    /// every deterministic rule happens to be bad at. The master seed is
    /// FIXED, so clicking "run optimizer" twice on the same cutlist always
    /// gives the same answer.
    /// </summary>
    private static IEnumerable<Strategy> AllStrategies(bool includeNoPartialsVariants, int extraRandomRuns)
    {
        // --- The deterministic grid --------------------------------------
        foreach (var sort in DeterministicSorts)
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

        // --- The randomized restarts -------------------------------------
        var master = new Random(20260717); // fixed seed = reproducible results

        for (var i = 0; i < extraRandomRuns; i++)
        {
            // Mostly gentle "noisy area" order, sometimes a full shuffle.
            var sort = master.Next(3) == 0 ? PartSortRule.Shuffled : PartSortRule.NoisyAreaDesc;
            var placement = (PlacementRule)master.Next(3);
            var split = (SplitRule)master.Next(2);
            var usePartials = !includeNoPartialsVariants || master.Next(4) > 0; // 75% use them

            yield return new Strategy(sort, placement, split, usePartials, Seed: master.Next());
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
        public decimal MinFullSheetLoad; // lower is better (consolidation - see below)
        public decimal SmallScrapArea;   // lower is better
        public int LeftoverPieceCount;   // lower is better (fewer, bigger offcuts)
        public int CutCount;             // lower is better (tie-breaker)
        public decimal LargestLeftover;  // HIGHER is better

        /// <summary>Bucket size for the "roughly equal" comparisons
        /// (copied from NestingOptions.CutTieScrapTolerance).</summary>
        public decimal Tolerance;

        // -------------------------------------------------------------------
        // The "roughly equal" comparisons use BUCKETS (area divided by the
        // tolerance, rounded down) rather than pairwise "within X of each
        // other" checks. Buckets keep the comparison TRANSITIVE: with a
        // pairwise check, scanning hundreds of candidates lets the champion
        // drift - each new winner only slightly worse on scrap but with
        // fewer cuts - ending far from the best-scrap layout. With buckets
        // that cannot happen: a layout in a better scrap bucket always
        // beats one in a worse bucket, no matter the order candidates are
        // examined in.
        // -------------------------------------------------------------------
        private int ScrapBucket => (int)Math.Floor(SmallScrapArea / Tolerance);
        private int LargestLeftoverBucket => (int)Math.Floor(LargestLeftover / Tolerance);

        // -------------------------------------------------------------------
        // CONSOLIDATION: MinFullSheetLoad is the parts area on the LEAST
        // filled full sheet. Comparing this (ascending) prefers layouts
        // that pack the other sheets tight and leave the last sheet nearly
        // empty - i.e. "a few small pieces on the end of the 4th sheet"
        // with one big nearly-whole leftover, instead of spreading the
        // waste as medium chunks across every sheet. This deliberately
        // outranks small-scrap minimization: a nearly-empty sheet is worth
        // far more than shaving slivers off the tightly-packed sheets.
        // -------------------------------------------------------------------
        private int MinLoadBucket => (int)Math.Floor(MinFullSheetLoad / Tolerance);

        public static LayoutScore Of(MaterialNesting layout, NestingOptions options)
        {
            var score = new LayoutScore
            {
                UnplacedCount = layout.UnplacedParts.Count,
                Tolerance = Math.Max(1m, options.CutTieScrapTolerance),
            };

            var minFullLoad = decimal.MaxValue;

            foreach (var sheet in layout.Sheets)
            {
                if (!sheet.IsPartialSheet)
                {
                    score.FullSheets++;

                    // Track the least-filled full sheet (consolidation metric).
                    // Partial sheets are excluded: using up a small partial is
                    // already a win and shouldn't game this metric.
                    var load = sheet.Parts.Sum(p => p.Width * p.Length);
                    if (load < minFullLoad) minFullLoad = load;
                }
                score.SheetAreaUsed += sheet.SheetWidth * sheet.SheetLength;
                score.CutCount += sheet.CutCount;
                score.LeftoverPieceCount += sheet.Leftovers.Count;

                foreach (var leftover in sheet.Leftovers)
                {
                    var area = leftover.Width * leftover.Length;
                    var reusable = leftover.Width >= options.MinOffcutWidth
                                && leftover.Length >= options.MinOffcutLength;

                    if (!reusable) score.SmallScrapArea += area;
                    if (area > score.LargestLeftover) score.LargestLeftover = area;
                }
            }

            score.MinFullSheetLoad = minFullLoad == decimal.MaxValue ? 0m : minFullLoad;
            return score;
        }

        /// <summary>
        /// Comparison - earlier criteria win ties.
        ///
        /// Material efficiency always dominates: sheets, then sheet area,
        /// then the small-scrap bucket, then the largest-leftover bucket
        /// (so waste consolidates into one big reusable piece). Only when
        /// layouts are "roughly equal" on all of those does the saw-cut
        /// count decide, followed by exact scrap/fragmentation numbers.
        /// </summary>
        public bool IsBetterThan(LayoutScore other)
        {
            if (UnplacedCount != other.UnplacedCount) return UnplacedCount < other.UnplacedCount;
            if (FullSheets != other.FullSheets) return FullSheets < other.FullSheets;
            if (SheetAreaUsed != other.SheetAreaUsed) return SheetAreaUsed < other.SheetAreaUsed;

            // Consolidation first: prefer the layout whose least-filled full
            // sheet is emptiest (waste gathered on ONE sheet, nearly whole).
            if (MinLoadBucket != other.MinLoadBucket) return MinLoadBucket < other.MinLoadBucket;
            if (LargestLeftoverBucket != other.LargestLeftoverBucket)
                return LargestLeftoverBucket > other.LargestLeftoverBucket;

            // Then small scrap, in coarse buckets (transitive "roughly equal").
            if (ScrapBucket != other.ScrapBucket) return ScrapBucket < other.ScrapBucket;

            // Roughly equal on waste - fewer saw cuts wins.
            if (CutCount != other.CutCount) return CutCount < other.CutCount;

            // Final exact tie-breakers.
            if (MinFullSheetLoad != other.MinFullSheetLoad) return MinFullSheetLoad < other.MinFullSheetLoad;
            if (SmallScrapArea != other.SmallScrapArea) return SmallScrapArea < other.SmallScrapArea;
            if (LeftoverPieceCount != other.LeftoverPieceCount) return LeftoverPieceCount < other.LeftoverPieceCount;
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
        var pieces = ExpandPieces(parts, result);

        // --- Sort according to this run's strategy ---------------------------
        pieces = SortPieces(pieces, strategy.Sort, strategy.Seed);

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

        CollectLeftovers(openSheets, result);
        return result;
    }

    /// <summary>Turns Part rows (with quantities) into individual pieces to
    /// place; invalid rows are reported instead of crashing.</summary>
    private static List<PieceToPlace> ExpandPieces(List<Part> parts, MaterialNesting result)
    {
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
        return pieces;
    }

    /// <summary>Records each sheet's unused regions and adds the sheets to
    /// the result (slivers thinner than 1/4" are ignored - sawdust-adjacent).</summary>
    private static void CollectLeftovers(List<OpenSheet> openSheets, MaterialNesting result)
    {
        foreach (var sheet in openSheets)
        {
            foreach (var rect in sheet.FreeRects)
            {
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
    }

    // =======================================================================
    // SEQUENTIAL ("bin-oriented") mode.
    //
    // The global mode above places each part on the best spot across ALL
    // open sheets - great for squeezing the sheet count down, but it tends
    // to spread the waste over every sheet. This mode does what the
    // bin-packing literature calls "bin-oriented" construction instead:
    // pack ONE sheet as full as possible (trying many part orders and
    // rules, keeping the fullest packing), commit it, and repeat with
    // whatever is left. The natural outcome is tightly packed early sheets
    // and all the leftover material pooled on the final sheet - one big
    // reusable piece instead of medium chunks everywhere.
    //
    // Neither mode wins everywhere, so Nest() runs BOTH and the scoring
    // picks the better result per material.
    // =======================================================================

    /// <summary>The rule combinations tried for EACH sheet in sequential
    /// mode (24 deterministic + 50 randomized).</summary>
    private static readonly List<Strategy> PerSheetCombos =
        AllStrategies(includeNoPartialsVariants: false, extraRandomRuns: 50).ToList();

    private static MaterialNesting NestOneMaterialSequential(
        string material,
        List<Part> parts,
        decimal kerf,
        List<PartialSheet> inventory,
        NestingOptions options,
        bool usePartials)
    {
        var result = new MaterialNesting
        {
            Material = material,
            StrategyName = "sheet-by-sheet fill" + (usePartials ? "" : " / ignoring partials"),
        };

        var remaining = ExpandPieces(parts, result);

        var partialStock = usePartials
            ? inventory
                .Where(ps => ps.Material.Trim().Equals(material, StringComparison.OrdinalIgnoreCase)
                             && ps.Quantity > 0 && ps.Width > 0 && ps.Length > 0)
                .Select(ps => new PartialStock { Sheet = ps, Remaining = ps.Quantity })
                .ToList()
            : new List<PartialStock>();

        // Can this piece fit on an EMPTY sheet of the given size?
        static bool FitsSheet(PieceToPlace p, decimal sheetW, decimal sheetL) =>
            (p.Width <= sheetW && p.Length <= sheetL) ||
            (!p.GrainMatters && p.Length <= sheetW && p.Width <= sheetL);

        // Pieces that fit no sheet source at all are reported up front,
        // so the sheet loop below can never get stuck on them.
        remaining.RemoveAll(piece =>
        {
            var fitsSomething = FitsSheet(piece, options.SheetWidth, options.SheetLength)
                || partialStock.Any(s => s.Remaining > 0 && FitsSheet(piece, s.Sheet.Width, s.Sheet.Length));
            if (fitsSomething) return false;

            result.UnplacedParts.Add(new UnplacedPart
            {
                PartId = piece.PartId,
                Label = piece.Label,
                Width = piece.Width,
                Length = piece.Length,
                Reason = $"Too large for a {options.SheetWidth}\" x {options.SheetLength}\" sheet" +
                         (piece.GrainMatters ? " (rotation not allowed because grain matters)." : "."),
            });
            return true;
        });

        var openSheets = new List<OpenSheet>();

        // --- Fill one sheet at a time until every piece is placed -----------
        while (remaining.Count > 0)
        {
            // Which sheet do we fill next? Partial inventory first (the
            // smallest partial that fits at least one remaining piece),
            // otherwise a fresh full sheet.
            var partial = partialStock
                .Where(s => s.Remaining > 0 && remaining.Any(p => FitsSheet(p, s.Sheet.Width, s.Sheet.Length)))
                .OrderBy(s => s.Sheet.Width * s.Sheet.Length)
                .FirstOrDefault();

            var sheetW = partial?.Sheet.Width ?? options.SheetWidth;
            var sheetL = partial?.Sheet.Length ?? options.SheetLength;

            // Try every rule combination on this ONE sheet; keep the packing
            // with the most area (ties: fewer cuts, then fewer fragments).
            OpenSheet? bestSheet = null;
            List<PieceToPlace>? bestPlaced = null;
            decimal bestArea = -1;

            foreach (var combo in PerSheetCombos)
            {
                var (sheet, placed) = PackSingleSheet(
                    sheetW, sheetL,
                    isPartial: partial is not null,
                    sourceId: partial?.Sheet.Id,
                    sheetNumber: openSheets.Count + 1,
                    remaining, kerf, combo);

                var area = placed.Sum(p => p.Width * p.Length);
                var isBetter = bestSheet is null
                    || area > bestArea
                    || (area == bestArea && sheet.Layout.CutCount < bestSheet.Layout.CutCount)
                    || (area == bestArea && sheet.Layout.CutCount == bestSheet.Layout.CutCount
                        && sheet.FreeRects.Count < bestSheet.FreeRects.Count);

                if (isBetter)
                {
                    bestSheet = sheet;
                    bestPlaced = placed;
                    bestArea = area;
                }
            }

            if (bestSheet is null || bestPlaced is null || bestPlaced.Count == 0)
            {
                // Nothing fits a fresh sheet (shouldn't happen after the
                // pre-filter, but never loop forever - report and stop).
                foreach (var piece in remaining)
                {
                    result.UnplacedParts.Add(new UnplacedPart
                    {
                        PartId = piece.PartId,
                        Label = piece.Label,
                        Width = piece.Width,
                        Length = piece.Length,
                        Reason = "Internal error: piece fit no freshly opened sheet.",
                    });
                }
                break;
            }

            // Commit this sheet and remove its pieces from the pool.
            openSheets.Add(bestSheet);
            if (partial is not null) partial.Remaining--;
            var placedSet = new HashSet<PieceToPlace>(bestPlaced);
            remaining.RemoveAll(placedSet.Contains);
        }

        CollectLeftovers(openSheets, result);
        return result;
    }

    /// <summary>
    /// Packs as many of the remaining pieces as possible onto ONE sheet,
    /// using the given rule combination. Pieces that don't fit are simply
    /// skipped (they stay in the pool for the next sheet).
    /// </summary>
    private static (OpenSheet Sheet, List<PieceToPlace> Placed) PackSingleSheet(
        decimal sheetW, decimal sheetL, bool isPartial, int? sourceId, int sheetNumber,
        List<PieceToPlace> remaining, decimal kerf, Strategy combo)
    {
        var sheet = MakeOpenSheet(sheetW, sheetL, isPartial, sourceId, sheetNumber);
        var justThisSheet = new List<OpenSheet> { sheet };
        var placed = new List<PieceToPlace>();

        foreach (var piece in SortPieces(remaining, combo.Sort, combo.Seed))
        {
            var best = FindBestPlacement(justThisSheet, piece, combo.Placement);
            if (best is null) continue; // no room on this sheet - skip

            PlacePiece(best, piece, kerf, combo.Split);
            placed.Add(piece);
        }

        return (sheet, placed);
    }

    private static List<PieceToPlace> SortPieces(List<PieceToPlace> pieces, PartSortRule rule, int seed)
    {
        // The two randomized orders (used by the restart runs).
        if (rule == PartSortRule.NoisyAreaDesc)
        {
            // Biggest-first, but each part's area is nudged by +/-20% before
            // sorting - a gentle reshuffle that keeps big parts mostly first.
            var rng = new Random(seed);
            return pieces
                .OrderByDescending(p => (double)(p.Width * p.Length) * (0.8 + 0.4 * rng.NextDouble()))
                .ToList();
        }

        if (rule == PartSortRule.Shuffled)
        {
            // Completely random order (Fisher-Yates shuffle).
            var rng = new Random(seed);
            var shuffled = pieces.ToList();
            for (var i = shuffled.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            return shuffled;
        }

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

        // --- Count the saw cuts this placement needs -----------------------
        // Freeing the part takes one cut per side that is NOT already flush
        // with the edge of its free rectangle (a flush edge was cut earlier,
        // or is the factory edge of the sheet).
        if (best.PlacedW < rect.W) sheet.Layout.CutCount++;
        if (best.PlacedH < rect.H) sheet.Layout.CutCount++;

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
