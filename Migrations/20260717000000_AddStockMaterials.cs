using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GcwSheetOptimizer.Migrations
{
    /// <summary>
    /// Adds the StockMaterials table: standard purchasable sheet materials
    /// with their real sheet sizes (e.g. 48.5" x 96.5" oversized plywood).
    /// </summary>
    public partial class AddStockMaterials : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockMaterials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SheetWidth = table.Column<decimal>(type: "numeric", nullable: false),
                    SheetLength = table.Column<decimal>(type: "numeric", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMaterials", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMaterials_Name",
                table: "StockMaterials",
                column: "Name",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "StockMaterials");
        }
    }
}
