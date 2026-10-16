using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DatabaseAIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "database_connections",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TlsMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EncryptedCredentials = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastTestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ServerVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermCanReadTables = table.Column<bool>(type: "boolean", nullable: false),
                    PermCanWriteData = table.Column<bool>(type: "boolean", nullable: false),
                    PermExecutableProcedures = table.Column<int>(type: "integer", nullable: false),
                    PermIsAdministrator = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_database_connections_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_database_connections_CreatedByUserId",
                schema: "platform",
                table: "database_connections",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_database_connections_Name",
                schema: "platform",
                table: "database_connections",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "database_connections",
                schema: "platform");
        }
    }
}
