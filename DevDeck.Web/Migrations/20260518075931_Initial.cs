using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevDeck.Web.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DevServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ServiceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkingDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    StartCommand = table.Column<string>(type: "TEXT", nullable: false),
                    StartArguments = table.Column<string>(type: "TEXT", nullable: true),
                    StopCommand = table.Column<string>(type: "TEXT", nullable: true),
                    StopArguments = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevServices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LaunchProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaunchProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProxyRoutes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DevServiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    DestinationUrlOverride = table.Column<string>(type: "TEXT", nullable: true),
                    MatchPath = table.Column<string>(type: "TEXT", nullable: false),
                    MatchHostsCsv = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    PathTransformMode = table.Column<string>(type: "TEXT", nullable: false),
                    PathPrefixToRemove = table.Column<string>(type: "TEXT", nullable: true),
                    PathPrefixToAdd = table.Column<string>(type: "TEXT", nullable: true),
                    PathSet = table.Column<string>(type: "TEXT", nullable: true),
                    PreserveHostHeader = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoStartService = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireHealthyDestination = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthorizationPolicy = table.Column<string>(type: "TEXT", nullable: true),
                    ShowOnDashboard = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxyRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxyRoutes_DevServices_DevServiceId",
                        column: x => x.DevServiceId,
                        principalTable: "DevServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ServiceEnvironmentVariables",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DevServiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    IsSecret = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceEnvironmentVariables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceEnvironmentVariables_DevServices_DevServiceId",
                        column: x => x.DevServiceId,
                        principalTable: "DevServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceHealthChecks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DevServiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedStatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastCheckedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceHealthChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceHealthChecks_DevServices_DevServiceId",
                        column: x => x.DevServiceId,
                        principalTable: "DevServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DevServiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StoppedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartCommandSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    StartArgumentsSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    WorkingDirectorySnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    LogFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceRuns_DevServices_DevServiceId",
                        column: x => x.DevServiceId,
                        principalTable: "DevServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LaunchProfileServices",
                columns: table => new
                {
                    LaunchProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    DevServiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaunchProfileServices", x => new { x.LaunchProfileId, x.DevServiceId });
                    table.ForeignKey(
                        name: "FK_LaunchProfileServices_DevServices_DevServiceId",
                        column: x => x.DevServiceId,
                        principalTable: "DevServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LaunchProfileServices_LaunchProfiles_LaunchProfileId",
                        column: x => x.LaunchProfileId,
                        principalTable: "LaunchProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Key",
                table: "AppSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevServices_Name",
                table: "DevServices",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LaunchProfileServices_DevServiceId",
                table: "LaunchProfileServices",
                column: "DevServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyRoutes_DevServiceId",
                table: "ProxyRoutes",
                column: "DevServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyRoutes_MatchPath",
                table: "ProxyRoutes",
                column: "MatchPath");

            migrationBuilder.CreateIndex(
                name: "IX_ProxyRoutes_Name",
                table: "ProxyRoutes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceEnvironmentVariables_DevServiceId_Key",
                table: "ServiceEnvironmentVariables",
                columns: new[] { "DevServiceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealthChecks_DevServiceId",
                table: "ServiceHealthChecks",
                column: "DevServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRuns_DevServiceId_StartedUtc",
                table: "ServiceRuns",
                columns: new[] { "DevServiceId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "LaunchProfileServices");

            migrationBuilder.DropTable(
                name: "ProxyRoutes");

            migrationBuilder.DropTable(
                name: "ServiceEnvironmentVariables");

            migrationBuilder.DropTable(
                name: "ServiceHealthChecks");

            migrationBuilder.DropTable(
                name: "ServiceRuns");

            migrationBuilder.DropTable(
                name: "LaunchProfiles");

            migrationBuilder.DropTable(
                name: "DevServices");
        }
    }
}
