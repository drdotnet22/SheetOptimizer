using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GcwSheetOptimizer.Migrations
{
    /// <summary>
    /// The initial database schema. This runs automatically on app startup
    /// (see Program.cs -> db.Database.Migrate()).
    /// </summary>
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    KerfWidth = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Parts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Width = table.Column<decimal>(type: "numeric", nullable: false),
                    Length = table.Column<decimal>(type: "numeric", nullable: false),
                    Material = table.Column<string>(type: "text", nullable: false),
                    GrainMatters = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true),
                    ProjectId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Parts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartialSheets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Width = table.Column<decimal>(type: "numeric", nullable: false),
                    Length = table.Column<decimal>(type: "numeric", nullable: false),
                    Material = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    DateAdded = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceProjectId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartialSheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartialSheets_Projects_SourceProjectId",
                        column: x => x.SourceProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "NestingResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(type: "integer", nullable: false),
                    ResultDataJson = table.Column<string>(type: "text", nullable: false),
                    GeneratedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NestingResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NestingResults_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Parts_ProjectId",
                table: "Parts",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PartialSheets_SourceProjectId",
                table: "PartialSheets",
                column: "SourceProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_NestingResults_ProjectId",
                table: "NestingResults",
                column: "ProjectId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "NestingResults");
            migrationBuilder.DropTable(name: "PartialSheets");
            migrationBuilder.DropTable(name: "Parts");
            migrationBuilder.DropTable(name: "Projects");
        }
    }
}
