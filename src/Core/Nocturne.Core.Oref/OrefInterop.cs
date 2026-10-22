using System.Runtime.InteropServices;
using System.Text;

namespace Nocturne.Core.Oref;

/// <summary>
/// P/Invoke bindings for the oref Rust library.
/// The oref library implements OpenAPS reference algorithms for glucose predictions,
/// IOB/COB calculations, and determine-basal dosing decisions.
/// </summary>
/// <remarks>
/// All functions that return strings allocate memory in Rust.
/// Call <see cref="FreeString"/> to free the returned memory to prevent memory leaks.
/// </remarks>
public static partial class OrefInterop
{
    private const string LibraryName = "oref";

    /// <summary>
    /// Free a string that was returned by an oref function.
    /// </summary>
    /// <param name="ptr">Pointer returned by one of the oref functions.</param>
    [LibraryImport(LibraryName, EntryPoint = "oref_free_string")]
    public static partial void FreeString(IntPtr ptr);

    /// <returns>Pointer to version string. Must be freed with FreeString.</returns>
    [LibraryImport(LibraryName, EntryPoint = "oref_version")]
    private static partial IntPtr VersionNative();

    /// <summary>
    /// Health check to verify the library is loaded correctly.
    /// </summary>
    /// <returns>Pointer to JSON string with status info. Must be freed with FreeString.</returns>
    [LibraryImport(LibraryName, EntryPoint = "oref_health_check")]
    private static partial IntPtr HealthCheckNative();

    /// <summary>
    /// Get the oref library version as a managed string.
    /// </summary>
    public static string GetVersion()
    {
        var ptr = VersionNative();
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Health check to verify the library is loaded correctly.
    /// </summary>
    /// <returns>JSON string containing status information.</returns>
    public static string HealthCheck()
    {
        var ptr = HealthCheckNative();
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Calculate Insulin on Board (IOB) from treatment history.
    /// </summary>
    /// <param name="profileJson">JSON string containing Profile data</param>
    /// <param name="treatmentsJson">JSON string containing array of Treatment objects</param>
    /// <param name="timeMillis">Current time as Unix milliseconds</param>
    /// <param name="currentOnly">If 1, only calculate current IOB (faster); if 0, calculate full array</param>
    /// <returns>Pointer to JSON string containing IOBData array. Must be freed with FreeString.</returns>
    [LibraryImport(LibraryName, EntryPoint = "oref_calculate_iob", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CalculateIobNative(
        string profileJson,
        string treatmentsJson,
        long timeMillis,
        int currentOnly);

    /// <summary>
    /// Calculate Insulin on Board (IOB) from treatment history.
    /// </summary>
    /// <param name="profileJson">JSON string containing Profile data</param>
    /// <param name="treatmentsJson">JSON string containing array of Treatment objects</param>
    /// <param name="timeMillis">Current time as Unix milliseconds</param>
    /// <param name="currentOnly">If true, only calculate current IOB (faster)</param>
    /// <returns>JSON string containing IOBData array.</returns>
    public static string CalculateIob(string profileJson, string treatmentsJson, long timeMillis, bool currentOnly = true)
    {
        var ptr = CalculateIobNative(profileJson, treatmentsJson, timeMillis, currentOnly ? 1 : 0);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "[]";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Calculate Carbs on Board (COB) from glucose and treatment history.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "oref_calculate_cob", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CalculateCobNative(
        string profileJson,
        string glucoseJson,
        string treatmentsJson,
        long timeMillis);

    /// <summary>
    /// Calculate Carbs on Board (COB) from glucose and treatment history.
    /// </summary>
    /// <param name="profileJson">JSON string containing Profile data</param>
    /// <param name="glucoseJson">JSON string containing array of GlucoseReading objects</param>
    /// <param name="treatmentsJson">JSON string containing array of Treatment objects</param>
    /// <param name="timeMillis">Current time as Unix milliseconds</param>
    /// <returns>JSON string containing COBResult.</returns>
    public static string CalculateCob(string profileJson, string glucoseJson, string treatmentsJson, long timeMillis)
    {
        var ptr = CalculateCobNative(profileJson, glucoseJson, treatmentsJson, timeMillis);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Calculate autosens ratio from glucose and treatment history.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "oref_calculate_autosens", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CalculateAutosensNative(
        string profileJson,
        string glucoseJson,
        string treatmentsJson,
        long timeMillis);

    /// <summary>
    /// Calculate autosens ratio from glucose and treatment history.
    /// Detects changes in insulin sensitivity over time.
    /// </summary>
    public static string CalculateAutosens(string profileJson, string glucoseJson, string treatmentsJson, long timeMillis)
    {
        var ptr = CalculateAutosensNative(profileJson, glucoseJson, treatmentsJson, timeMillis);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Run the determine-basal algorithm.
    /// This is the main dosing algorithm that determines optimal temp basals and SMBs.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "oref_determine_basal", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DetermineBasalNative(string inputsJson);

    /// <summary>
    /// Run the determine-basal algorithm.
    /// The result includes predicted_bg array for glucose forecasting.
    /// </summary>
    /// <param name="inputsJson">JSON string containing DetermineBasalInputs</param>
    /// <returns>JSON string containing DetermineBasalResult with predictions.</returns>
    public static string DetermineBasal(string inputsJson)
    {
        var ptr = DetermineBasalNative(inputsJson);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Convenience function to run determine_basal with individual parameters.
    /// </summary>
    [LibraryImport(LibraryName, EntryPoint = "oref_determine_basal_simple", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DetermineBasalSimpleNative(
        string profileJson,
        string glucoseStatusJson,
        string iobDataJson,
        string currentTempJson,
        double autosensRatio,
        double mealCob,
        int microBolusAllowed);

    /// <summary>
    /// Convenience function to run determine_basal with individual parameters.
    /// </summary>
    public static string DetermineBasalSimple(
        string profileJson,
        string glucoseStatusJson,
        string iobDataJson,
        string currentTempJson,
        double autosensRatio = 1.0,
        double mealCob = 0.0,
        bool microBolusAllowed = false)
    {
        var ptr = DetermineBasalSimpleNative(
            profileJson, glucoseStatusJson, iobDataJson, currentTempJson,
            autosensRatio, mealCob, microBolusAllowed ? 1 : 0);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "oref_calculate_glucose_status", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CalculateGlucoseStatusNative(string glucoseJson);

    /// <param name="glucoseJson">JSON string containing array of GlucoseReading objects (most recent first)</param>
    /// <returns>JSON string containing GlucoseStatus with delta, short_avgdelta, long_avgdelta.</returns>
    public static string CalculateGlucoseStatus(string glucoseJson)
    {
        var ptr = CalculateGlucoseStatusNative(glucoseJson);
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "{}";
        }
        finally
        {
            FreeString(ptr);
        }
    }

    /// <summary>
    /// Check if the native oref library can be loaded.
    /// </summary>
    /// <returns>True if library loads successfully.</returns>
    public static bool IsAvailable()
    {
        try
        {
            var version = GetVersion();
            return !string.IsNullOrEmpty(version);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
