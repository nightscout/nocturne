using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Constants;

/// <summary>
/// WebSocket event types for Socket.IO communication.
/// These constants ensure type safety between the C# backend and TypeScript frontend.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebSocketEvents
{
    // Connection events
    [EnumMember(Value = "connect")]
    Connect,

    [EnumMember(Value = "disconnect")]
    Disconnect,

    [EnumMember(Value = "connect_error")]
    ConnectError,

    [EnumMember(Value = "reconnect")]
    Reconnect,

    [EnumMember(Value = "reconnect_failed")]
    ReconnectFailed,

    [EnumMember(Value = "connect_ack")]
    ConnectAck,

    // Data events
    [EnumMember(Value = "dataUpdate")]
    DataUpdate,

    [EnumMember(Value = "treatmentUpdate")]
    TreatmentUpdate,

    // Storage events
    [EnumMember(Value = "create")]
    Create,

    [EnumMember(Value = "update")]
    Update,

    [EnumMember(Value = "delete")]
    Delete,

    // Notification events
    [EnumMember(Value = "announcement")]
    Announcement,

    [EnumMember(Value = "alarm")]
    Alarm,

    [EnumMember(Value = "urgent_alarm")]
    UrgentAlarm,

    [EnumMember(Value = "clear_alarm")]
    ClearAlarm,

    [EnumMember(Value = "notification")]
    Notification,

    // Status events
    [EnumMember(Value = "statusUpdate")]
    StatusUpdate,

    [EnumMember(Value = "status")]
    Status,

    // Auth events
    [EnumMember(Value = "authenticate")]
    Authenticate,

    [EnumMember(Value = "authenticated")]
    Authenticated,

    [EnumMember(Value = "join")]
    Join,

    [EnumMember(Value = "leave")]
    Leave,
}
