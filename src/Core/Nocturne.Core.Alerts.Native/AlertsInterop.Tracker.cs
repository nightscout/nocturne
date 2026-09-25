using System.Runtime.InteropServices;

namespace Nocturne.Core.Alerts.Native;

public static partial class AlertsInterop
{
    private const string TrackerProcessExport = "nocturne_alerts_tracker_process";
    private const string TrackerForceCloseExport = "nocturne_alerts_tracker_force_close";
    private const string TrackerCloseElapsedHysteresisExport = "nocturne_alerts_tracker_close_elapsed_hysteresis";

    /// <summary>
    /// Feeds one evaluation's truth through the excursion tracker without a condition tree.
    /// Request/response are the "Tracker" envelopes in crates/nocturne-alerts-ffi/README.md.
    /// Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = TrackerProcessExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr TrackerProcessNative(string requestJson);

    /// <summary>Closes the tracker's excursion, if it has one, from any state. Must be freed with FreeString.</summary>
    [LibraryImport(LibraryName, EntryPoint = TrackerForceCloseExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr TrackerForceCloseNative(string requestJson);

    /// <summary>Closes a hysteresis excursion whose window has elapsed. Must be freed with FreeString.</summary>
    [LibraryImport(LibraryName, EntryPoint = TrackerCloseElapsedHysteresisExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr TrackerCloseElapsedHysteresisNative(string requestJson);

    /// <summary>Raw tracker-process call: request envelope JSON in, response envelope JSON out.</summary>
    public static string TrackerProcess(string requestJson) =>
        ConsumeString(TrackerProcessNative(requestJson), "{}");

    /// <summary>Raw tracker force-close call: request envelope JSON in, response envelope JSON out.</summary>
    public static string TrackerForceClose(string requestJson) =>
        ConsumeString(TrackerForceCloseNative(requestJson), "{}");

    /// <summary>Raw tracker close-elapsed-hysteresis call: request envelope JSON in, response envelope JSON out.</summary>
    public static string TrackerCloseElapsedHysteresis(string requestJson) =>
        ConsumeString(TrackerCloseElapsedHysteresisNative(requestJson), "{}");
}
