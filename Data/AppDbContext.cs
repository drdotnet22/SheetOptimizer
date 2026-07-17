using GcwSheetOptimizer.Models;
using Microsoft.EntityFrameworkCore;

namespace GcwSheetOptimizer.Data;

/// <summary>
/// The EF Core database context - the bridge between the C# entity classes
/// (in Models/) and the PostgreSQL tables.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // Each DbSet becomes a table.
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<PartialSheet> PartialSheets => Set<PartialSheet>();
    public DbSet<NestingResult> NestingResults => Set<NestingResult>();
    public DbSet<StockMaterial> StockMaterials => Set<StockMaterial>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- Project -> Parts: deleting a project deletes its parts ---
        modelBuilder.Entity<Part>()
            .HasOne(p => p.Project)
            .WithMany(pr => pr.Parts)
            .HasForeignKey(p => p.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // --- Project -> NestingResults: deleting a project deletes its saved results ---
        modelBuilder.Entity<NestingResult>()
            .HasOne(r => r.Project)
            .WithMany(pr => pr.NestingResults)
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // --- PartialSheet -> SourceProject: deleting a project KEEPS the offcut,
        //     it just forgets where it came from (FK becomes null). ---
        modelBuilder.Entity<PartialSheet>()
            .HasOne(ps => ps.SourceProject)
            .WithMany() // Project has no navigation back to PartialSheets
            .HasForeignKey(ps => ps.SourceProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // Required string fields (so the database also enforces them).
        modelBuilder.Entity<Project>().Property(p => p.Name).IsRequired();
        modelBuilder.Entity<Part>().Property(p => p.Material).IsRequired();
        modelBuilder.Entity<PartialSheet>().Property(p => p.Material).IsRequired();
        modelBuilder.Entity<NestingResult>().Property(r => r.ResultDataJson).IsRequired();

        // --- Stock materials: names must be unique so the "exact match to
        //     part material" rule is never ambiguous. ---
        modelBuilder.Entity<StockMaterial>().Property(s => s.Name).IsRequired();
        modelBuilder.Entity<StockMaterial>()
            .HasIndex(s => s.Name)
            .IsUnique();

        // ------------------------------------------------------------------
        // DEVELOPMENT SEED DATA (commented out on purpose - the app starts
        // with an empty database by default).
        //
        // To try the app with sample data, uncomment the block below, then
        // create a migration for it:  dotnet ef migrations add SeedTestData
        // ------------------------------------------------------------------
        //
        // modelBuilder.Entity<Project>().HasData(
        //     new Project { Id = 1, Name = "Sample Cabinet", CreatedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), KerfWidth = 0.125m });
        //
        // modelBuilder.Entity<Part>().HasData(
        //     new Part { Id = 1, ProjectId = 1, Quantity = 2, Width = 24m, Length = 34.5m, Material = "3/4 Birch Plywood", GrainMatters = true,  Label = "Cabinet Side" },
        //     new Part { Id = 2, ProjectId = 1, Quantity = 1, Width = 22.5m, Length = 34.5m, Material = "3/4 Birch Plywood", GrainMatters = true,  Label = "Back Panel" },
        //     new Part { Id = 3, ProjectId = 1, Quantity = 3, Width = 22.5m, Length = 23m,   Material = "3/4 Birch Plywood", GrainMatters = false, Label = "Shelf" });
        //
        // modelBuilder.Entity<PartialSheet>().HasData(
        //     new PartialSheet { Id = 1, Width = 24m, Length = 48m, Material = "3/4 Birch Plywood", Quantity = 1, DateAdded = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Notes = "Leftover from bookshelf job" });
    }
}
