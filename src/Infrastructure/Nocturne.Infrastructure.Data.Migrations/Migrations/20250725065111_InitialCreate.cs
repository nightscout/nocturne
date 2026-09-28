using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "devicestatus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    utcOffset = table.Column<int>(type: "integer", nullable: true),
                    device = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: false
                    ),
                    isCharging = table.Column<bool>(type: "boolean", nullable: true),
                    uploader = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    pump = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    openaps = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    loop = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    xdripjs = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    radioAdapter = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    connect = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    @override = table.Column<string>(
                        name: "override",
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    cgm = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "null"),
                    meter = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    insulinPen = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    sys_created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    sys_updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devicestatus", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    dateString = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    mgdl = table.Column<double>(type: "double precision", nullable: false),
                    mmol = table.Column<double>(type: "double precision", nullable: true),
                    sgv = table.Column<double>(type: "double precision", nullable: true),
                    direction = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    type = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: false,
                        defaultValue: "sgv"
                    ),
                    device = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    notes = table.Column<string>(type: "text", nullable: true),
                    delta = table.Column<double>(type: "double precision", nullable: true),
                    scaled = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    sysTime = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    utcOffset = table.Column<int>(type: "integer", nullable: true),
                    noise = table.Column<int>(type: "integer", nullable: true),
                    filtered = table.Column<double>(type: "double precision", nullable: true),
                    unfiltered = table.Column<double>(type: "double precision", nullable: true),
                    rssi = table.Column<int>(type: "integer", nullable: true),
                    slope = table.Column<double>(type: "double precision", nullable: true),
                    intercept = table.Column<double>(type: "double precision", nullable: true),
                    scale = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    modified_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    is_demo = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: false
                    ),
                    meta = table.Column<string>(type: "jsonb", nullable: true, defaultValue: "{}"),
                    sys_created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    sys_updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "foods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    type = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: false,
                        defaultValue: "food"
                    ),
                    category = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    subcategory = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    name = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    portion = table.Column<double>(type: "double precision", nullable: false),
                    carbs = table.Column<double>(type: "double precision", nullable: false),
                    fat = table.Column<double>(type: "double precision", nullable: false),
                    protein = table.Column<double>(type: "double precision", nullable: false),
                    energy = table.Column<double>(type: "double precision", nullable: false),
                    gi = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    unit = table.Column<string>(
                        type: "character varying(10)",
                        maxLength: 10,
                        nullable: false,
                        defaultValue: "g"
                    ),
                    foods = table.Column<string>(type: "text", nullable: true),
                    hide_after_use = table.Column<bool>(type: "boolean", nullable: false),
                    hidden = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(
                        type: "integer",
                        nullable: false,
                        defaultValue: 99999
                    ),
                    sys_created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    sys_updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_foods", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    default_profile = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false,
                        defaultValue: "Default"
                    ),
                    start_date = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: false
                    ),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    units = table.Column<string>(
                        type: "character varying(10)",
                        maxLength: 10,
                        nullable: false,
                        defaultValue: "mg/dl"
                    ),
                    store_json = table.Column<string>(
                        type: "text",
                        nullable: false,
                        defaultValue: "{}"
                    ),
                    created_at_pg = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                    updated_at_pg = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    key = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    value = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    utc_offset = table.Column<int>(type: "integer", nullable: true),
                    srv_created = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    srv_modified = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    app = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    device = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    entered_by = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    version = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
                    notes = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: true
                    ),
                    sys_created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    sys_updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "treatments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    eventType = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    reason = table.Column<string>(type: "text", nullable: true),
                    glucose = table.Column<double>(type: "double precision", nullable: true),
                    glucoseType = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    carbs = table.Column<double>(type: "double precision", nullable: true),
                    insulin = table.Column<double>(type: "double precision", nullable: true),
                    protein = table.Column<double>(type: "double precision", nullable: true),
                    fat = table.Column<double>(type: "double precision", nullable: true),
                    foodType = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    units = table.Column<string>(
                        type: "character varying(10)",
                        maxLength: 10,
                        nullable: true
                    ),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    duration = table.Column<double>(type: "double precision", nullable: true),
                    percent = table.Column<double>(type: "double precision", nullable: true),
                    absolute = table.Column<double>(type: "double precision", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    enteredBy = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    targetTop = table.Column<double>(type: "double precision", nullable: true),
                    targetBottom = table.Column<double>(type: "double precision", nullable: true),
                    profile = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    split = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    date = table.Column<long>(type: "bigint", nullable: true),
                    carbTime = table.Column<int>(type: "integer", nullable: true),
                    boluscalc = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "{}"
                    ),
                    utcOffset = table.Column<int>(type: "integer", nullable: true),
                    timestamp = table.Column<long>(type: "bigint", nullable: true),
                    cuttedby = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    cutting = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    source = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    eventTime = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    preBolus = table.Column<double>(type: "double precision", nullable: true),
                    rate = table.Column<double>(type: "double precision", nullable: true),
                    mgdl = table.Column<double>(type: "double precision", nullable: true),
                    mmol = table.Column<double>(type: "double precision", nullable: true),
                    endmills = table.Column<long>(type: "bigint", nullable: true),
                    durationType = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    isAnnouncement = table.Column<bool>(type: "boolean", nullable: true),
                    profileJson = table.Column<string>(
                        type: "jsonb",
                        nullable: true,
                        defaultValue: "null"
                    ),
                    endprofile = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    insulinNeedsScaleFactor = table.Column<double>(
                        type: "double precision",
                        nullable: true
                    ),
                    absorptionTime = table.Column<int>(type: "integer", nullable: true),
                    enteredinsulin = table.Column<double>(type: "double precision", nullable: true),
                    splitNow = table.Column<double>(type: "double precision", nullable: true),
                    splitExt = table.Column<double>(type: "double precision", nullable: true),
                    status = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    relative = table.Column<double>(type: "double precision", nullable: true),
                    CR = table.Column<double>(type: "double precision", nullable: true),
                    NSCLIENT_ID = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    first = table.Column<bool>(type: "boolean", nullable: true),
                    end = table.Column<bool>(type: "boolean", nullable: true),
                    CircadianPercentageProfile = table.Column<bool>(
                        type: "boolean",
                        nullable: true
                    ),
                    percentage = table.Column<double>(type: "double precision", nullable: true),
                    timeshift = table.Column<double>(type: "double precision", nullable: true),
                    transmitterId = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    sys_created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    sys_updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_treatments", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_device",
                table: "devicestatus",
                column: "device"
            );

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_device_mills",
                table: "devicestatus",
                columns: new[] { "device", "mills" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_mills",
                table: "devicestatus",
                column: "mills",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_sys_created_at",
                table: "devicestatus",
                column: "sys_created_at"
            );

            migrationBuilder.CreateIndex(
                name: "ix_entries_mills",
                table: "entries",
                column: "mills",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_entries_sys_created_at",
                table: "entries",
                column: "sys_created_at"
            );

            migrationBuilder.CreateIndex(name: "ix_entries_type", table: "entries", column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_entries_type_mills",
                table: "entries",
                columns: new[] { "type", "mills" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ix_foods_category",
                table: "foods",
                column: "category"
            );

            migrationBuilder.CreateIndex(name: "ix_foods_name", table: "foods", column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_foods_sys_created_at",
                table: "foods",
                column: "sys_created_at"
            );

            migrationBuilder.CreateIndex(name: "ix_foods_type", table: "foods", column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_foods_type_name",
                table: "foods",
                columns: new[] { "type", "name" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_profiles_default_profile",
                table: "profiles",
                column: "default_profile"
            );

            migrationBuilder.CreateIndex(
                name: "ix_profiles_mills",
                table: "profiles",
                column: "mills",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_profiles_sys_created_at",
                table: "profiles",
                column: "created_at_pg"
            );

            migrationBuilder.CreateIndex(
                name: "ix_profiles_units",
                table: "profiles",
                column: "units"
            );

            migrationBuilder.CreateIndex(
                name: "ix_settings_is_active",
                table: "settings",
                column: "is_active"
            );

            migrationBuilder.CreateIndex(
                name: "ix_settings_key",
                table: "settings",
                column: "key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_settings_mills",
                table: "settings",
                column: "mills",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_settings_sys_created_at",
                table: "settings",
                column: "sys_created_at"
            );

            migrationBuilder.CreateIndex(
                name: "ix_treatments_event_type",
                table: "treatments",
                column: "eventType"
            );

            migrationBuilder.CreateIndex(
                name: "ix_treatments_event_type_mills",
                table: "treatments",
                columns: new[] { "eventType", "mills" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ix_treatments_mills",
                table: "treatments",
                column: "mills",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_treatments_sys_created_at",
                table: "treatments",
                column: "sys_created_at"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "devicestatus");

            migrationBuilder.DropTable(name: "entries");

            migrationBuilder.DropTable(name: "foods");

            migrationBuilder.DropTable(name: "profiles");

            migrationBuilder.DropTable(name: "settings");

            migrationBuilder.DropTable(name: "treatments");
        }
    }
}
