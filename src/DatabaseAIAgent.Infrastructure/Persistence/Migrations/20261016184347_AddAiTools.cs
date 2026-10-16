using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DatabaseAIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_tools",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredProcedureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Risk = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClassificationSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClassificationReasons = table.Column<List<string>>(type: "text[]", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DisabledReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UnsupportedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxRows = table.Column<int>(type: "integer", nullable: false),
                    MaxResultBytes = table.Column<int>(type: "integer", nullable: false),
                    SourceDefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Parameters = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_tools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_tools_database_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalSchema: "platform",
                        principalTable: "database_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_tools_db_procedures_StoredProcedureId",
                        column: x => x.StoredProcedureId,
                        principalSchema: "platform",
                        principalTable: "db_procedures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_tools_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "tool_executions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RedactedArguments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    ResultBytes = table.Column<long>(type: "bigint", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    Confirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tool_executions_ai_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "platform",
                        principalTable: "ai_tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tool_executions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tool_permissions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ToolId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tool_permissions_ai_tools_ToolId",
                        column: x => x.ToolId,
                        principalSchema: "platform",
                        principalTable: "ai_tools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_tools_ApprovedByUserId",
                schema: "platform",
                table: "ai_tools",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_tools_ConnectionId",
                schema: "platform",
                table: "ai_tools",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_tools_Name",
                schema: "platform",
                table: "ai_tools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_tools_StoredProcedureId",
                schema: "platform",
                table: "ai_tools",
                column: "StoredProcedureId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_StartedAt",
                schema: "platform",
                table: "tool_executions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_ToolId_StartedAt",
                schema: "platform",
                table: "tool_executions",
                columns: new[] { "ToolId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_UserId",
                schema: "platform",
                table: "tool_executions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_permissions_ToolId",
                schema: "platform",
                table: "tool_permissions",
                column: "ToolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_executions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "tool_permissions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "ai_tools",
                schema: "platform");
        }
    }
}
