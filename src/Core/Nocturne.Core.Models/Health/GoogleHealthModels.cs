using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Health;

[JsonConverter(typeof(JsonStringEnumConverter<GoogleHealthSyncPhase>))]
public enum GoogleHealthSyncPhase
{
    [EnumMember(Value = "queued"), JsonStringEnumMemberName("queued")]
    Queued,

    [EnumMember(Value = "preparing"), JsonStringEnumMemberName("preparing")]
    Preparing,

    [EnumMember(Value = "refreshing_session"), JsonStringEnumMemberName("refreshing_session")]
    RefreshingSession,

    [EnumMember(Value = "reading"), JsonStringEnumMemberName("reading")]
    Reading,

    [EnumMember(Value = "validating"), JsonStringEnumMemberName("validating")]
    Validating,

    [EnumMember(Value = "integrating"), JsonStringEnumMemberName("integrating")]
    Integrating
}

public class GoogleHealthOptions
{
    [MaxLength(32)] public string[] DataTypes { get; set; } = [];
    [Range(1, 90)] public int HistoryDays { get; set; } = 7;
    public DateTimeOffset? ImportFrom { get; set; }
    public bool PreviewOnly { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<GoogleHealthCategoryKind>))]
public enum GoogleHealthCategoryKind
{
    [EnumMember(Value = "vitals"), JsonStringEnumMemberName("vitals")]
    Vitals,

    [EnumMember(Value = "activity"), JsonStringEnumMemberName("activity")]
    Activity,

    [EnumMember(Value = "body_measurement"), JsonStringEnumMemberName("body_measurement")]
    BodyMeasurement,

    [EnumMember(Value = "nutrition"), JsonStringEnumMemberName("nutrition")]
    Nutrition,

    [EnumMember(Value = "sleep"), JsonStringEnumMemberName("sleep")]
    Sleep,

    [EnumMember(Value = "cycle_tracking"), JsonStringEnumMemberName("cycle_tracking")]
    CycleTracking
}

[JsonConverter(typeof(JsonStringEnumConverter<GoogleHealthTypeState>))]
public enum GoogleHealthTypeState
{
    [EnumMember(Value = "read_failed"), JsonStringEnumMemberName("read_failed")]
    ReadFailed,

    [EnumMember(Value = "no_readonly_scope"), JsonStringEnumMemberName("no_readonly_scope")]
    NoReadonlyScope,

    [EnumMember(Value = "not_readable"), JsonStringEnumMemberName("not_readable")]
    NotReadable,

    [EnumMember(Value = "not_yet_supported"), JsonStringEnumMemberName("not_yet_supported")]
    NotYetSupported,

    [EnumMember(Value = "not_scanned"), JsonStringEnumMemberName("not_scanned")]
    NotScanned,

    [EnumMember(Value = "permission_not_granted"), JsonStringEnumMemberName("permission_not_granted")]
    PermissionNotGranted,

    [EnumMember(Value = "needs_attention"), JsonStringEnumMemberName("needs_attention")]
    NeedsAttention,

    [EnumMember(Value = "importing"), JsonStringEnumMemberName("importing")]
    Importing,

    [EnumMember(Value = "importing_no_data"), JsonStringEnumMemberName("importing_no_data")]
    ImportingNoData,

    [EnumMember(Value = "available"), JsonStringEnumMemberName("available")]
    Available,

    [EnumMember(Value = "available_no_data"), JsonStringEnumMemberName("available_no_data")]
    AvailableNoData
}

/// <summary>
/// Where the connection is in its lifecycle, the first that applies in declaration order. A
/// failure is reported separately, by <see cref="GoogleHealthStatus.ErrorCode"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GoogleHealthConnectionState>))]
public enum GoogleHealthConnectionState
{
    [EnumMember(Value = "unavailable"), JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [EnumMember(Value = "disconnected"), JsonStringEnumMemberName("disconnected")]
    Disconnected,

    [EnumMember(Value = "review_required"), JsonStringEnumMemberName("review_required")]
    ReviewRequired,

    [EnumMember(Value = "nothing_selected"), JsonStringEnumMemberName("nothing_selected")]
    NothingSelected,

    [EnumMember(Value = "syncing"), JsonStringEnumMemberName("syncing")]
    Syncing,

    [EnumMember(Value = "active"), JsonStringEnumMemberName("active")]
    Active
}

/// <summary>What the last inventory scan found for a data type.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GoogleHealthFound>))]
public enum GoogleHealthFound
{
    [EnumMember(Value = "not_scanned"), JsonStringEnumMemberName("not_scanned")]
    NotScanned,

    [EnumMember(Value = "no_permission"), JsonStringEnumMemberName("no_permission")]
    NoPermission,

    [EnumMember(Value = "scan_failed"), JsonStringEnumMemberName("scan_failed")]
    ScanFailed,

    [EnumMember(Value = "found"), JsonStringEnumMemberName("found")]
    Found,

    [EnumMember(Value = "none"), JsonStringEnumMemberName("none")]
    None
}

public class GoogleHealthCategory
{
    public GoogleHealthCategoryKind Kind { get; set; }
    /// <summary>Whether any type in the category can be newly selected for import.</summary>
    public bool HasSelectable { get; set; }
    public GoogleHealthDataTypeStatus[] Types { get; set; } = [];
}

public class GoogleHealthDataTypeStatus
{
    public string DataType { get; set; } = "";
    public string? Destination { get; set; }
    public GoogleHealthTypeState State { get; set; }
    public GoogleHealthFound Found { get; set; }
    public int Count { get; set; }
    /// <summary>Whether the type may be added to the import selection.</summary>
    public bool Selectable { get; set; }
    public string? ErrorCode { get; set; }
}

public class GoogleHealthStatus
{
    /// <summary>Whether the instance operator has configured a Google client to connect through.</summary>
    public bool Available { get; set; }
    public GoogleHealthConnectionState ConnectionState { get; set; }
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public bool IsDisconnecting { get; set; }
    public string[] SelectedTypes { get; set; } = [];
    public GoogleHealthCategory[] Categories { get; set; } = [];
    public int HistoryDays { get; set; } = 7;
    public DateTimeOffset? ImportFrom { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    public DateTimeOffset? BackfillSyncedThrough { get; set; }
    public bool BackfillComplete { get; set; }
    public string? ErrorCode { get; set; }
    public string[] ErrorDataTypes { get; set; } = [];
    public bool PreviewRequired { get; set; }
    public bool IsSyncing { get; set; }
    public GoogleHealthSyncPhase? SyncPhase { get; set; }
    public string? SyncDataType { get; set; }
    public int SyncCompletedDataTypes { get; set; }
    public int SyncTotalDataTypes { get; set; }
    public int SyncPagesRead { get; set; }
    public int? SyncProgressPercent { get; set; }
}

/// <summary>A data type in Google's catalog and whether, and where, Nocturne can import it.</summary>
public class GoogleHealthCapability
{
    public string DataType { get; set; } = "";
    public GoogleHealthCategoryKind Category { get; set; }
    public bool Supported { get; set; }
    public string? Destination { get; set; }
    /// <summary>Why an unsupported type cannot be read; null when Nocturne simply lacks it.</summary>
    public GoogleHealthTypeState? UnavailableReason { get; set; }
}

/// <summary>One data type's result from the last inventory scan.</summary>
public record GoogleHealthInventoryItem(string DataType, int Count, string? ErrorCode);

public class GoogleHealthAuthorize { public string Url { get; set; } = ""; }
public class GoogleHealthCallback
{
    [Required, MaxLength(1024)] public string State { get; set; } = "";
    [Required, MaxLength(4096)] public string Code { get; set; } = "";
}

public class GoogleHealthReading
{
    public string DataType { get; set; } = "";
    public string? OriginalId { get; set; }
    public long Mills { get; set; }
    public long? EndMills { get; set; }
    public int? UtcOffsetMinutes { get; set; }
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";
}
