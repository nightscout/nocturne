using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace Nocturne.Widget.Windows11;

/// <summary>
/// Entry point for the Nocturne Windows 11 Widget provider
/// </summary>
internal static class Program
{
    /// <summary>
    /// The CLSID for the widget provider COM class factory - must match Package.appxmanifest
    /// </summary>
    public static readonly Guid WidgetProviderClsid = new("B8E3F2A1-5C4D-4E6F-8A9B-1C2D3E4F5A6B");

    private static ManualResetEvent? _emptyWidgetListEvent;

    // COM registration constants
    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const uint REGCLS_SUSPENDED = 4;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    [MTAThread]
    public static void Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Widget provider starting...");
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Args: {string.Join(" ", args)}");
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] PID: {Environment.ProcessId}");

        // Check if launched by Windows Widget host for COM activation
        if (!args.Contains("-RegisterProcessAsComServer"))
        {
            Console.WriteLine("Widget provider must be launched by Windows Widget host.");
            Console.WriteLine("To test manually, run with: -RegisterProcessAsComServer");
            return;
        }

        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Initializing COM wrappers...");
        // Initialize COM wrappers for WinRT interop - REQUIRED for widget provider
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] COM wrappers initialized");

        _emptyWidgetListEvent = new ManualResetEvent(false);

        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Creating WidgetProviderFactory...");
        var factory = new WidgetProviderFactory<NocturneWidgetProvider>();
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Factory created, calling CoRegisterClassObject...");

        // Register the COM class factory
        var hr = CoRegisterClassObject(
            WidgetProviderClsid,
            factory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE,
            out var cookie);

        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] CoRegisterClassObject returned: 0x{hr:X8}");

        if (hr < 0)
        {
            Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] CoRegisterClassObject failed!");
            Marshal.ThrowExceptionForHR(hr);
        }

        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] Registered successfully with cookie: {cookie}");

        // Check if we have a console window
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            // Running with console - wait for user input
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
        else
        {
            // Running as Windows app - wait until all widgets are removed
            _emptyWidgetListEvent.WaitOne();
        }

        CoRevokeClassObject(cookie);
        Console.WriteLine("Widget provider unregistered.");
    }

    /// <summary>
    /// Signal that all widgets have been deleted
    /// </summary>
    public static void SignalEmptyWidgetList()
    {
        _emptyWidgetListEvent?.Set();
    }
}

/// <summary>
/// COM class factory for creating widget provider instances
/// </summary>
[ComVisible(true)]
internal sealed class WidgetProviderFactory<T> : IClassFactory where T : IWidgetProvider, new()
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        Console.WriteLine($"CreateInstance called with riid: {riid}");

        if (pUnkOuter != IntPtr.Zero)
        {
            Console.WriteLine("Aggregation not supported");
            return CLASS_E_NOAGGREGATION;
        }

        // Check for the CLASS GUID (not interface GUID) or IUnknown
        // Microsoft's sample uses typeof(T).GUID which is the class GUID
        if (riid == typeof(T).GUID || riid == Guids.IUnknown)
        {
            Console.WriteLine($"Creating widget provider instance for {typeof(T).Name}...");
            // Use MarshalInspectable to create a COM-callable wrapper for the WinRT interface
            ppvObject = MarshalInspectable<IWidgetProvider>.FromManaged(new T());
            Console.WriteLine($"Widget provider created, ptr: {ppvObject}");
            return 0; // S_OK
        }

        Console.WriteLine($"Interface not supported: {riid} (expected: {typeof(T).GUID})");
        return E_NOINTERFACE;
    }

    public int LockServer(bool fLock)
    {
        return 0; // S_OK
    }
}

/// <summary>
/// COM interface GUIDs
/// </summary>
internal static class Guids
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    public static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
}

/// <summary>
/// COM IClassFactory interface
/// </summary>
[ComImport]
[ComVisible(false)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}
