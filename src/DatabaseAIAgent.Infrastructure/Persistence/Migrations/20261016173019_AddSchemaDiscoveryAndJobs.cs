using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DatabaseAIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaDiscoveryAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "db_procedures",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Signature = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReturnType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CanExecute = table.Column<bool>(type: "boolean", nullable: false),
                    DeclaredDataAccess = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AverageDurationMs = table.Column<double>(type: "double precision", nullable: true),
                    ParameterCount = table.Column<int>(type: "integer", nullable: false),
                    IsMissing = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CallsOtherRoutines = table.Column<bool>(type: "boolean", nullable: false),
                    ChangesSchema = table.Column<bool>(type: "boolean", nullable: false),
                    DefinitionAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    DeletesData = table.Column<bool>(type: "boolean", nullable: false),
                    ModifiesData = table.Column<bool>(type: "boolean", nullable: false),
                    UsesDynamicSql = table.Column<bool>(type: "boolean", nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: true),
                    ResultColumns = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_procedures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_procedures_database_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "platform",
                        principalTable: "database_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_relationships",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FromSchema = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FromTable = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FromColumns = table.Column<List<string>>(type: "text[]", nullable: false),
                    ToSchema = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToTable = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToColumns = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_relationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_relationships_database_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "platform",
                        principalTable: "database_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_schemas",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_schemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_schemas_database_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "platform",
                        principalTable: "database_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_tables",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EstimatedRowCount = table.Column<long>(type: "bigint", nullable: true),
                    ColumnCount = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Columns = table.Column<string>(type: "jsonb", nullable: true),
                    Indexes = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_tables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_tables_database_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "platform",
                        principalTable: "database_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schema_analyses",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Failure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SchemaCount = table.Column<int>(type: "integer", nullable: false),
                    TableCount = table.Column<int>(type: "integer", nullable: false),
                    ViewCount = table.Column<int>(type: "integer", nullable: false),
                    ColumnCount = table.Column<int>(type: "integer", nullable: false),
                    RelationshipCount = table.Column<int>(type: "integer", nullable: false),
                    ProcedureCount = table.Column<int>(type: "integer", nullable: false),
                    NewProcedureCount = table.Column<int>(type: "integer", nullable: false),
                    MissingProcedureCount = table.Column<int>(type: "integer", nullable: false),
                    Warnings = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_analyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_schema_analyses_database_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "platform",
                        principalTable: "database_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_schema_analyses_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_background_jobs_Status_AvailableAt",
                schema: "platform",
                table: "background_jobs",
                columns: new[] { "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_db_procedures_ConnectionId_SchemaName_Name_Signature",
                schema: "platform",
                table: "db_procedures",
                columns: new[] { "ConnectionId", "SchemaName", "Name", "Signature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_relationships_ConnectionId",
                schema: "platform",
                table: "db_relationships",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_db_schemas_ConnectionId_Name",
                schema: "platform",
                table: "db_schemas",
                columns: new[] { "ConnectionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_tables_ConnectionId_SchemaName_Name",
                schema: "platform",
                table: "db_tables",
                columns: new[] { "ConnectionId", "SchemaName", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schema_analyses_ConnectionId_QueuedAt",
                schema: "platform",
                table: "schema_analyses",
                columns: new[] { "ConnectionId", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_schema_analyses_RequestedByUserId",
                schema: "platform",
                table: "schema_analyses",
                column: "RequestedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_jobs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "db_procedures",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "db_relationships",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "db_schemas",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "db_tables",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "schema_analyses",
                schema: "platform");
        }
    }
}
