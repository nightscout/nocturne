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

    private const string FreeStringExport = "nocturne_alerts_free_string";
    private const string VersionExport = "nocturne_alerts_version";
    private const string TzdbVersionExport = "nocturne_alerts_tzdb_version";
    private const string EvaluateExport = "nocturne_alerts_evaluate";
    private const string EvaluateNodeExport = "nocturne_alerts_evaluate_node";
    private const string LeafPathsExport = "nocturne_alerts_leaf_paths";
    private const string ClassifyExport = "nocturne_alerts_classify";
    private const string ValidateExport = "nocturne_alerts_validate";

    /// <summary>Every export bound below; <see cref="Probe"/> resolves each one.</summary>
    private static readonly string[] BoundExports =
    [
        FreeStringExport, VersionExport, TzdbVersionExport, EvaluateExport, EvaluateNodeExport,
        LeafPathsExport, ClassifyExport, ValidateExport,
    ];

    /// <summary>
    /// The <c>nocturne-alerts-ffi</c> package version this assembly was built against, stamped
    /// from the crate's Cargo.toml by the csproj.
    /// </summary>
    public static string ExpectedVersion { get; } =
        typeof(AlertsInterop).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "NocturneAlertsExpectedVersion")?.Value ?? string.Empty;

    private static IntPtr _resolvedHandle;

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
                return _resolvedHandle = handle;
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
    [LibraryImport(LibraryName, EntryPoint = FreeStringExport)]
    public static partial void FreeString(IntPtr ptr);

    #endregion

    #region Native entry points

    /// <summary>Crate version as a plain string. Must be freed with FreeString.</summary>
    [LibraryImport(LibraryName, EntryPoint = VersionExport)]
    private static partial IntPtr VersionNative();

    /// <summary>
    /// The IANA time zone database release compiled into the library (e.g. <c>2025b</c>). Must be
    /// freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = TzdbVersionExport)]
    private static partial IntPtr TzdbVersionNative();

    /// <summary>
    /// Evaluates one rule for one tick. Request/response are the JSON envelopes
    /// documented in crates/nocturne-alerts-ffi/README.md. Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = EvaluateExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr EvaluateNative(string requestJson);

    /// <summary>
    /// Evaluates one condition node for one instant outside the per-rule driver
    /// (auxiliary scopes: snooze conditions, sweep auto-resolve). Request/response
    /// are the JSON envelopes documented in crates/nocturne-alerts-ffi/README.md.
    /// Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = EvaluateNodeExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr EvaluateNodeNative(string requestJson);

    /// <summary>
    /// Enumerates the canonical condition paths and leaf ids of a condition tree
    /// (for timer-pruning hosts). Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = LeafPathsExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr LeafPathsNative(string conditionNodeJson);

    /// <summary>
    /// Derives a rule's scope class (low/high/composite/undirected) for scoped Do
    /// Not Disturb. Request/response are the JSON envelopes documented in
    /// crates/nocturne-alerts-ffi/README.md. Must be freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = ClassifyExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ClassifyNative(string requestJson);

    /// <summary>
    /// Checks a rule's condition trees for everything a save should reject. Request/response
    /// are the JSON envelopes documented in crates/nocturne-alerts-ffi/README.md. Must be
    /// freed with FreeString.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = ValidateExport, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ValidateNative(string requestJson);

    #endregion

    #region Managed wrappers

    /// <summary>Get the nocturne_alerts library version as a managed string.</summary>
    public static string GetVersion() => ConsumeString(VersionNative(), string.Empty);

    /// <summary>The IANA time zone database release the library evaluates zones with.</summary>
    public static string GetTzdbVersion() => ConsumeString(TzdbVersionNative(), string.Empty);

    /// <summary>Raw evaluate call: request envelope JSON in, response envelope JSON out.</summary>
    public static string Evaluate(string requestJson) => ConsumeString(EvaluateNative(requestJson), "{}");

    /// <summary>Raw evaluate-node call: request envelope JSON in, response envelope JSON out.</summary>
    public static string EvaluateNode(string requestJson) => ConsumeString(EvaluateNodeNative(requestJson), "{}");

    /// <summary>Raw leaf-paths call: condition node JSON in, response envelope JSON out.</summary>
    public static string LeafPaths(string conditionNodeJson) => ConsumeString(LeafPathsNative(conditionNodeJson), "{}");

    /// <summary>Raw classify call: request envelope JSON in, response envelope JSON out.</summary>
    public static string Classify(string requestJson) => ConsumeString(ClassifyNative(requestJson), "{}");

    /// <summary>Raw validate call: request envelope JSON in, response envelope JSON out.</summary>
    public static string Validate(string requestJson) => ConsumeString(ValidateNative(requestJson), "{}");

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

    /// <summary>Whether the native nocturne_alerts library loads and passes <see cref="Probe"/>.</summary>
    public static bool IsAvailable() => Probe().IsAvailable;

    /// <summary>
    /// Loads the native library and checks it is the build this binding expects: its version
    /// equals <see cref="ExpectedVersion"/>, every bound export resolves and it reports a tzdb
    /// release, so a stale library fails here rather than on the first rule it evaluates.
    /// </summary>
    public static NativeProbeResult Probe()
    {
        try
        {
            var version = GetVersion();
            var handle = _resolvedHandle;
            if (handle == IntPtr.Zero
                && !NativeLibrary.TryLoad(LibraryName, typeof(AlertsInterop).Assembly, null, out handle))
                return NativeProbeResult.Unavailable("the library answered but its handle could not be obtained");

            var verified = Verify(version, ExpectedVersion, export => NativeLibrary.TryGetExport(handle, export, out _));
            if (!verified.IsAvailable)
                return verified;

            var tzdb = GetTzdbVersion();
            return string.IsNullOrEmpty(tzdb)
                ? NativeProbeResult.Unavailable("the library reports no tzdb release")
                : verified with { Version = version, TzdbVersion = tzdb };
        }
        catch (DllNotFoundException ex)
        {
            return NativeProbeResult.Unavailable(ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return NativeProbeResult.Unavailable(ex.Message);
        }
        catch (BadImageFormatException ex)
        {
            // A cdylib built for the wrong architecture (an arm64 .so on an amd64 host)
            // loads far enough to fail here rather than to not be found at all.
            return NativeProbeResult.Unavailable(ex.Message);
        }
    }

    internal static NativeProbeResult Verify(string reportedVersion, string expectedVersion, Func<string, bool> exportResolves)
    {
        if (string.IsNullOrEmpty(expectedVersion))
            return NativeProbeResult.Unavailable("this build carries no expected library version");
        if (reportedVersion != expectedVersion)
            return NativeProbeResult.Unavailable(
                $"the library reports version '{reportedVersion}' but this build expects '{expectedVersion}'");

        var missing = BoundExports.Where(e => !exportResolves(e)).ToList();
        return missing.Count == 0
            ? NativeProbeResult.Available
            : NativeProbeResult.Unavailable($"the library does not export {string.Join(", ", missing)}");
    }

    #endregion
}

/// <summary>The outcome of <see cref="AlertsInterop.Probe"/>.</summary>
/// <param name="Failure">Why the library is unusable, or <see langword="null"/> when it is usable.</param>
/// <param name="Version">The library version, when it is usable.</param>
/// <param name="TzdbVersion">The IANA time zone database release it evaluates with, when it is usable.</param>
public readonly record struct NativeProbeResult(string? Failure, string? Version = null, string? TzdbVersion = null)
{
    public static NativeProbeResult Available => default;

    public bool IsAvailable => Failure is null;

    public static NativeProbeResult Unavailable(string failure) => new(failure);
}
