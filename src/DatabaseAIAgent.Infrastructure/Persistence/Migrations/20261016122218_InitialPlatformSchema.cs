using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DatabaseAIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPlatformSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "organization",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Industry = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssistantName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    BusinessDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    DateFormat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResponseStyle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RetentionAuditLogDays = table.Column<int>(type: "integer", nullable: true),
                    RetentionConversationDays = table.Column<int>(type: "integer", nullable: true),
                    RetentionKnowledgeDocumentDays = table.Column<int>(type: "integer", nullable: true),
                    RetentionUsageRecordDays = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization", x => x.Id);
                    table.CheckConstraint("ck_organization_singleton", "\"Id\" = '00000000-0000-0000-0000-000000000001'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization",
                schema: "platform");
        }
    }
}
