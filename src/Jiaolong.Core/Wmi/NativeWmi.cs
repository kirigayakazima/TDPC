using System.Runtime.InteropServices;

namespace Jiaolong.Core.Wmi;

/// <summary>
/// Raw COM declarations for WMI (wbemcli.h). Hand-written because the offline build
/// environment has no System.Management package available.
/// Only GetObject / ExecMethod / IWbemClassObject.{GetMethod,SpawnInstance,Put,Get} are
/// declared with real signatures; everything else is a vtable placeholder in the exact
/// wbemcli.h order (the CLR binds by slot, so order is what matters).
/// </summary>
internal static class WmiNative
{
    internal const int WBEM_FLAG_RETURN_IMMEDIATELY = 0x10;
    internal const int WBEM_FLAG_FORWARD_ONLY = 0x20;

    // CIMTYPE
    internal const int CIM_SINT8 = 16;
    internal const int CIM_UINT8 = 17;
    internal const int CIM_SINT16 = 2;
    internal const int CIM_UINT16 = 3;
    internal const int CIM_SINT32 = 4;
    internal const int CIM_UINT32 = 5;
    internal const int CIM_STRING = 8;
    internal const int CIM_FLAG_ARRAY = 0x2000;

    internal const uint CLSCTX_INPROC_SERVER = 1;

    internal const int RPC_C_AUTHN_WINNT = 2;
    internal const int RPC_C_AUTHZ_NONE = 0;
    internal const int RPC_C_AUTHN_LEVEL_PKT_PRIVACY = 6;
    internal const int RPC_C_IMP_LEVEL_IMPERSONATE = 3;

    internal static readonly Guid ClsidWbemLocator = new("9556DC99-828C-11CF-A37E-00AA003240C7");
    /// <summary>
    /// On some Windows images (including Jiaolong 16PRO's) the classic 9556DC99 coclass is not
    /// registered at all; instead wbemprox.dll registers this "WBEM Locator" coclass.
    /// It still exposes IWbemLocator, so we try it as a fallback.
    /// </summary>
    internal static readonly Guid ClsidWbemLocatorAlt = new("4590F811-1D3A-11D0-891F-00AA004B2E24");
    internal static readonly Guid IidWbemLocator = new("DC12A681-737F-11CF-884D-00AA004B2E24");

    /// <summary>
    /// Every WBEM locator coclass this Windows image actually registers in wbemprox.dll.
    /// Exactly one of them exposes IID_IWbemLocator; we probe them in order.
    /// </summary>
    internal static readonly Guid[] WbemLocatorCandidates =
    {
        new("9556DC99-828C-11CF-A37E-00AA003240C7"),  // classic  WbemLocator        (often absent)
        new("4590F811-1D3A-11D0-891F-00AA004B2E24"),  //          WBEM Locator
        new("443E7B79-DE31-11D2-B340-00104BCC4B4A"),  //          WbemUnauthenticatedLocator
        new("CB8555CC-9128-11D1-AD9B-00C04FD8FDFF"),  //          WbemAdministrativeLocator
        new("CD184336-9128-11D1-AD9B-00C04FD8FDFF"),  //          WbemAuthenticatedLocator
    };

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    /// <summary>COM must be initialised on this thread or CoCreateInstance returns CO_E_NOTINITIALIZED.</summary>
    internal static void EnsureComInitialized()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        // S_OK(0) / S_FALSE(1) / RPC_E_CHANGED_MODE(0x80010106) are all acceptable
        if (hr is 0 or 1 or unchecked((int)0x80010106)) return;
        Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    /// <summary>
    /// Fallback used when the raw CoCreateInstance path reports REGDB_E_CLASSNOTREG.
    /// Activator knows how to resolve the WbemLocator coclass from the registry.
    /// </summary>
    internal static IntPtr CreateByClsid(Guid clsid)
    {
        var t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        object o = Activator.CreateInstance(t)!;
        IntPtr p = Marshal.GetIUnknownForObject(o);
        Marshal.ReleaseComObject(o);
        return p;
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    internal static extern int CoSetProxyBlanket(
        IntPtr pProxy, uint dwAuthnSvc, uint dwAuthzSvc, IntPtr pServerPrincalName,
        uint dwAuthnLevel, uint dwImpLevel, IntPtr pAuthInfo, uint dwCapabilities);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    internal static extern int VariantClear(ref object pvarg);
}

[ComImport, Guid("DC12A681-737F-11CF-884D-00AA004B2E24")]  // IID_IWbemLocator (NOT the CLSID)
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemLocator
{
    [PreserveSig]
    int ConnectServer(
        [MarshalAs(UnmanagedType.BStr)] string strNetworkResource,
        [MarshalAs(UnmanagedType.BStr)] string strUser,
        [MarshalAs(UnmanagedType.BStr)] string strPassword,
        [MarshalAs(UnmanagedType.BStr)] string strLocale,
        int lSecurityFlags,
        [MarshalAs(UnmanagedType.BStr)] string strAuthority,
        IntPtr pCtx,
        out IntPtr ppNamespace);
}

/// <summary>IID = DC12A687-737F-11CF-884D-00AA004B2E24</summary>
[ComImport, Guid("DC12A687-737F-11CF-884D-00AA004B2E24")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemServices
{
    // slot 1..21 -- placeholders, exact wbemcli.h order
    void OpenNamespace(ref object strObjectPath, uint lFlags, IntPtr pCtx, IntPtr ppCallResult, out IntPtr ppNamespace);
    void CancelAsyncCall(IntPtr pSink);
    void QueryObjectSink(uint lFlags, out IntPtr ppResponseHandler);
    [PreserveSig] int GetObject(
        [MarshalAs(UnmanagedType.BStr)] string strObjectPath, uint lFlags, IntPtr pCtx,
        out IWbemClassObject ppObject, out IntPtr ppCallResult);
    void GetObjectAsync();
    void PutClass();
    void PutClassAsync();
    void DeleteClass();
    void DeleteClassAsync();
    void CreateClassEnum();
    void CreateClassEnumAsync();
    void PutInstance();
    void PutInstanceAsync();
    void DeleteInstance();
    void DeleteInstanceAsync();
    void CreateInstanceEnum();
    void CreateInstanceEnumAsync();
    void ExecQuery();
    void ExecQueryAsync();
    void ExecNotificationQuery();
    void ExecNotificationQueryAsync();

    // slot 22 -- the one we actually use
    [PreserveSig]
    int ExecMethod(
        [MarshalAs(UnmanagedType.BStr)] string strObjectPath,
        [MarshalAs(UnmanagedType.BStr)] string strMethodName,
        uint lFlags, IntPtr pCtx, IWbemClassObject pInParams,
        out IWbemClassObject ppOutParams, out IntPtr ppCallResult);
}

/// <summary>IID = DC12A683-737F-11CF-884D-00AA004B2E24</summary>
[ComImport, Guid("DC12A683-737F-11CF-884D-00AA004B2E24")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemClassObject
{
    void GetQualifierSet();               // 1
    [PreserveSig] int Get(                // 2
        [MarshalAs(UnmanagedType.BStr)] string wszName, int lFlags,
        ref object pVal, ref int pType, ref int plFlavor);
    [PreserveSig] int Put(                // 3
        [MarshalAs(UnmanagedType.BStr)] string wszName, int lFlags,
        ref object pVal, ref int pType);
    void Delete();                        // 4
    void GetNames();                      // 5
    void BeginEnumeration();              // 6
    void Next();                          // 7
    void EndEnumeration();                // 8
    void GetPropertyQualifierSet();       // 9
    void GetObjectText();                 // 10
    [PreserveSig] int SpawnInstance(      // 11
        int lFlags, out IWbemClassObject ppNewInstance);
    [PreserveSig] int GetMethod(          // 12
        [MarshalAs(UnmanagedType.BStr)] string wszName, int lFlags,
        out IWbemClassObject ppInSignature, out IWbemClassObject ppOutSignature);
    void GetMethodQualifierSet();         // 13
    void GetMethodOrigin();               // 14
}

internal static class WmiOle
{
    /// <summary>Creates the WbemLocator COM object and connects to a namespace.</summary>
    internal static IntPtr Connect(string namespacePath)
    {
        var iid = WmiNative.IidWbemLocator;
        IntPtr pLoc = IntPtr.Zero;
        string used = "?";
        int hr = unchecked((int)0x80040154);   // REGDB_E_CLASSNOTREG until a candidate succeeds
        var log = new System.Text.StringBuilder();

        WmiNative.EnsureComInitialized();

        foreach (var c in WmiNative.WbemLocatorCandidates)
        {
            var clsid = c;
            hr = WmiNative.CoCreateInstance(ref clsid, IntPtr.Zero, WmiNative.CLSCTX_INPROC_SERVER,
                                            ref iid, out pLoc);
            log.Append($"{c}=0x{hr:X8} ");
            if (hr == 0 && pLoc != IntPtr.Zero) { used = c.ToString(); break; }
            pLoc = IntPtr.Zero;
        }

        if (pLoc == IntPtr.Zero)
        {
            var err = new InvalidOperationException(
                "none of the WBEM locator coclasses exposes IID_IWbemLocator. " + log);
            throw err;
        }

        IWbemLocator loc;
        try
        {
            loc = (IWbemLocator)Marshal.GetObjectForIUnknown(pLoc);
        }
        catch (Exception ex2)
        {
            throw new InvalidOperationException($"QI for IWbemLocator failed (via {used}). {log}", ex2);
        }
        Marshal.Release(pLoc);
        System.Diagnostics.Debug.WriteLine("WbemLocator via " + used);

        hr = loc.ConnectServer(namespacePath, null!, null!, null!, 0, null!, IntPtr.Zero, out IntPtr pSvc);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        // Without this blanket WMI answers 0x80041003 (access denied) even for reads.
        hr = WmiNative.CoSetProxyBlanket(pSvc,
            (uint)WmiNative.RPC_C_AUTHN_WINNT, (uint)WmiNative.RPC_C_AUTHZ_NONE, IntPtr.Zero,
            (uint)WmiNative.RPC_C_AUTHN_LEVEL_PKT_PRIVACY, (uint)WmiNative.RPC_C_IMP_LEVEL_IMPERSONATE,
            IntPtr.Zero, 0);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        return pSvc;
    }
}
