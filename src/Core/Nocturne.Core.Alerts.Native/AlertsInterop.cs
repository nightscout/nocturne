using System.Reflection;
using System.Runtime.InteropServices;

namespace Nocturne.Core.Alerts.Native;

/// <summary>
/// P/Invoke bindings for the nocturne_alerts Rust library
/// (<c>crates/nocturne-alerts-ffi</c>). The C ABI is JSON-in/JSON-out: every
/// argument is a NUL-terminated UTF-8 string and every returned pointer is a
/// NUL-terminated UTF-8 string allocated in Rust.
/// </summary>
/// <remarks>
/// All functions that return strings allocate memory in Rust. Call
/// <see cref="FreeString"/> exactly once per returned pointer to prevent
/// memory leaks — the public wrappers here do that in a try/finally.
/// The library never throws across the boundary: failures come back as
/// <c>{"ok": false, "error": "…"}</c> envelopes (see crates/nocturne-alerts-ffi/README.md).
/// </remarks>
public static partial class AlertsInterop
{
    private const string LibraryName = "nocturne_alerts";

    static AlertsInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(AlertsInterop).Assembly, ResolveLibrary);
    }

    /// <summary>
    /// Probes, in order: <c>NOCTURNE_ALERTS_NATIVE_DIR</c> (set by CI after a fresh
    /// cargo build), <c>runtimes/{rid}/native</c> under the application base directory
    /// (the csproj packing layout), and the application base directory itself; then
    /// falls back to the default loader search.
    /// </summary>
    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
            return IntPtr.Zero;

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero; // fall back to the default loader search
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var fileName = PlatformFileName();

        var overrideDir = Environment.GetEnvironmentVariable("NOCTURNE_ALERTS_NATIVE_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
            yield return Path.Combine(overrideDir, fileName);

        var rid = RuntimeInformation.RuntimeIdentifier;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);

        // The csproj packs into the portable x64 RIDs; probe them when the
        // runtime RID is more specific (e.g. win-x64 vs win10-x64).
        var packedRid = OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsMacOS() ? "osx-x64"
            : "linux-x64";
        if (packedRid != rid)
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", packedRid, "native", fileName);

        yield return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    private static string PlatformFileName() =>
        OperatingSystem.IsWindows() ? "nocturne_alerts.dll"
        : OperatingSystem.IsMacOS() ? "libnocturne_alerts.dylib"
        : "libnocturne_alerts.so";

    #region Memory Management

    /// <summary>
    /// Free a string that was returned by a nocturne_alerts function.
    /// </summary>
    /// <param name="ptr">Pointer returned by one of the nocturne_alerts functions.</param>
    [LibraryImport(LibraryName, EntryPoint = "nocturne_alerts_free_string")]
    public static partial void FreeString(IntPtr ptr);

    #endregion

    #region Native entry points

    /// <summary>Crate version as a plain string. Must be freed with FreeString.</summary>
    [LibraryImport(LibraryName, EntryPoint = "nocturne_alerts_version")]
    private static partial IntPtr VersionNative();

    /// <summary>
    /// Evaluates one rule for one tick. Request/response are the JSON envelopes
    /// documented in crates/nocturne-alerts-ffi/README.md. Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nocturne_alerts_evaluate", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr EvaluateNative(string requestJson);

    /// <summary>
    /// Evaluates one condition node for one instant outside the per-rule driver
    /// (auxiliary scopes: snooze conditions, sweep auto-resolve). Request/response
    /// are the JSON envelopes documented in crates/nocturne-alerts-ffi/README.md.
    /// Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nocturne_alerts_evaluate_node", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr EvaluateNodeNative(string requestJson);

    /// <summary>
    /// Enumerates the canonical condition paths and leaf ids of a condition tree
    /// (for timer-pruning hosts). Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nocturne_alerts_leaf_paths", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr LeafPathsNative(string conditionNodeJson);

    /// <summary>
    /// Derives a rule's scope class (low/high/composite/undirected) for scoped Do
    /// Not Disturb. Request/response are the JSON envelopes documented in
    /// crates/nocturne-alerts-ffi/README.md. Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "nocturne_alerts_classify", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ClassifyNative(string requestJson);

    #endregion

    #region Managed wrappers

    /// <summary>Get the nocturne_alerts library version as a managed string.</summary>
    public static string GetVersion() => ConsumeString(VersionNative(), string.Empty);

    /// <summary>Raw evaluate call: request envelope JSON in, response envelope JSON out.</summary>
    public static string Evaluate(string requestJson) => ConsumeString(EvaluateNative(requestJson), "{}");

    /// <summary>Raw evaluate-node call: request envelope JSON in, response envelope JSON out.</summary>
    public static string EvaluateNode(string requestJson) => ConsumeString(EvaluateNodeNative(requestJson), "{}");

    /// <summary>Raw leaf-paths call: condition node JSON in, response envelope JSON out.</summary>
    public static string LeafPaths(string conditionNodeJson) => ConsumeString(LeafPathsNative(conditionNodeJson), "{}");

    /// <summary>Raw classify call: request envelope JSON in, response envelope JSON out.</summary>
    public static string Classify(string requestJson) => ConsumeString(ClassifyNative(requestJson), "{}");

    private static string ConsumeString(IntPtr ptr, string fallback)
    {
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? fallback;
        }
        finally
        {
            FreeString(ptr);
        }
    }

    #endregion

    #region Library Loading Helpers

    /// <summary>
    /// Check if the native nocturne_alerts library can be loaded.
    /// </summary>
    /// <returns>True if the library loads successfully.</returns>
    public static bool IsAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(GetVersion());
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            // A cdylib built for the wrong architecture (an arm64 .so on an amd64 host)
            // loads far enough to fail here rather than to not be found at all. Treat it
            // as unavailable so callers take their degraded path instead of faulting.
            return false;
        }
    }

    #endregion
}
