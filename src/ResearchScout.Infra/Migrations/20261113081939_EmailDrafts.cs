using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ResearchScout.Infra.Migrations
{
    /// <inheritdoc />
    public partial class EmailDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    professor_name = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    cited_paper_ids = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_drafts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_drafts_created_at",
                table: "email_drafts",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_drafts");
        }
    }
}
