using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using GcwSheetOptimizer.Models;

namespace GcwSheetOptimizer.Services;

/// <summary>
/// Parses an uploaded cutlist CSV into Part objects.
///
/// Expected columns (header names are matched case-insensitively, extra
/// columns are ignored):
///   Quantity, Width, Length, Material, GrainMatters, Label (optional)
///
/// Bad rows do NOT abort the import - each problem is collected as a
/// per-row error message so the UI can show "row 5: missing Width" while
/// still importing the good rows.
/// </summary>
public class CsvImportService
{
    /// <summary>The outcome of parsing one CSV file.</summary>
    public class ImportResult
    {
        public List<Part> ValidParts { get; } = new();
        public List<string> RowErrors { get; } = new();

        /// <summary>A fatal problem with the whole file (e.g. no header row).</summary>
        public string? FileError { get; set; }
    }

    public ImportResult Parse(Stream csvStream)
    {
        var result = new ImportResult();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Match headers ignoring case and surrounding spaces:
            // "width", " Width ", "WIDTH" all work.
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),

            // Don't throw on quirky data - we validate fields ourselves.
            MissingFieldFound = null,
            BadDataFound = null,
            HeaderValidated = null,
        };

        try
        {
            using var reader = new StreamReader(csvStream);
            using var csv = new CsvReader(reader, config);

            // Read the header row first.
            if (!csv.Read() || !csv.ReadHeader())
            {
                result.FileError = "The file appears to be empty (no header row found).";
                return result;
            }

            var headers = (csv.HeaderRecord ?? Array.Empty<string>())
                .Select(h => h.Trim().ToLowerInvariant())
                .ToHashSet();

            // Check the required columns exist before bothering with rows.
            var required = new[] { "quantity", "width", "length", "material", "grainmatters" };
            var missing = required.Where(r => !headers.Contains(r)).ToList();
            if (missing.Count > 0)
            {
                result.FileError =
                    "Missing required column(s): " + string.Join(", ", missing) +
                    ". Expected headers: Quantity, Width, Length, Material, GrainMatters, Label (optional).";
                return result;
            }

            // Row 1 is the header, so data starts at row 2.
            var rowNumber = 1;
            while (csv.Read())
            {
                rowNumber++;
                try
                {
                    ParseRow(csv, rowNumber, result);
                }
                catch (Exception ex)
                {
                    // A single bad row should never kill the import.
                    result.RowErrors.Add($"Row {rowNumber}: could not be read ({ex.Message}).");
                }
            }
        }
        catch (Exception ex)
        {
            result.FileError = "Could not read the file as CSV: " + ex.Message;
        }

        return result;
    }

    private static void ParseRow(CsvReader csv, int rowNumber, ImportResult result)
    {
        var errors = new List<string>();

        // --- Quantity -------------------------------------------------------
        var quantityText = csv.GetField("quantity")?.Trim();
        if (!int.TryParse(quantityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity))
            errors.Add("Quantity is missing or not a whole number");
        else if (quantity <= 0)
            errors.Add("Quantity must be at least 1");

        // --- Width / Length --------------------------------------------------
        var width = ParseDimension(csv.GetField("width"), "Width", errors);
        var length = ParseDimension(csv.GetField("length"), "Length", errors);

        // --- Material ---------------------------------------------------------
        var material = csv.GetField("material")?.Trim();
        if (string.IsNullOrWhiteSpace(material))
            errors.Add("Material is missing");

        // --- GrainMatters (accepts true/false, 1/0, yes/no) -------------------
        var grainText = csv.GetField("grainmatters")?.Trim();
        var grainMatters = ParseFlexibleBool(grainText);
        if (grainMatters is null)
            errors.Add($"GrainMatters value '{grainText}' not recognized (use true/false, 1/0, or yes/no)");

        // --- Label (optional column, optional value) ---------------------------
        string? label = null;
        if (csv.TryGetField<string>("label", out var labelValue))
            label = string.IsNullOrWhiteSpace(labelValue) ? null : labelValue.Trim();

        if (errors.Count > 0)
        {
            result.RowErrors.Add($"Row {rowNumber}: " + string.Join("; ", errors) + ". Row was skipped.");
            return;
        }

        result.ValidParts.Add(new Part
        {
            Quantity = quantity,
            Width = width!.Value,
            Length = length!.Value,
            Material = material!,
            GrainMatters = grainMatters!.Value,
            Label = label,
        });
    }

    /// <summary>Parses a dimension field; adds an error message if invalid.</summary>
    private static decimal? ParseDimension(string? text, string fieldName, List<string> errors)
    {
        text = text?.Trim();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            errors.Add($"{fieldName} is missing or not a number");
            return null;
        }
        if (value <= 0)
        {
            errors.Add($"{fieldName} must be greater than zero");
            return null;
        }
        return value;
    }

    /// <summary>Accepts true/false, 1/0, yes/no, y/n (any casing). Null = unrecognized.</summary>
    private static bool? ParseFlexibleBool(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" => true,
            "false" or "0" or "no" or "n" => false,
            _ => null,
        };
    }
}
