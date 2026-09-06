using System.ComponentModel.DataAnnotations;

namespace Nocturne.Core.Models.Health;

public class GoogleHealthOptions
{
    [Required, MaxLength(200)] public string ClientId { get; set; } = "";
    [MaxLength(500)] public string? ClientSecret { get; set; }
    [Required, MaxLength(500)] public string CallbackUrl { get; set; } = "";
    [MaxLength(32)] public string[] DataTypes { get; set; } = [];
    [Range(1, 90)] public int HistoryDays { get; set; } = 7;
    public DateTimeOffset? ImportFrom { get; set; }
    public bool PreviewOnly { get; set; }
}

public class GoogleHealthStatus
{
    public GoogleHealthCapability[] Capabilities { get; set; } = [];
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string ClientId { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string[] SelectedTypes { get; set; } = [];
    public string[] GrantedTypes { get; set; } = [];
    public int HistoryDays { get; set; } = 7;
    public DateTimeOffset? ImportFrom { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSync { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
    public string? ErrorCode { get; set; }
    public string[] ErrorDataTypes { get; set; } = [];
    public bool PreviewRequired { get; set; }
    public bool IsSyncing { get; set; }
    public string? SyncPhase { get; set; }
    public string? SyncDataType { get; set; }
    public int SyncCompletedDataTypes { get; set; }
    public int SyncTotalDataTypes { get; set; }
    public int SyncPagesRead { get; set; }
    public int? SyncProgressPercent { get; set; }
}

public class GoogleHealthPreview
{
    public GoogleHealthPreviewItem[] Items { get; set; } = [];
}

public class GoogleHealthPreviewItem
{
    public string DataType { get; set; } = "";
    public bool Granted { get; set; }
    public int Count { get; set; }
    public string? ErrorCode { get; set; }
    public bool Supported { get; set; }
}

public class GoogleHealthCapability
{
    public string DataType { get; set; } = "";
    public bool Supported { get; set; }
    public string? Destination { get; set; }
}

public class GoogleHealthAuthorize { public string Url { get; set; } = ""; }
public class GoogleHealthCallback
{
    [Required, MaxLength(256)] public string State { get; set; } = "";
    [Required, MaxLength(4096)] public string Code { get; set; } = "";
}

public class GoogleHealthReading
{
    public string DataType { get; set; } = "";
    public long Mills { get; set; }
    public long? EndMills { get; set; }
    public int? UtcOffsetMinutes { get; set; }
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";
}
