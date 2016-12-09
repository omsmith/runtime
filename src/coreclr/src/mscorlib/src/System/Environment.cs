// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

/*============================================================
**
**
**
** Purpose: Provides some basic access to some environment 
** functionality.
**
**
============================================================*/
namespace System {
    using System.IO;
    using System.Security;
    using System.Resources;
    using System.Globalization;
    using System.Collections;
    using System.Collections.Generic;
    using System.Security.Permissions;
    using System.Text;
    using System.Configuration.Assemblies;
    using System.Runtime.InteropServices;
    using System.Reflection;
    using System.Diagnostics;
    using Microsoft.Win32;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Runtime.ConstrainedExecution;
    using System.Runtime.Versioning;
    using System.Diagnostics.Contracts;

    [ComVisible(true)]
    public enum EnvironmentVariableTarget
    {
        Process = 0,
        User = 1,
        Machine = 2,
    }

    [ComVisible(true)]
    public static partial class Environment
    {
        // Assume the following constants include the terminating '\0' - use <, not <=
        const int MaxEnvVariableValueLength = 32767;  // maximum length for environment variable name and value
        // System environment variables are stored in the registry, and have 
        // a size restriction that is separate from both normal environment 
        // variables and registry value name lengths, according to MSDN.
        // MSDN doesn't detail whether the name is limited to 1024, or whether
        // that includes the contents of the environment variable.
        const int MaxSystemEnvVariableLength = 1024;
        const int MaxUserEnvVariableLength = 255;

        internal sealed class ResourceHelper
        {
            internal ResourceHelper(String name) {
                m_name = name;
            }

            private String m_name;
            private ResourceManager SystemResMgr;

            // To avoid infinite loops when calling GetResourceString.  See comments
            // in GetResourceString for this field.
            private List<string> currentlyLoading;
        
            // process-wide state (since this is only used in one domain), 
            // used to avoid the TypeInitialization infinite recusion
            // in GetResourceStringCode
            internal bool resourceManagerInited = false;

            // Is this thread currently doing infinite resource lookups?
            private int infinitelyRecursingCount;

            // Data representing one individual resource lookup on a thread.
            internal class GetResourceStringUserData
            {
                public ResourceHelper m_resourceHelper;
                public String m_key;
                public String m_retVal;
                public bool m_lockWasTaken;

                public GetResourceStringUserData(ResourceHelper resourceHelper, String key)
                {
                    m_resourceHelper = resourceHelper;
                    m_key = key;
                }
            }
            
            [System.Security.SecuritySafeCritical]  // auto-generated
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
            internal String GetResourceString(String key)  {
                if (key == null || key.Length == 0) {
                    Contract.Assert(false, "Environment::GetResourceString with null or empty key.  Bug in caller, or weird recursive loading problem?");
                    return "[Resource lookup failed - null or empty resource name]";
                }

                // We have a somewhat common potential for infinite 
                // loops with mscorlib's ResourceManager.  If "potentially dangerous"
                // code throws an exception, we will get into an infinite loop
                // inside the ResourceManager and this "potentially dangerous" code.
                // Potentially dangerous code includes the IO package, CultureInfo,
                // parts of the loader, some parts of Reflection, Security (including 
                // custom user-written permissions that may parse an XML file at
                // class load time), assembly load event handlers, etc.  Essentially,
                // this is not a bounded set of code, and we need to fix the problem.
                // Fortunately, this is limited to mscorlib's error lookups and is NOT
                // a general problem for all user code using the ResourceManager.
                
                // The solution is to make sure only one thread at a time can call 
                // GetResourceString.  Also, since resource lookups can be 
                // reentrant, if the same thread comes into GetResourceString
                // twice looking for the exact same resource name before 
                // returning, we're going into an infinite loop and we should 
                // return a bogus string.  

                GetResourceStringUserData userData = new GetResourceStringUserData(this, key);

                RuntimeHelpers.TryCode tryCode = new RuntimeHelpers.TryCode(GetResourceStringCode);
                RuntimeHelpers.CleanupCode cleanupCode = new RuntimeHelpers.CleanupCode(GetResourceStringBackoutCode);

                RuntimeHelpers.ExecuteCodeWithGuaranteedCleanup(tryCode, cleanupCode, userData);
                return userData.m_retVal;
            }

            #if FEATURE_CORECLR
            [System.Security.SecurityCritical] // auto-generated
            #else
            [System.Security.SecuritySafeCritical]
            #endif
            private void GetResourceStringCode(Object userDataIn)
            {
                GetResourceStringUserData userData = (GetResourceStringUserData) userDataIn;
                ResourceHelper rh = userData.m_resourceHelper;
                String key = userData.m_key;

                Monitor.Enter(rh, ref userData.m_lockWasTaken);

                // Are we recursively looking up the same resource?  Note - our backout code will set
                // the ResourceHelper's currentlyLoading stack to null if an exception occurs.
                if (rh.currentlyLoading != null && rh.currentlyLoading.Count > 0 && rh.currentlyLoading.LastIndexOf(key) != -1) {
                    // We can start infinitely recursing for one resource lookup,
                    // then during our failure reporting, start infinitely recursing again.
                    // avoid that.
                    if (rh.infinitelyRecursingCount > 0) {
                        userData.m_retVal = "[Resource lookup failed - infinite recursion or critical failure detected.]";
                        return;
                    }
                    rh.infinitelyRecursingCount++;

                    // Note: our infrastructure for reporting this exception will again cause resource lookup.
                    // This is the most direct way of dealing with that problem.
                    String message = "Infinite recursion during resource lookup within "+System.CoreLib.Name+".  This may be a bug in "+System.CoreLib.Name+", or potentially in certain extensibility points such as assembly resolve events or CultureInfo names.  Resource name: " + key;
                    Assert.Fail("[Recursive resource lookup bug]", message, Assert.COR_E_FAILFAST, System.Diagnostics.StackTrace.TraceFormat.NoResourceLookup);
                    Environment.FailFast(message);
                }
                if (rh.currentlyLoading == null)
                    rh.currentlyLoading = new List<string>();

                // Call class constructors preemptively, so that we cannot get into an infinite
                // loop constructing a TypeInitializationException.  If this were omitted,
                // we could get the Infinite recursion assert above by failing type initialization
                // between the Push and Pop calls below.
        
                if (!rh.resourceManagerInited)
                {
                    // process-critical code here.  No ThreadAbortExceptions
                    // can be thrown here.  Other exceptions percolate as normal.
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try {
                    }
                    finally {
                        RuntimeHelpers.RunClassConstructor(typeof(ResourceManager).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(ResourceReader).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(RuntimeResourceSet).TypeHandle);
                        RuntimeHelpers.RunClassConstructor(typeof(BinaryReader).TypeHandle);
                        rh.resourceManagerInited = true; 
                    }
            
                } 
        
                rh.currentlyLoading.Add(key); // Push

                if (rh.SystemResMgr == null) {
                    rh.SystemResMgr = new ResourceManager(m_name, typeof(Object).Assembly);
                }
                String s = rh.SystemResMgr.GetString(key, null);
                rh.currentlyLoading.RemoveAt(rh.currentlyLoading.Count - 1); // Pop

                Contract.Assert(s!=null, "Managed resource string lookup failed.  Was your resource name misspelled?  Did you rebuild mscorlib after adding a resource to resources.txt?  Debug this w/ cordbg and bug whoever owns the code that called Environment.GetResourceString.  Resource name was: \""+key+"\"");

                userData.m_retVal = s;
            }

            #if FEATURE_CORECLR
            [System.Security.SecurityCritical] // auto-generated
            #endif
            [PrePrepareMethod]
            private void GetResourceStringBackoutCode(Object userDataIn, bool exceptionThrown)
            {
                GetResourceStringUserData userData = (GetResourceStringUserData) userDataIn;
                ResourceHelper rh = userData.m_resourceHelper;

                if (exceptionThrown)
                {
                    if (userData.m_lockWasTaken) 
                    {
                        // Backout code - throw away potentially corrupt state
                        rh.SystemResMgr = null;
                        rh.currentlyLoading = null;
                    }
                }
                // Release the lock, if we took it.
                if (userData.m_lockWasTaken)
                {
                    Monitor.Exit(rh);
                }
            }
        
        }

              private static volatile ResourceHelper m_resHelper;  // Doesn't need to be initialized as they're zero-init.

        private const  int    MaxMachineNameLength = 256;

        // Private object for locking instead of locking on a public type for SQL reliability work.
        private static Object s_InternalSyncObject;
        private static Object InternalSyncObject {
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
            get {
                if (s_InternalSyncObject == null) {
                    Object o = new Object();
                    Interlocked.CompareExchange<Object>(ref s_InternalSyncObject, o, null);
                }
                return s_InternalSyncObject;
            }
        }


        private static volatile OperatingSystem m_os;  // Cached OperatingSystem value

        /*==================================TickCount===================================
        **Action: Gets the number of ticks since the system was started.
        **Returns: The number of ticks since the system was started.
        **Arguments: None
        **Exceptions: None
        ==============================================================================*/
        public static extern int TickCount {
            [System.Security.SecuritySafeCritical]  // auto-generated
            [MethodImplAttribute(MethodImplOptions.InternalCall)]
            get;
        }
        
        // Terminates this process with the given exit code.
        [System.Security.SecurityCritical]  // auto-generated
        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        internal static extern void _Exit(int exitCode);

        [System.Security.SecuritySafeCritical]  // auto-generated
#pragma warning disable 618
        [SecurityPermissionAttribute(SecurityAction.Demand, Flags=SecurityPermissionFlag.UnmanagedCode)]
#pragma warning restore 618
        public static void Exit(int exitCode) {
            _Exit(exitCode);
        }


        public static extern int ExitCode {
            [System.Security.SecuritySafeCritical]  // auto-generated
            [MethodImplAttribute(MethodImplOptions.InternalCall)]
            get;
    
            [System.Security.SecuritySafeCritical]  // auto-generated
            [MethodImplAttribute(MethodImplOptions.InternalCall)]
            set;
        }

        // Note: The CLR's Watson bucketization code looks at the caller of the FCALL method
        // to assign blame for crashes.  Don't mess with this, such as by making it call 
        // another managed helper method, unless you consult with some CLR Watson experts.
        [System.Security.SecurityCritical]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public static extern void FailFast(String message);

        [System.Security.SecurityCritical]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern void FailFast(String message, uint exitCode);

        // This overload of FailFast will allow you to specify the exception object
        // whose bucket details *could* be used when undergoing the failfast process.
        // To be specific:
        //
        // 1) When invoked from within a managed EH clause (fault/finally/catch),
        //    if the exception object is preallocated, the runtime will try to find its buckets
        //    and use them. If the exception object is not preallocated, it will use the bucket
        //    details contained in the object (if any).
        //
        // 2) When invoked from outside the managed EH clauses (fault/finally/catch),
        //    if the exception object is preallocated, the runtime will use the callsite's
        //    IP for bucketing. If the exception object is not preallocated, it will use the bucket
        //    details contained in the object (if any).
        [System.Security.SecurityCritical]
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public static extern void FailFast(String message, Exception exception);

        /*===============================CurrentDirectory===============================
        **Action:  Provides a getter and setter for the current directory.  The original
        **         current directory is the one from which the process was started.  
        **Returns: The current directory (from the getter).  Void from the setter.
        **Arguments: The current directory to which to switch to the setter.
        **Exceptions: 
        ==============================================================================*/
        public static String CurrentDirectory
        {
            get{
                return Directory.GetCurrentDirectory();
            }

            #if FEATURE_CORECLR
            [System.Security.SecurityCritical] // auto-generated
            #endif
            set { 
                Directory.SetCurrentDirectory(value);
            }
        }

        // Returns the system directory (ie, C:\WinNT\System32).
        public static String SystemDirectory {
#if FEATURE_CORECLR
            [System.Security.SecurityCritical]
#else
            [System.Security.SecuritySafeCritical]  // auto-generated
#endif
            get {
                StringBuilder sb = new StringBuilder(Path.MaxPath);
                int r = Win32Native.GetSystemDirectory(sb, Path.MaxPath);
                Contract.Assert(r < Path.MaxPath, "r < Path.MaxPath");
                if (r==0) __Error.WinIOError();
                String path = sb.ToString();

                return path;
            }
        }

        // Returns the windows directory (ie, C:\WinNT).
        // Used by NLS+ custom culures only at the moment.
        internal static String InternalWindowsDirectory {
            [System.Security.SecurityCritical]  // auto-generated
            get {
                StringBuilder sb = new StringBuilder(Path.MaxPath);
                int r = Win32Native.GetWindowsDirectory(sb, Path.MaxPath);
                Contract.Assert(r < Path.MaxPath, "r < Path.MaxPath");
                if (r==0) __Error.WinIOError();
                String path = sb.ToString();
                
                return path;
            }
        }

        [System.Security.SecuritySafeCritical]  // auto-generated
        public static String ExpandEnvironmentVariables(String name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            Contract.EndContractBlock();

            if (name.Length == 0) {
                return name;
            }

            if (AppDomain.IsAppXModel() && !AppDomain.IsAppXDesignMode()) {
                // Environment variable accessors are not approved modern API.
                // Behave as if no variables are defined in this case.
                return name; 
            }

            int currentSize = 100;
            StringBuilder blob = new StringBuilder(currentSize); // A somewhat reasonable default size

#if PLATFORM_UNIX // Win32Native.ExpandEnvironmentStrings isn't available
            int lastPos = 0, pos;
            while (lastPos < name.Length && (pos = name.IndexOf('%', lastPos + 1)) >= 0)
            {
                if (name[lastPos] == '%')
                {
                    string key = name.Substring(lastPos + 1, pos - lastPos - 1);
                    string value = Environment.GetEnvironmentVariable(key);
                    if (value != null)
                    {
                        blob.Append(value);
                        lastPos = pos + 1;
                        continue;
                    }
                }
                blob.Append(name.Substring(lastPos, pos - lastPos));
                lastPos = pos;
            }
            blob.Append(name.Substring(lastPos));
#else

            int size;

            blob.Length = 0;
            size = Win32Native.ExpandEnvironmentStrings(name, blob, currentSize);
            if (size == 0)
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            
            while (size > currentSize) {
                currentSize = size;
                blob.Capacity = currentSize;
                blob.Length = 0;

                size = Win32Native.ExpandEnvironmentStrings(name, blob, currentSize);
                if (size == 0)
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
#endif // PLATFORM_UNIX

            return blob.ToString();
        }

        public static String MachineName {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {

                // UWP Debug scenarios
                if (AppDomain.IsAppXModel() && !AppDomain.IsAppXDesignMode())
                {
                    // Getting Computer Name is not a supported scenario on Store apps.
                    throw new PlatformNotSupportedException();
                }

                // In future release of operating systems, you might be able to rename a machine without
                // rebooting.  Therefore, don't cache this machine name.
                StringBuilder buf = new StringBuilder(MaxMachineNameLength);
                int len = MaxMachineNameLength;
                if (Win32Native.GetComputerName(buf, ref len) == 0)
                    throw new InvalidOperationException(Environment.GetResourceString("InvalidOperation_ComputerName"));
                return buf.ToString();
            }
        }

        [SecurityCritical]
        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        private static extern Int32 GetProcessorCount();

        public static int ProcessorCount {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                return GetProcessorCount();
            }
        }

        public static int SystemPageSize {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                (new EnvironmentPermission(PermissionState.Unrestricted)).Demand();
                Win32Native.SYSTEM_INFO info = new Win32Native.SYSTEM_INFO();
                Win32Native.GetSystemInfo(ref info);
                return info.dwPageSize;
            }
        }

        /*==============================GetCommandLineArgs==============================
        **Action: Gets the command line and splits it appropriately to deal with whitespace,
        **        quotes, and escape characters.
        **Returns: A string array containing your command line arguments.
        **Arguments: None
        **Exceptions: None.
        ==============================================================================*/
        [System.Security.SecuritySafeCritical]  // auto-generated
        public static String[] GetCommandLineArgs()
        {
            new EnvironmentPermission(EnvironmentPermissionAccess.Read, "Path").Demand();
            /*
             * There are multiple entry points to a hosted app.
             * The host could use ::ExecuteAssembly() or ::CreateDelegate option
             * ::ExecuteAssembly() -> In this particular case, the runtime invokes the main 
               method based on the arguments set by the host, and we return those arguments
             *
             * ::CreateDelegate() -> In this particular case, the host is asked to create a 
             * delegate based on the appDomain, assembly and methodDesc passed to it.
             * which the caller uses to invoke the method. In this particular case we do not have
             * any information on what arguments would be passed to the delegate.
             * So our best bet is to simply use the commandLine that was used to invoke the process.
             * in case it is present.
             */
            if(s_CommandLineArgs != null)
                return (string[])s_CommandLineArgs.Clone();

            return GetCommandLineArgsNative();
        }

        [System.Security.SecurityCritical]  // auto-generated
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern String[] GetCommandLineArgsNative();

        private static string[] s_CommandLineArgs = null;
        private static void SetCommandLineArgs(string[] cmdLineArgs)
        {
            s_CommandLineArgs = cmdLineArgs;
        }

        private unsafe static char[] GetEnvironmentCharArray()
        {
            char[] block = null;

            // Make sure pStrings is not leaked with async exceptions
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
            }
            finally
            {
                char* pStrings = null;

                try
                {
                    pStrings = Win32Native.GetEnvironmentStrings();
                    if (pStrings == null)
                    {
                        throw new OutOfMemoryException();
                    }

                    // Format for GetEnvironmentStrings is:
                    // [=HiddenVar=value\0]* [Variable=value\0]* \0
                    // See the description of Environment Blocks in MSDN's
                    // CreateProcess page (null-terminated array of null-terminated strings).

                    // Search for terminating \0\0 (two unicode \0's).
                    char* p = pStrings;
                    while (!(*p == '\0' && *(p + 1) == '\0'))
                        p++;

                    int len = (int)(p - pStrings + 1);
                    block = new char[len];

                    fixed (char* pBlock = block)
                        string.wstrcpy(pBlock, pStrings, len);
                }
                finally
                {
                    if (pStrings != null)
                        Win32Native.FreeEnvironmentStrings(pStrings);
                }
            }

            return block;
        }

        /*===============================GetLogicalDrives===============================
        **Action: Retrieves the names of the logical drives on this machine in the  form "C:\". 
        **Arguments:   None.
        **Exceptions:  IOException.
        **Permissions: SystemInfo Permission.
        ==============================================================================*/
        [System.Security.SecuritySafeCritical]  // auto-generated
        public static String[] GetLogicalDrives() {
            new EnvironmentPermission(PermissionState.Unrestricted).Demand();
                                 
            int drives = Win32Native.GetLogicalDrives();
            if (drives==0)
                __Error.WinIOError();
            uint d = (uint)drives;
            int count = 0;
            while (d != 0) {
                if (((int)d & 1) != 0) count++;
                d >>= 1;
            }
            String[] result = new String[count];
            char[] root = new char[] {'A', ':', '\\'};
            d = (uint)drives;
            count = 0;
            while (d != 0) {
                if (((int)d & 1) != 0) {
                    result[count++] = new String(root);
                }
                d >>= 1;
                root[0]++;
            }
            return result;
        }
        
        /*===================================NewLine====================================
        **Action: A property which returns the appropriate newline string for the given
        **        platform.
        **Returns: \r\n on Win32.
        **Arguments: None.
        **Exceptions: None.
        ==============================================================================*/
        public static String NewLine {
            get {
                Contract.Ensures(Contract.Result<String>() != null);
#if !PLATFORM_UNIX
                return "\r\n";
#else
                return "\n";
#endif // !PLATFORM_UNIX
            }
        }

        
        /*===================================Version====================================
        **Action: Returns the COM+ version struct, describing the build number.
        **Returns:
        **Arguments:
        **Exceptions:
        ==============================================================================*/
        public static Version Version {
            get {

                // Previously this represented the File version of mscorlib.dll.  Many other libraries in the framework and outside took dependencies on the first three parts of this version 
                // remaining constant throughout 4.x.  From 4.0 to 4.5.2 this was fine since the file version only incremented the last part.Starting with 4.6 we switched to a file versioning
                // scheme that matched the product version.  In order to preserve compatibility with existing libraries, this needs to be hard-coded.
                
                return new Version(4,0,30319,42000);
            }
        }

        
        /*==================================WorkingSet==================================
        **Action:
        **Returns:
        **Arguments:
        **Exceptions:
        ==============================================================================*/
        [System.Security.SecurityCritical]  // auto-generated
        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode), SuppressUnmanagedCodeSecurity]
        private static extern long GetWorkingSet();

        public static long WorkingSet {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                new EnvironmentPermission(PermissionState.Unrestricted).Demand();
                return GetWorkingSet();
            }
        }


        /*==================================OSVersion===================================
        **Action:
        **Returns:
        **Arguments:
        **Exceptions:
        ==============================================================================*/
        public static OperatingSystem OSVersion {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                Contract.Ensures(Contract.Result<OperatingSystem>() != null);

                if (m_os==null) { // We avoid the lock since we don't care if two threads will set this at the same time.

                    Microsoft.Win32.Win32Native.OSVERSIONINFO osvi = new Microsoft.Win32.Win32Native.OSVERSIONINFO();
                    if (!GetVersion(osvi)) {
                        throw new InvalidOperationException(Environment.GetResourceString("InvalidOperation_GetVersion"));
                    }
                            
                    Microsoft.Win32.Win32Native.OSVERSIONINFOEX osviEx = new Microsoft.Win32.Win32Native.OSVERSIONINFOEX();
                    if (!GetVersionEx(osviEx))
                        throw new InvalidOperationException(Environment.GetResourceString("InvalidOperation_GetVersion"));

#if PLATFORM_UNIX
                    PlatformID id = PlatformID.Unix;
#else
                    PlatformID id = PlatformID.Win32NT;
#endif // PLATFORM_UNIX

                    Version v =  new Version(osvi.MajorVersion, osvi.MinorVersion, osvi.BuildNumber, (osviEx.ServicePackMajor << 16) |osviEx.ServicePackMinor);
                    m_os = new OperatingSystem(id, v, osvi.CSDVersion);
                }
                Contract.Assert(m_os != null, "m_os != null");
                return m_os;
            }
        }

#if FEATURE_CORESYSTEM

        internal static bool IsWindows8OrAbove {
            get {
                return true;
            }
        }

#if FEATURE_COMINTEROP
        internal static bool IsWinRTSupported {
            get {
                return true;
            }
        }
#endif // FEATURE_COMINTEROP

#else // FEATURE_CORESYSTEM

        private static volatile bool s_IsWindows8OrAbove;
        private static volatile bool s_CheckedOSWin8OrAbove;

        // Windows 8 version is 6.2
        internal static bool IsWindows8OrAbove {
            get {
                if (!s_CheckedOSWin8OrAbove) {
                    OperatingSystem OS = Environment.OSVersion;
                    s_IsWindows8OrAbove = (OS.Platform == PlatformID.Win32NT && 
                                   ((OS.Version.Major == 6 && OS.Version.Minor >= 2) || (OS.Version.Major > 6)));
                    s_CheckedOSWin8OrAbove = true;
                }
                return s_IsWindows8OrAbove;
            }
        }

#if FEATURE_COMINTEROP
        private static volatile bool s_WinRTSupported;
        private static volatile bool s_CheckedWinRT;

        // Does the current version of Windows have Windows Runtime suppport?
        internal static bool IsWinRTSupported {
            [SecuritySafeCritical]
            get {
                if (!s_CheckedWinRT) {
                    s_WinRTSupported = WinRTSupported();
                    s_CheckedWinRT = true;
                }

                return s_WinRTSupported;
            }
        }

        [SecurityCritical]
        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinRTSupported();
#endif // FEATURE_COMINTEROP

#endif // FEATURE_CORESYSTEM

        [System.Security.SecurityCritical]  // auto-generated
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern bool GetVersion(Microsoft.Win32.Win32Native.OSVERSIONINFO  osVer);

        [System.Security.SecurityCritical]  // auto-generated
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern bool GetVersionEx(Microsoft.Win32.Win32Native.OSVERSIONINFOEX  osVer);


        /*==================================StackTrace==================================
        **Action:
        **Returns:
        **Arguments:
        **Exceptions:
        ==============================================================================*/
        public static String StackTrace {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                Contract.Ensures(Contract.Result<String>() != null);

                new EnvironmentPermission(PermissionState.Unrestricted).Demand();
                return GetStackTrace(null, true);
            }
        }

        [System.Security.SecurityCritical] // auto-generated
        internal static String GetStackTrace(Exception e, bool needFileInfo)
        {
            // Note: Setting needFileInfo to true will start up COM and set our
            // apartment state.  Try to not call this when passing "true" 
            // before the EE's ExecuteMainMethod has had a chance to set up the
            // apartment state.  -- 
            StackTrace st;
            if (e == null)
                st = new StackTrace(needFileInfo);
            else
                st = new StackTrace(e, needFileInfo);

            // Do no include a trailing newline for backwards compatibility
            return st.ToString( System.Diagnostics.StackTrace.TraceFormat.Normal );
        }

        [System.Security.SecuritySafeCritical]  // auto-generated
        private static void InitResourceHelper() {
            // Only the default AppDomain should have a ResourceHelper.  All calls to 
            // GetResourceString from any AppDomain delegate to GetResourceStringLocal 
            // in the default AppDomain via the fcall GetResourceFromDefault.

            bool tookLock = false;
            RuntimeHelpers.PrepareConstrainedRegions();
            try {

                Monitor.Enter(Environment.InternalSyncObject, ref tookLock);

                if (m_resHelper == null) {
                    ResourceHelper rh = new ResourceHelper(System.CoreLib.Name);

                    System.Threading.Thread.MemoryBarrier();
                    m_resHelper =rh;
                }
            }
            finally {
                if (tookLock)
                    Monitor.Exit(Environment.InternalSyncObject);
            }
        }

        // Looks up the resource string value for key.
        // 
        // if you change this method's signature then you must change the code that calls it
        // in excep.cpp and probably you will have to visit mscorlib.h to add the new signature
        // as well as metasig.h to create the new signature type
        [System.Security.SecurityCritical] // auto-generated
        // NoInlining causes the caller and callee to not be inlined in mscorlib as it is an assumption of StackCrawlMark use
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static String GetResourceStringLocal(String key) {
            if (m_resHelper == null)
                InitResourceHelper();

            return m_resHelper.GetResourceString(key);
        }

        [System.Security.SecuritySafeCritical]  // auto-generated
        internal static String GetResourceString(String key) {
            return GetResourceStringLocal(key);
        }

        // The reason the following overloads exist are to reduce code bloat.
        // Since GetResourceString is basically only called when exceptions are
        // thrown, we want the code size to be as small as possible.
        // Using the params object[] overload works against this since the
        // initialization of the array is done inline in the caller at the IL
        // level. So we have overloads that simply wrap the params one, and
        // the methods they call through to are tagged as NoInlining. 
        // In mscorlib NoInlining causes the caller and callee to not be inlined
        // as it is an assumption of StackCrawlMark use so it is not added 
        // directly to these methods, but to the ones they call.
        // That way they do not bloat either the IL or the generated asm.

        internal static string GetResourceString(string key, object val0)
        {
            return GetResourceStringFormatted(key, new object[] { val0 });
        }

        internal static string GetResourceString(string key, object val0, object val1)
        {
            return GetResourceStringFormatted(key, new object[] { val0, val1 });
        }

        internal static string GetResourceString(string key, object val0, object val1, object val2)
        {
            return GetResourceStringFormatted(key, new object[] { val0, val1, val2 });
        }

        internal static string GetResourceString(string key, object val0, object val1, object val2, object val3)
        {
            return GetResourceStringFormatted(key, new object[] { val0, val1, val2, val3 });
        }

        internal static string GetResourceString(string key, object val0, object val1, object val2, object val3, object val4)
        {
            return GetResourceStringFormatted(key, new object[] { val0, val1, val2, val3, val4 });
        }

        internal static string GetResourceString(string key, object val0, object val1, object val2, object val3, object val4, object val5)
        {
            return GetResourceStringFormatted(key, new object[] { val0, val1, val2, val3, val4, val5 });
        }

        internal static String GetResourceString(string key, params object[] values)
        {
            return GetResourceStringFormatted(key, values);
        }

        // NoInlining causes the caller and callee to not be inlined in mscorlib as it is an assumption of StackCrawlMark use
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static String GetResourceStringFormatted(string key, params object[] values)
        {
            string rs = GetResourceString(key);
            return String.Format(CultureInfo.CurrentCulture, rs, values);
        }

        // The following two internal methods are not used anywhere within the framework,
        // but are being kept around as external platforms built on top of us have taken 
        // dependency by using private reflection on them for getting system resource strings 
        private static String GetRuntimeResourceString(String key) {
            return GetResourceString(key);
        }

        private static String GetRuntimeResourceString(String key, params Object[] values) {
            return GetResourceStringFormatted(key,values);
        }

        public static bool Is64BitProcess {
            get {
#if BIT64
                    return true;
#else // 32
                    return false;
#endif
            }
        }

        public static bool Is64BitOperatingSystem {
            [System.Security.SecuritySafeCritical]
            get {
#if BIT64
                    // 64-bit programs run only on 64-bit
                    return true;
#else // 32
                    bool isWow64; // WinXP SP2+ and Win2k3 SP1+
                    return Win32Native.DoesWin32MethodExist(Win32Native.KERNEL32, "IsWow64Process")
                        && Win32Native.IsWow64Process(Win32Native.GetCurrentProcess(), out isWow64)
                        && isWow64;
#endif
            }
        }

        public static extern bool HasShutdownStarted {
        [System.Security.SecuritySafeCritical]  // auto-generated
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
            get;
        }

        public static string UserName {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                new EnvironmentPermission(EnvironmentPermissionAccess.Read,"UserName").Demand();

                StringBuilder sb = new StringBuilder(256);
                int size = sb.Capacity;
                if (Win32Native.GetUserName(sb, ref size))
                {
                    return sb.ToString();
                }
                return String.Empty;
            }
        }

        public static bool UserInteractive
        {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                return true;
            }
        }
        
        [System.Security.SecuritySafeCritical]  // auto-generated
        public static string GetFolderPath(SpecialFolder folder) {
            if (!Enum.IsDefined(typeof(SpecialFolder), folder))
                throw new ArgumentException(Environment.GetResourceString("Arg_EnumIllegalVal", (int)folder));
            Contract.EndContractBlock();

            return InternalGetFolderPath(folder, SpecialFolderOption.None);
        }

        [System.Security.SecuritySafeCritical]  // auto-generated
        public static string GetFolderPath(SpecialFolder folder, SpecialFolderOption option) {
            if (!Enum.IsDefined(typeof(SpecialFolder),folder))
                throw new ArgumentException(Environment.GetResourceString("Arg_EnumIllegalVal", (int)folder));
            if (!Enum.IsDefined(typeof(SpecialFolderOption),option))
                throw new ArgumentException(Environment.GetResourceString("Arg_EnumIllegalVal", (int)option));
            Contract.EndContractBlock();

            return InternalGetFolderPath(folder, option);
        }

        [System.Security.SecurityCritical]
        internal static string UnsafeGetFolderPath(SpecialFolder folder)
        {
            return InternalGetFolderPath(folder, SpecialFolderOption.None, suppressSecurityChecks: true);
        }

        [System.Security.SecurityCritical]
        private static string InternalGetFolderPath(SpecialFolder folder, SpecialFolderOption option, bool suppressSecurityChecks = false)
        {
#if FEATURE_CORESYSTEM
            // This is currently customized for Windows Phone since CoreSystem doesn't support
            // SHGetFolderPath. The allowed folder values are based on the version of .NET CF WP7 was using.
            switch (folder)
            {
                case SpecialFolder.System:
                    return SystemDirectory;
                case SpecialFolder.ApplicationData:
                case SpecialFolder.Favorites:
                case SpecialFolder.Programs:
                case SpecialFolder.StartMenu:
                case SpecialFolder.Startup:
                case SpecialFolder.Personal:
                    throw new PlatformNotSupportedException();
                default:
                    throw new PlatformNotSupportedException();
            }
#else // FEATURE_CORESYSTEM

            StringBuilder sb = new StringBuilder(Path.MaxPath);
            int hresult = Win32Native.SHGetFolderPath(IntPtr.Zero,                    /* hwndOwner: [in] Reserved */
                                                      ((int)folder | (int)option),    /* nFolder:   [in] CSIDL    */
                                                      IntPtr.Zero,                    /* hToken:    [in] access token */
                                                      Win32Native.SHGFP_TYPE_CURRENT, /* dwFlags:   [in] retrieve current path */
                                                      sb);                            /* pszPath:   [out]resultant path */
            String s;
            if (hresult < 0)
            {
                switch (hresult)
                {
                default:
                    // The previous incarnation threw away all errors. In order to limit
                    // breaking changes, we will be permissive about these errors
                    // instead of calling ThowExceptionForHR.
                    //Runtime.InteropServices.Marshal.ThrowExceptionForHR(hresult);
                    break;
                case __HResults.COR_E_PLATFORMNOTSUPPORTED:
                    // This one error is the one we do want to throw.

                    throw new PlatformNotSupportedException();
                }

                // SHGetFolderPath does not initialize the output buffer on error
                s = String.Empty;
            }
            else
            {
                s = sb.ToString();
            }

            if (!suppressSecurityChecks)
            {
                // On CoreCLR we can check with the host if we're not trying to use any special options.
                // Otherwise, we need to do a full demand since hosts aren't expecting to handle requests to
                // create special folders.
                if (option == SpecialFolderOption.None)
                {
                    FileSecurityState state = new FileSecurityState(FileSecurityStateAccess.PathDiscovery, String.Empty, s);
                    state.EnsureState();
                }
                else
                {
                    new FileIOPermission(FileIOPermissionAccess.PathDiscovery, s).Demand();
                }
            }
            return s;
#endif // FEATURE_CORESYSTEM
        }

        public static string UserDomainName
        {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get {
                new EnvironmentPermission(EnvironmentPermissionAccess.Read,"UserDomain").Demand();

                byte[] sid = new byte[1024];
                int sidLen = sid.Length;
                StringBuilder domainName = new StringBuilder(1024);
                uint domainNameLen = (uint) domainName.Capacity;
                int peUse;

                byte ret = Win32Native.GetUserNameEx(Win32Native.NameSamCompatible, domainName, ref domainNameLen);
                    if (ret == 1) {                        
                        string samName = domainName.ToString();
                        int index = samName.IndexOf('\\');
                        if( index != -1) {
                            return samName.Substring(0, index);
                        }
                    }
                    domainNameLen = (uint) domainName.Capacity;                    
                    
                bool success = Win32Native.LookupAccountName(null, UserName, sid, ref sidLen, domainName, ref domainNameLen, out peUse);
                if (!success)  {
                    int errorCode = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(Win32Native.GetMessage(errorCode));
                }

                return domainName.ToString();
            }
        }

        public enum SpecialFolderOption {
            None        = 0,
            Create      = Win32Native.CSIDL_FLAG_CREATE,
            DoNotVerify = Win32Native.CSIDL_FLAG_DONT_VERIFY,
        }
        
//////!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!////////
//////!!!!!! Keep the following locations synchronized            !!!!!!////////
//////!!!!!! 1) ndp\clr\src\BCL\Microsoft\Win32\Win32Native.cs    !!!!!!////////
//////!!!!!! 2) ndp\clr\src\BCL\System\Environment.cs             !!!!!!////////
//////!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!////////
        [ComVisible(true)]
        public enum SpecialFolder {
            //  
            //      Represents the file system directory that serves as a common repository for
            //       application-specific data for the current, roaming user. 
            //     A roaming user works on more than one computer on a network. A roaming user's 
            //       profile is kept on a server on the network and is loaded onto a system when the
            //       user logs on. 
            //  
            ApplicationData =  Win32Native.CSIDL_APPDATA,
            //  
            //      Represents the file system directory that serves as a common repository for application-specific data that
            //       is used by all users. 
            //  
            CommonApplicationData =  Win32Native.CSIDL_COMMON_APPDATA,
            //  
            //     Represents the file system directory that serves as a common repository for application specific data that
            //       is used by the current, non-roaming user. 
            //  
            LocalApplicationData =  Win32Native.CSIDL_LOCAL_APPDATA,
            //  
            //     Represents the file system directory that serves as a common repository for Internet
            //       cookies. 
            //  
            Cookies =  Win32Native.CSIDL_COOKIES,
            Desktop = Win32Native.CSIDL_DESKTOP,
            //  
            //     Represents the file system directory that serves as a common repository for the user's
            //       favorite items. 
            //  
            Favorites =  Win32Native.CSIDL_FAVORITES,
            //  
            //     Represents the file system directory that serves as a common repository for Internet
            //       history items. 
            //  
            History =  Win32Native.CSIDL_HISTORY,
            //  
            //     Represents the file system directory that serves as a common repository for temporary 
            //       Internet files. 
            //  
            InternetCache =  Win32Native.CSIDL_INTERNET_CACHE,
            //  
            //      Represents the file system directory that contains
            //       the user's program groups. 
            //  
            Programs =  Win32Native.CSIDL_PROGRAMS,
            MyComputer =  Win32Native.CSIDL_DRIVES,
            MyMusic =  Win32Native.CSIDL_MYMUSIC,
            MyPictures = Win32Native.CSIDL_MYPICTURES,
            //      "My Videos" folder
            MyVideos = Win32Native.CSIDL_MYVIDEO,
            //  
            //     Represents the file system directory that contains the user's most recently used
            //       documents. 
            //  
            Recent =  Win32Native.CSIDL_RECENT,
            //  
            //     Represents the file system directory that contains Send To menu items. 
            //  
            SendTo =  Win32Native.CSIDL_SENDTO,
            //  
            //     Represents the file system directory that contains the Start menu items. 
            //  
            StartMenu =  Win32Native.CSIDL_STARTMENU,
            //  
            //     Represents the file system directory that corresponds to the user's Startup program group. The system
            //       starts these programs whenever any user logs on to Windows NT, or
            //       starts Windows 95 or Windows 98. 
            //  
            Startup =  Win32Native.CSIDL_STARTUP,
            //  
            //     System directory.
            //  
            System =  Win32Native.CSIDL_SYSTEM,
            //  
            //     Represents the file system directory that serves as a common repository for document
            //       templates. 
            //  
            Templates =  Win32Native.CSIDL_TEMPLATES,
            //  
            //     Represents the file system directory used to physically store file objects on the desktop.
            //       This should not be confused with the desktop folder itself, which is
            //       a virtual folder. 
            //  
            DesktopDirectory =  Win32Native.CSIDL_DESKTOPDIRECTORY,
            //  
            //     Represents the file system directory that serves as a common repository for documents. 
            //  
            Personal =  Win32Native.CSIDL_PERSONAL, 
            //          
            // "MyDocuments" is a better name than "Personal"
            //
            MyDocuments = Win32Native.CSIDL_PERSONAL,
            //  
            //     Represents the program files folder. 
            //  
            ProgramFiles =  Win32Native.CSIDL_PROGRAM_FILES,
            //  
            //     Represents the folder for components that are shared across applications. 
            //  
            CommonProgramFiles =  Win32Native.CSIDL_PROGRAM_FILES_COMMON,
        }

        public static int CurrentManagedThreadId
        {
            [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
            get
            {
                return Thread.CurrentThread.ManagedThreadId;
            }
        }

        internal static extern int CurrentProcessorNumber
        {
            [System.Security.SecuritySafeCritical]
            [MethodImplAttribute(MethodImplOptions.InternalCall)]
            get;
        }

        public static string GetEnvironmentVariable(string variable)
        {
            if (variable == null)
            {
                throw new ArgumentNullException(nameof(variable));
            }

            // separated from the EnvironmentVariableTarget overload to help with tree shaking in common case
            return GetEnvironmentVariableCore(variable);
        }

        public static string GetEnvironmentVariable(string variable, EnvironmentVariableTarget target)
        {
            if (variable == null)
            {
                throw new ArgumentNullException(nameof(variable));
            }

            ValidateTarget(target);

            return GetEnvironmentVariableCore(variable, target);
        }

        public static IDictionary GetEnvironmentVariables()
        {
            // separated from the EnvironmentVariableTarget overload to help with tree shaking in common case
            return GetEnvironmentVariablesCore();
        }

        public static IDictionary GetEnvironmentVariables(EnvironmentVariableTarget target)
        {
            ValidateTarget(target);

            return GetEnvironmentVariablesCore(target);
        }

        public static void SetEnvironmentVariable(string variable, string value)
        {
            ValidateVariableAndValue(variable, ref value);

            // separated from the EnvironmentVariableTarget overload to help with tree shaking in common case
            SetEnvironmentVariableCore(variable, value);
        }

        public static void SetEnvironmentVariable(string variable, string value, EnvironmentVariableTarget target)
        {
            ValidateVariableAndValue(variable, ref value);
            ValidateTarget(target);

            SetEnvironmentVariableCore(variable, value, target);
        }

        private static void ValidateVariableAndValue(string variable, ref string value)
        {
            const int MaxEnvVariableValueLength = 32767;

            if (variable == null)
            {
                throw new ArgumentNullException(nameof(variable));
            }
            if (variable.Length == 0)
            {
                throw new ArgumentException(GetResourceString("Argument_StringZeroLength"), nameof(variable));
            }
            if (variable[0] == '\0')
            {
                throw new ArgumentException(GetResourceString("Argument_StringFirstCharIsZero"), nameof(variable));
            }
            if (variable.Length >= MaxEnvVariableValueLength)
            {
                throw new ArgumentException(GetResourceString("Argument_LongEnvVarValue"), nameof(variable));
            }
            if (variable.IndexOf('=') != -1)
            {
                throw new ArgumentException(GetResourceString("Argument_IllegalEnvVarName"), nameof(variable));
            }

            if (string.IsNullOrEmpty(value) || value[0] == '\0')
            {
                // Explicitly null out value if it's empty
                value = null;
            }
            else if (value.Length >= MaxEnvVariableValueLength)
            {
                throw new ArgumentException(GetResourceString("Argument_LongEnvVarValue"), nameof(value));
            }
        }

        private static void ValidateTarget(EnvironmentVariableTarget target)
        {
            if (target != EnvironmentVariableTarget.Process &&
                target != EnvironmentVariableTarget.Machine &&
                target != EnvironmentVariableTarget.User)
            {
                throw new ArgumentOutOfRangeException(nameof(target), target, SR.Format(GetResourceString("Arg_EnumIllegalVal"), target));
            }
        }

        private static Dictionary<string, string> GetRawEnvironmentVariables()
        {
            // Format for GetEnvironmentStrings is:
            // (=HiddenVar=value\0 | Variable=value\0)* \0
            // See the description of Environment Blocks in MSDN's
            // CreateProcess page (null-terminated array of null-terminated strings).
            // Note the =HiddenVar's aren't always at the beginning.

            // Copy strings out, parsing into pairs and inserting into the table.
            // The first few environment variable entries start with an '='.
            // The current working directory of every drive (except for those drives
            // you haven't cd'ed into in your DOS window) are stored in the 
            // environment block (as =C:=pwd) and the program's exit code is 
            // as well (=ExitCode=00000000).

            var results = new Dictionary<string, string>();
            char[] block = GetEnvironmentCharArray();
            for (int i = 0; i < block.Length; i++)
            {
                int startKey = i;

                // Skip to key. On some old OS, the environment block can be corrupted.
                // Some will not have '=', so we need to check for '\0'. 
                while (block[i] != '=' && block[i] != '\0') i++;
                if (block[i] == '\0') continue;

                // Skip over environment variables starting with '='
                if (i - startKey == 0)
                {
                    while (block[i] != 0) i++;
                    continue;
                }

                string key = new string(block, startKey, i - startKey);
                i++;  // skip over '='

                int startValue = i;
                while (block[i] != 0) i++; // Read to end of this entry 
                string value = new string(block, startValue, i - startValue); // skip over 0 handled by for loop's i++

                results[key] = value;
            }
            return results;
        }
    }
}
