using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("LiteDesktopSwitcher")]
[assembly: AssemblyProduct("LiteDesktopSwitcher")]
[assembly: AssemblyDescription("Use Alt+1 through Alt+9 to switch Windows virtual desktops.")]
[assembly: AssemblyCopyright("Copyright © 2026 LiteDesktopSwitcher contributors")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

internal static class Program
{
    private const string MutexName = "LiteDesktopSwitcher_Singleton";
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_TIMER = 0x0113;
    private const int HotKeyBase = 0x4C40;
    private static readonly UIntPtr TimerId = new UIntPtr(0x4C44);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Run();

        bool created;
        using (Mutex mutex = new Mutex(true, MutexName, out created))
        {
            if (!created)
                return 0;

            Logger.Write("启动；Windows build=" + Native.GetWindowsBuild());
            DesktopWorker worker = new DesktopWorker();
            HotKeySet hotKeys = new HotKeySet();
            worker.Start();
            worker.WaitUntilReady(2500);

            try
            {
                hotKeys.Update(worker.DesktopCount);
                Native.SetTimer(IntPtr.Zero, TimerId, 1000, IntPtr.Zero);

                Native.MSG message;
                int result;
                while ((result = Native.GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.message == WM_HOTKEY)
                    {
                        int id = unchecked((int)message.wParam.ToUInt64());
                        int target = id - HotKeyBase;
                        if (target >= 0 && target < 9 && !Native.HasExtraModifier())
                            worker.RequestSwitch(target);
                    }
                    else if (message.message == WM_TIMER && message.wParam == TimerId)
                    {
                        hotKeys.Update(worker.DesktopCount);
                    }
                }

                if (result < 0)
                    Logger.Write("消息循环失败，Win32=" + Marshal.GetLastWin32Error());
            }
            finally
            {
                Native.KillTimer(IntPtr.Zero, TimerId);
                hotKeys.Dispose();
                worker.Dispose();
                Logger.Write("退出");
                try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
            }
        }
        return 0;
    }

    private sealed class HotKeySet : IDisposable
    {
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_NOREPEAT = 0x4000;
        private readonly bool[] registered = new bool[9];
        private readonly long[] retryAfter = new long[9];
        private int wantedCount = -1;
        private bool conflictShown;

        public void Update(int desktopCount)
        {
            int wanted = desktopCount > 0 ? Math.Min(desktopCount, 9) : 9;
            long now = Environment.TickCount;

            for (int i = wanted; i < 9; i++)
            {
                if (registered[i])
                {
                    Native.UnregisterHotKey(IntPtr.Zero, HotKeyBase + i);
                    registered[i] = false;
                }
            }

            for (int i = 0; i < wanted; i++)
            {
                if (registered[i] || (retryAfter[i] != 0 && unchecked(now - retryAfter[i]) < 0))
                    continue;

                if (Native.RegisterHotKey(IntPtr.Zero, HotKeyBase + i, MOD_ALT | MOD_NOREPEAT, (uint)('1' + i)))
                {
                    registered[i] = true;
                    retryAfter[i] = 0;
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    retryAfter[i] = unchecked(now + 30000);
                    Logger.Write("Alt+" + (i + 1) + " 注册失败，Win32=" + error);
                    if (!conflictShown)
                    {
                        conflictShown = true;
                        Native.MessageBox(IntPtr.Zero,
                            "Alt+" + (i + 1) + " 已被其他程序占用。该快捷键不会生效，程序将在 30 秒后重试。\n\n详情见日志：\n" + Logger.Path,
                            "LiteDesktopSwitcher", 0x30);
                    }
                }
            }

            if (wanted != wantedCount)
            {
                wantedCount = wanted;
                Logger.Write("桌面数变化；当前注册 Alt+1.." + wanted);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < registered.Length; i++)
            {
                if (registered[i])
                    Native.UnregisterHotKey(IntPtr.Zero, HotKeyBase + i);
            }
        }
    }
}

internal sealed class DesktopWorker : IDisposable
{
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly ManualResetEvent ready = new ManualResetEvent(false);
    private readonly IntPtr[] waitHandles;
    private int pendingTarget = -1;
    private int desktopCount = -1;
    private int stopping;
    private int workerThreadId;
    private int pumpedMessageCount;

    public DesktopWorker()
    {
        waitHandles = new IntPtr[] { wake.SafeWaitHandle.DangerousGetHandle() };
        thread = new Thread(ThreadMain);
        thread.IsBackground = true;
        thread.Name = "Desktop COM worker";
        thread.SetApartmentState(ApartmentState.STA);
    }

    public int DesktopCount { get { return Thread.VolatileRead(ref desktopCount); } }
    public bool IsAlive { get { return thread.IsAlive; } }
    public uint WorkerThreadId { get { return unchecked((uint)Thread.VolatileRead(ref workerThreadId)); } }
    public int PumpedMessageCount { get { return Thread.VolatileRead(ref pumpedMessageCount); } }

    public void Start() { thread.Start(); }
    public bool WaitUntilReady(int milliseconds) { return ready.WaitOne(milliseconds); }

    public void RequestSwitch(int target)
    {
        Interlocked.Exchange(ref pendingTarget, target);
        wake.Set();
    }

    private void ThreadMain()
    {
        Interlocked.Exchange(ref workerThreadId, unchecked((int)Native.GetCurrentThreadId()));
        int initHr = Native.CoInitializeEx(IntPtr.Zero, 0x2);
        bool comInitialized = initHr >= 0;

        try
        {
            if (!comInitialized && initHr != unchecked((int)0x80010106))
                Logger.Write("COM 初始化失败：" + Native.HResult(initHr));

            while (Thread.VolatileRead(ref stopping) == 0)
            {
                DesktopBackend backend = new DesktopBackend();
                try
                {
                    RefreshCount(backend, true);
                    ready.Set();
                    RunWorkerSession(backend);
                }
                catch (Exception ex)
                {
                    // 单次 COM/注册表异常只销毁当前后端；线程继续存活并重建会话。
                    Interlocked.Exchange(ref desktopCount, -1);
                    Logger.Write("工作会话异常，500ms 后自动重建：" + ex);
                }
                finally
                {
                    ready.Set();
                    try { backend.Dispose(); }
                    catch (Exception ex) { Logger.Write("释放异常后端失败：" + ex.Message); }
                }

                if (Thread.VolatileRead(ref stopping) == 0)
                    WaitForWakeOrMessages(500);
            }
        }
        finally
        {
            Interlocked.Exchange(ref workerThreadId, 0);
            ready.Set();
            if (comInitialized)
                Native.CoUninitialize();
        }
    }

    private void RunWorkerSession(DesktopBackend backend)
    {
        while (Thread.VolatileRead(ref stopping) == 0)
        {
            if (Interlocked.CompareExchange(ref pendingTarget, -1, -1) < 0)
                WaitForWakeOrMessages(1000);
            if (Thread.VolatileRead(ref stopping) != 0)
                return;

            RefreshCount(backend, false);
            int target = Interlocked.Exchange(ref pendingTarget, -1);
            if (target >= 0)
            {
                SwitchVerified(backend, target);
                // 切换期间到达的多个请求只保留最后一个，防止 Explorer 动画堆积。
                while ((target = Interlocked.Exchange(ref pendingTarget, -1)) >= 0)
                    SwitchVerified(backend, target);
            }
        }
    }

    private bool WaitForWakeOrMessages(int timeoutMilliseconds)
    {
        const uint WAIT_OBJECT_0 = 0;
        const uint WAIT_TIMEOUT = 258;
        const uint WAIT_FAILED = 0xFFFFFFFF;
        const uint QS_ALLINPUT = 0x04FF;
        const uint MWMO_INPUTAVAILABLE = 0x0004;

        Stopwatch watch = Stopwatch.StartNew();
        while (Thread.VolatileRead(ref stopping) == 0)
        {
            long remainingLong = timeoutMilliseconds - watch.ElapsedMilliseconds;
            uint remaining = remainingLong > 0 ? (uint)Math.Min(remainingLong, UInt32.MaxValue) : 0;
            uint result = Native.MsgWaitForMultipleObjectsEx(1, waitHandles, remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result == WAIT_OBJECT_0)
            {
                PumpPendingMessages();
                return true;
            }
            if (result == WAIT_OBJECT_0 + 1)
            {
                PumpPendingMessages();
                if (watch.ElapsedMilliseconds >= timeoutMilliseconds)
                    return false;
                continue;
            }
            if (result == WAIT_TIMEOUT)
                return false;
            if (result == WAIT_FAILED)
                throw new InvalidOperationException("MsgWaitForMultipleObjectsEx 失败，Win32=" + Marshal.GetLastWin32Error());
            throw new InvalidOperationException("MsgWaitForMultipleObjectsEx 返回异常值=" + result);
        }
        return true;
    }

    private void PumpPendingMessages()
    {
        Native.MSG message;
        while (Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 0x0001))
        {
            Interlocked.Increment(ref pumpedMessageCount);
            if (message.message == 0x0012) // WM_QUIT 没有窗口过程，不应继续分发。
                continue;
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    private void RefreshCount(DesktopBackend backend, bool forceInit)
    {
        int registryCount;
        int current;
        bool registryAvailable = VirtualDesktopRegistry.TryRead(out registryCount, out current);
        int previousCount = DesktopCount;

        // 私有 COM 管理器可能长期返回创建/删除桌面前的旧快照。
        // 注册表拓扑发生变化时必须丢弃旧管理器，否则新增桌面的热键永远不会注册。
        bool topologyChanged = NeedsTopologyRefresh(previousCount, registryAvailable, registryCount);
        if (topologyChanged)
        {
            Logger.Write("检测到桌面拓扑变化；桌面数 " + previousCount + " -> " + registryCount + "，重建 COM 后端");
            backend.Reset();
            forceInit = true;
        }

        int comCount;
        int hr;
        bool comAvailable = backend.TryGetCount(forceInit, out comCount, out hr);
        int effectiveCount = ResolveDesktopCount(comAvailable, comCount, registryAvailable, registryCount);
        if (topologyChanged && comAvailable && comCount != registryCount)
            Logger.Write("COM 重建后桌面数仍与注册表不一致；COM=" + comCount + "，注册表=" + registryCount + "，热键范围采用注册表拓扑");
        Interlocked.Exchange(ref desktopCount, effectiveCount);
    }

    internal static bool NeedsTopologyRefresh(int previousCount, bool registryAvailable, int registryCount)
    {
        return previousCount > 0 && registryAvailable && registryCount > 0 && previousCount != registryCount;
    }

    internal static int ResolveDesktopCount(bool comAvailable, int comCount, bool registryAvailable, int registryCount)
    {
        if (registryAvailable && registryCount > 0)
            return registryCount;
        return comAvailable && comCount > 0 ? comCount : -1;
    }

    private void SwitchVerified(DesktopBackend backend, int target)
    {
        int count;
        int current;
        bool currentKnown;
        VirtualDesktopRegistry.TryRead(out count, out current, out currentKnown);
        if (count <= 0)
            count = DesktopCount;

        if (count > 0 && target >= count)
        {
            Logger.Write("忽略不存在的桌面 " + (target + 1) + "；当前桌面数=" + count);
            Native.MessageBeep(0x30);
            return;
        }
        if (currentKnown && current == target)
            return;

        int hr;
        bool issued = backend.TrySwitch(target, out hr);
        bool verified = issued && WaitForTarget(target, 900);
        if (issued && verified)
            return;

        Logger.Write("COM 切换首次失败；目标=" + (target + 1) + "，HRESULT=" + Native.HResult(hr) +
            (issued ? "，结果未验证" : String.Empty));

        backend.Reset();
        issued = backend.TrySwitch(target, out hr);
        verified = issued && WaitForTarget(target, 900);
        if (issued && verified)
        {
            Logger.Write("重建 COM 后切换成功；目标=" + (target + 1));
            return;
        }

        int refreshedCount;
        int refreshedCurrent;
        bool refreshedCurrentKnown;
        VirtualDesktopRegistry.TryRead(out refreshedCount, out refreshedCurrent, out refreshedCurrentKnown);
        if (refreshedCount > 0)
            count = refreshedCount;
        if (refreshedCurrentKnown)
            current = refreshedCurrent;

        Logger.Write("COM 重试失败；目标=" + (target + 1) + "，HRESULT=" + Native.HResult(hr) + "；启用系统快捷键回退");
        if (!NativeDesktopFallback.TrySwitch(current, target, count, PumpForWorker) || !WaitForTarget(target, 1500))
        {
            Logger.Write("切换最终失败；目标=" + (target + 1));
            Native.MessageBeep(0x10);
        }
    }

    private bool WaitForTarget(int target, int timeoutMilliseconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        bool currentObserved = false;
        while (watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            int count;
            int current;
            bool currentKnown;
            if (VirtualDesktopRegistry.TryRead(out count, out current, out currentKnown) && currentKnown)
            {
                currentObserved = true;
                if (current == target)
                    return true;
            }
            WaitForWakeOrMessages(30);
        }
        // 极少数系统不公开 CurrentVirtualDesktop；这种情况下只能以成功的 HRESULT 为准。
        return !currentObserved;
    }

    private void PumpForWorker(int milliseconds)
    {
        WaitForWakeOrMessages(milliseconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            wake.Set();
            if (!thread.Join(2500))
            {
                Logger.Write("工作线程未能在 2.5 秒内退出；保留同步句柄直到进程结束");
                return;
            }
        }
        wake.Dispose();
        ready.Dispose();
    }
}

internal sealed class DesktopBackend : IDisposable
{
    private static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid SID_VirtualDesktopManagerInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid CLSID_VirtualDesktopManager = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IServiceProvider = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid IID_ManagerWin10 = new Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6");
    private static readonly Guid IID_ManagerWin11Early = new Guid("B2F925B9-5A0F-4D2E-9F4D-2B1507593C10");
    private static readonly Guid IID_ManagerWin11 = new Guid("53F5CA0B-158F-4124-900C-057158060B27");
    private static readonly Guid IID_DesktopWin10 = new Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4");
    private static readonly Guid IID_DesktopWin11Early = new Guid("536D3495-B208-4CC9-AE26-DE8111275BF8");
    private static readonly Guid IID_DesktopWin11 = new Guid("3F07F4BE-B107-441A-AF0F-39D82529072C");

    private IntPtr manager;
    private ManagerLayout layout;
    private long nextInitialization;
    private string backendName = "无";

    private sealed class ManagerLayout
    {
        public Guid ManagerIid;
        public Guid DesktopIid;
        public int GetDesktopsSlot;
        public int SwitchDesktopSlot;
        public bool HasMonitorParameter;
        public string Name;
    }

    public bool TryGetCount(bool forceInitialization, out int count, out int hr)
    {
        count = -1;
        if (!EnsureInitialized(forceInitialization, out hr))
            return false;

        IntPtr array;
        if (!TryGetDesktops(out array, out hr))
        {
            ResetOnFailure(hr);
            return false;
        }

        try
        {
            uint value;
            GetCountDelegate getCount = GetDelegate<GetCountDelegate>(array, 3);
            hr = getCount(array, out value);
            if (hr < 0)
            {
                ResetOnFailure(hr);
                return false;
            }
            count = checked((int)value);
            return true;
        }
        finally { Marshal.Release(array); }
    }

    public bool TrySwitch(int target, out int hr)
    {
        hr = unchecked((int)0x80004005);
        if (!EnsureInitialized(true, out hr))
            return false;

        IntPtr array;
        if (!TryGetDesktops(out array, out hr))
        {
            ResetOnFailure(hr);
            return false;
        }

        IntPtr desktop = IntPtr.Zero;
        try
        {
            uint count;
            GetCountDelegate getCount = GetDelegate<GetCountDelegate>(array, 3);
            hr = getCount(array, out count);
            if (hr < 0 || target < 0 || target >= count)
            {
                if (hr >= 0)
                    hr = unchecked((int)0x80070057);
                return false;
            }

            GetAtDelegate getAt = GetDelegate<GetAtDelegate>(array, 4);
            Guid desktopIid = layout.DesktopIid;
            hr = getAt(array, (uint)target, ref desktopIid, out desktop);
            if (hr < 0 || desktop == IntPtr.Zero)
                return false;

            if (layout.HasMonitorParameter)
            {
                SwitchDesktopMonitorDelegate switchDesktop = GetDelegate<SwitchDesktopMonitorDelegate>(manager, layout.SwitchDesktopSlot);
                hr = switchDesktop(manager, IntPtr.Zero, desktop);
            }
            else
            {
                SwitchDesktopDelegate switchDesktop = GetDelegate<SwitchDesktopDelegate>(manager, layout.SwitchDesktopSlot);
                hr = switchDesktop(manager, desktop);
            }
            if (hr < 0)
                ResetOnFailure(hr);
            return hr >= 0;
        }
        catch (Exception ex)
        {
            hr = Marshal.GetHRForException(ex);
            Logger.Write("调用 " + backendName + " 异常：" + ex.GetType().Name + "，" + Native.HResult(hr));
            Reset();
            return false;
        }
        finally
        {
            if (desktop != IntPtr.Zero) Marshal.Release(desktop);
            Marshal.Release(array);
        }
    }

    private bool EnsureInitialized(bool force, out int hr)
    {
        if (manager != IntPtr.Zero)
        {
            hr = 0;
            return true;
        }

        long now = Environment.TickCount;
        if (!force && nextInitialization != 0 && unchecked(now - nextInitialization) < 0)
        {
            hr = unchecked((int)0x80004005);
            return false;
        }
        nextInitialization = unchecked(now + 2000);
        return Initialize(out hr);
    }

    private bool Initialize(out int hr)
    {
        Reset();
        IntPtr shell = IntPtr.Zero;
        IntPtr provider = IntPtr.Zero;
        Guid shellClsid = CLSID_ImmersiveShell;
        Guid unknownIid = IID_IUnknown;
        hr = Native.CoCreateInstance(ref shellClsid, IntPtr.Zero, 4, ref unknownIid, out shell);
        if (hr < 0 || shell == IntPtr.Zero)
        {
            Logger.Write("ImmersiveShell 尚未就绪：" + Native.HResult(hr));
            return false;
        }

        try
        {
            Guid providerIid = IID_IServiceProvider;
            hr = Marshal.QueryInterface(shell, ref providerIid, out provider);
            if (hr < 0 || provider == IntPtr.Zero)
                return false;

            ManagerLayout[] candidates = GetCandidates();
            Guid[] serviceIds = new Guid[] { SID_VirtualDesktopManagerInternal, CLSID_VirtualDesktopManager };
            QueryServiceDelegate queryService = GetDelegate<QueryServiceDelegate>(provider, 3);

            for (int c = 0; c < candidates.Length; c++)
            {
                for (int s = 0; s < serviceIds.Length; s++)
                {
                    IntPtr candidate = IntPtr.Zero;
                    Guid service = serviceIds[s];
                    Guid iid = candidates[c].ManagerIid;
                    hr = queryService(provider, ref service, ref iid, out candidate);
                    if (hr < 0 || candidate == IntPtr.Zero)
                        continue;

                    manager = candidate;
                    layout = candidates[c];
                    int validatedCount;
                    int validationHr;
                    if (ValidateCurrent(out validatedCount, out validationHr))
                    {
                        backendName = layout.Name;
                        nextInitialization = 0;
                        Logger.Write("虚拟桌面后端=" + backendName + "；桌面数=" + validatedCount);
                        hr = 0;
                        return true;
                    }

                    Marshal.Release(manager);
                    manager = IntPtr.Zero;
                    layout = null;
                    hr = validationHr;
                }
            }
        }
        finally
        {
            if (provider != IntPtr.Zero) Marshal.Release(provider);
            if (shell != IntPtr.Zero) Marshal.Release(shell);
        }

        Logger.Write("没有匹配当前 Windows build " + Native.GetWindowsBuild() + " 的私有桌面接口；将使用系统快捷键回退。最后 HRESULT=" + Native.HResult(hr));
        return false;
    }

    private bool ValidateCurrent(out int count, out int hr)
    {
        count = -1;
        IntPtr array;
        if (!TryGetDesktops(out array, out hr))
            return false;
        try
        {
            uint value;
            GetCountDelegate getCount = GetDelegate<GetCountDelegate>(array, 3);
            hr = getCount(array, out value);
            if (hr < 0 || value == 0 || value > 1024)
                return false;
            count = (int)value;
            return true;
        }
        catch (Exception ex)
        {
            hr = Marshal.GetHRForException(ex);
            return false;
        }
        finally { Marshal.Release(array); }
    }

    private bool TryGetDesktops(out IntPtr array, out int hr)
    {
        array = IntPtr.Zero;
        try
        {
            if (layout.HasMonitorParameter)
            {
                GetDesktopsMonitorDelegate get = GetDelegate<GetDesktopsMonitorDelegate>(manager, layout.GetDesktopsSlot);
                hr = get(manager, IntPtr.Zero, out array);
            }
            else
            {
                GetDesktopsDelegate get = GetDelegate<GetDesktopsDelegate>(manager, layout.GetDesktopsSlot);
                hr = get(manager, out array);
            }
            return hr >= 0 && array != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            hr = Marshal.GetHRForException(ex);
            return false;
        }
    }

    private static ManagerLayout[] GetCandidates()
    {
        int build = Native.GetWindowsBuild();
        List<ManagerLayout> result = new List<ManagerLayout>();
        if (build >= 22000)
        {
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11, DesktopIid = IID_DesktopWin11, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win11-current" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11Early, DesktopIid = IID_DesktopWin11Early,
                GetDesktopsSlot = build >= 22489 ? 8 : 7, SwitchDesktopSlot = build >= 22489 ? 10 : 9,
                HasMonitorParameter = true, Name = build >= 22489 ? "Win11-early-22H2" : "Win11-22000" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin10, DesktopIid = IID_DesktopWin10, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win10-compatible" });
        }
        else
        {
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin10, DesktopIid = IID_DesktopWin10, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win10" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11Early, DesktopIid = IID_DesktopWin11Early, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, HasMonitorParameter = true, Name = "Win10-21H2-monitor" });
        }
        return result.ToArray();
    }

    private void ResetOnFailure(int hr)
    {
        if (hr < 0)
            Reset();
    }

    public void Reset()
    {
        if (manager != IntPtr.Zero)
        {
            Marshal.Release(manager);
            manager = IntPtr.Zero;
        }
        layout = null;
        backendName = "无";
    }

    public void Dispose() { Reset(); }

    private static T GetDelegate<T>(IntPtr instance, int slot) where T : class
    {
        IntPtr vtable = Marshal.ReadIntPtr(instance);
        IntPtr function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(function, typeof(T));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryServiceDelegate(IntPtr self, ref Guid service, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesktopsDelegate(IntPtr self, out IntPtr desktops);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesktopsMonitorDelegate(IntPtr self, IntPtr monitor, out IntPtr desktops);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SwitchDesktopDelegate(IntPtr self, IntPtr desktop);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SwitchDesktopMonitorDelegate(IntPtr self, IntPtr monitor, IntPtr desktop);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountDelegate(IntPtr self, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAtDelegate(IntPtr self, uint index, ref Guid iid, out IntPtr result);
}

internal static class VirtualDesktopRegistry
{
    private const string RootPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    public static bool TryRead(out int count, out int currentIndex)
    {
        bool currentKnown;
        return TryRead(out count, out currentIndex, out currentKnown);
    }

    public static bool TryRead(out int count, out int currentIndex, out bool currentKnown)
    {
        count = -1;
        currentIndex = -1;
        currentKnown = false;
        try
        {
            byte[] rootIds = null;
            byte[] rootCurrent = null;
            byte[] sessionIds = null;
            byte[] sessionCurrent = null;
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(RootPath))
            {
                if (root != null)
                {
                    rootIds = root.GetValue("VirtualDesktopIDs") as byte[];
                    rootCurrent = root.GetValue("CurrentVirtualDesktop") as byte[];
                }
            }

            string sessionPath = RootPath.Replace("\\VirtualDesktops", "\\SessionInfo\\" + Process.GetCurrentProcess().SessionId + "\\VirtualDesktops");
            using (RegistryKey session = Registry.CurrentUser.OpenSubKey(sessionPath))
            {
                if (session != null)
                {
                    sessionIds = session.GetValue("VirtualDesktopIDs") as byte[];
                    sessionCurrent = session.GetValue("CurrentVirtualDesktop") as byte[];
                }
            }

            // RDP/多会话下 SessionInfo 才代表当前会话；全局值仅作为兼容回退。
            bool sessionStatePresent = IsValidIdList(sessionIds) || (sessionCurrent != null && sessionCurrent.Length == 16);
            byte[] ids = IsValidIdList(sessionIds) ? sessionIds : rootIds;
            if (ids == null || ids.Length < 16 || ids.Length % 16 != 0)
                return false;
            count = ids.Length / 16;
            currentIndex = SelectCurrentIndex(ids, sessionCurrent, rootCurrent, sessionStatePresent);
            currentKnown = currentIndex >= 0;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Write("读取桌面拓扑失败：" + ex.Message);
            return false;
        }
    }

    internal static int SelectCurrentIndex(byte[] ids, byte[] sessionCurrent, byte[] rootCurrent, bool sessionStatePresent)
    {
        // Session 键一旦提供有效状态，就绝不混用可能属于其他会话的全局当前位置。
        return sessionStatePresent ? FindCurrentIndex(ids, sessionCurrent) : FindCurrentIndex(ids, rootCurrent);
    }

    private static bool IsValidIdList(byte[] ids)
    {
        return ids != null && ids.Length >= 16 && ids.Length % 16 == 0;
    }

    private static int FindCurrentIndex(byte[] ids, byte[] current)
    {
        if (!IsValidIdList(ids) || current == null || current.Length != 16)
            return -1;

        Guid currentId = new Guid(current);
        byte[] idBytes = new byte[16];
        int count = ids.Length / 16;
        for (int i = 0; i < count; i++)
        {
            Buffer.BlockCopy(ids, i * 16, idBytes, 0, 16);
            if (new Guid(idBytes) == currentId)
                return i;
        }
        return -1;
    }
}

internal static class NativeDesktopFallback
{
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_RWIN = 0x5C;

    public static bool TrySwitch(int current, int target, int count, Action<int> wait)
    {
        Stopwatch releaseWait = Stopwatch.StartNew();
        while (AnyModifierDown() && releaseWait.ElapsedMilliseconds < 2000)
            Pause(wait, 10);
        if (AnyModifierDown())
        {
            Logger.Write("回退取消：修饰键在 2 秒后仍未释放");
            return false;
        }

        if (current < 0 || count <= 0 || target < 0 || target >= count)
        {
            Logger.Write("回退取消：无法可靠确认当前桌面或目标范围；current=" + current + "，count=" + count + "，target=" + target);
            return false;
        }

        int steps = Math.Abs(target - current);
        ushort direction = target < current ? VK_LEFT : VK_RIGHT;
        for (int i = 0; i < steps; i++)
        {
            int expected = current + (direction == VK_LEFT ? -1 : 1);
            if (!SendOne(direction))
                return false;
            if (!WaitForRegistryIndex(expected, 1200, wait))
            {
                Logger.Write("回退中止：系统快捷键后未到达桌面 " + (expected + 1));
                return false;
            }
            current = expected;
            // 注册表更新可能早于动画结束；留出稳定时间，禁止动画堆积。
            Pause(wait, 250);
        }
        return current == target;
    }

    private static bool SendOne(ushort direction)
    {
        Native.INPUT[] inputs = new Native.INPUT[]
        {
            Native.KeyInput(VK_LWIN, false), Native.KeyInput(VK_CONTROL, false),
            Native.KeyInput(direction, false), Native.KeyInput(direction, true),
            Native.KeyInput(VK_CONTROL, true), Native.KeyInput(VK_LWIN, true)
        };
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        if (sent == inputs.Length) return true;
        int error = Marshal.GetLastWin32Error();

        // SendInput 可能只插入部分事件；无条件补发 key-up，避免 Win/Ctrl/方向键卡住。
        Native.INPUT[] cleanup = new Native.INPUT[]
        {
            Native.KeyInput(direction, true), Native.KeyInput(VK_CONTROL, true), Native.KeyInput(VK_LWIN, true)
        };
        uint cleanupSent = Native.SendInput((uint)cleanup.Length, cleanup, Marshal.SizeOf(typeof(Native.INPUT)));
        Logger.Write("SendInput 失败；已发送=" + sent + "/" + inputs.Length + "，Win32=" + error +
            "；释放事件=" + cleanupSent + "/" + cleanup.Length);
        return false;
    }

    private static bool WaitForRegistryIndex(int expectedIndex, int timeout, Action<int> wait)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeout)
        {
            int count;
            int current;
            bool currentKnown;
            if (VirtualDesktopRegistry.TryRead(out count, out current, out currentKnown) && currentKnown && current == expectedIndex)
                return true;
            Pause(wait, 30);
        }
        return false;
    }

    private static void Pause(Action<int> wait, int milliseconds)
    {
        if (wait != null)
            wait(milliseconds);
        else
            Thread.Sleep(milliseconds);
    }

    private static bool AnyModifierDown()
    {
        return IsDown(VK_MENU) || IsDown(VK_CONTROL) || IsDown(VK_SHIFT) || IsDown(VK_LWIN) || IsDown(VK_RWIN);
    }

    private static bool IsDown(int key) { return (Native.GetAsyncKeyState(key) & 0x8000) != 0; }
}

internal static class SelfTest
{
    public static int Run()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("LiteDesktopSwitcher 安全自检（不会切换桌面）");
        report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine("Windows build：" + Native.GetWindowsBuild());

        int registryCount;
        int current;
        bool currentKnown;
        bool registryOk = VirtualDesktopRegistry.TryRead(out registryCount, out current, out currentKnown);
        report.AppendLine("注册表拓扑：" + (registryOk
            ? "正常，桌面数=" + registryCount + "，当前桌面=" + (currentKnown ? "索引 " + current : "不可确认（不会触发盲目回退）")
            : "不可用"));

        Guid firstId = new Guid("11111111-1111-1111-1111-111111111111");
        Guid secondId = new Guid("22222222-2222-2222-2222-222222222222");
        byte[] syntheticIds = new byte[32];
        Buffer.BlockCopy(firstId.ToByteArray(), 0, syntheticIds, 0, 16);
        Buffer.BlockCopy(secondId.ToByteArray(), 0, syntheticIds, 16, 16);
        bool sessionPriorityOk = VirtualDesktopRegistry.SelectCurrentIndex(
            syntheticIds, secondId.ToByteArray(), firstId.ToByteArray(), true) == 1;
        report.AppendLine("Session 优先规则：" + (sessionPriorityOk ? "正常" : "失败"));

        bool topologyRefreshOk = DesktopWorker.NeedsTopologyRefresh(4, true, 5) &&
            !DesktopWorker.NeedsTopologyRefresh(5, true, 5) &&
            !DesktopWorker.NeedsTopologyRefresh(4, false, -1) &&
            DesktopWorker.ResolveDesktopCount(true, 4, true, 5) == 5 &&
            DesktopWorker.ResolveDesktopCount(true, 5, false, -1) == 5;
        report.AppendLine("动态桌面拓扑刷新：" + (topologyRefreshOk ? "正常" : "失败"));

        int initHr = Native.CoInitializeEx(IntPtr.Zero, 0x2);
        bool initialized = initHr >= 0;
        int count;
        int hr;
        using (DesktopBackend backend = new DesktopBackend())
        {
            bool backendOk = backend.TryGetCount(true, out count, out hr);
            report.AppendLine("私有 COM 枚举：" + (backendOk ? "正常，桌面数=" + count : "不可用，HRESULT=" + Native.HResult(hr) + "（运行时会自动回退）"));
        }
        if (initialized) Native.CoUninitialize();

        bool workerPumpOk = false;
        using (DesktopWorker worker = new DesktopWorker())
        {
            worker.Start();
            bool workerReady = worker.WaitUntilReady(2500);
            int before = worker.PumpedMessageCount;
            uint workerId = worker.WorkerThreadId;
            bool posted = workerId != 0 && Native.PostThreadMessage(workerId, 0x8044, UIntPtr.Zero, IntPtr.Zero);
            Stopwatch pumpWait = Stopwatch.StartNew();
            while (posted && worker.PumpedMessageCount == before && pumpWait.ElapsedMilliseconds < 1000)
                Thread.Sleep(10);
            workerPumpOk = workerReady && worker.IsAlive && worker.DesktopCount > 0 && posted && worker.PumpedMessageCount > before;
        }
        report.AppendLine("COM STA 消息泵：" + (workerPumpOk ? "正常" : "失败"));

        const int testId = 0x4C7F;
        bool hotKeyOk = Native.RegisterHotKey(IntPtr.Zero, testId, 0x4001, 0x87); // Alt+F24，仅注册后立即释放。
        int hotKeyError = hotKeyOk ? 0 : Marshal.GetLastWin32Error();
        if (hotKeyOk) Native.UnregisterHotKey(IntPtr.Zero, testId);
        report.AppendLine("系统热键机制：" + (hotKeyOk ? "正常" : "失败，Win32=" + hotKeyError));
        int inputSize = Marshal.SizeOf(typeof(Native.INPUT));
        report.AppendLine("SendInput x64 结构：" + (inputSize == 40 ? "正常（40 字节）" : "异常（" + inputSize + " 字节）"));

        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LiteDesktopSwitcher.selftest.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        Logger.Write(report.ToString().Replace(Environment.NewLine, " | "));
        return registryOk && sessionPriorityOk && topologyRefreshOk && workerPumpOk && hotKeyOk && inputSize == 40 ? 0 : 1;
    }
}

internal static class Logger
{
    private static readonly object Sync = new object();
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiteDesktopSwitcher", "LiteDesktopSwitcher.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                Directory.CreateDirectory(directory);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1024 * 1024)
                {
                    string old = Path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(Path, old);
                }
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        // 保留最大成员，确保 x64 INPUT 的托管尺寸与 Win32 的 40 字节完全一致。
        [FieldOffset(0)] public MOUSEINPUT mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort virtualKey;
        public ushort scanCode;
        public uint flags;
        public uint time;
        public UIntPtr extraInfo;
    }

    internal static INPUT KeyInput(ushort key, bool up)
    {
        INPUT input = new INPUT();
        input.type = 1;
        input.union.keyboard.virtualKey = key;
        input.union.keyboard.flags = up ? 0x0002u : 0u;
        return input;
    }

    internal static bool HasExtraModifier()
    {
        return IsDown(0x11) || IsDown(0x10) || IsDown(0x5B) || IsDown(0x5C);
    }

    private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

    internal static int GetWindowsBuild()
    {
        RTL_OSVERSIONINFOEX version = new RTL_OSVERSIONINFOEX();
        version.size = Marshal.SizeOf(typeof(RTL_OSVERSIONINFOEX));
        return RtlGetVersion(ref version) == 0 ? unchecked((int)version.buildNumber) : Environment.OSVersion.Version.Build;
    }

    internal static string HResult(int value) { return "0x" + unchecked((uint)value).ToString("X8"); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RTL_OSVERSIONINFOEX
    {
        public int size;
        public uint majorVersion;
        public uint minorVersion;
        public uint buildNumber;
        public uint platformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string servicePack;
        public ushort servicePackMajor;
        public ushort servicePackMinor;
        public ushort suiteMask;
        public byte productType;
        public byte reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEX version);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out MSG message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out MSG message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage([In] ref MSG message);
    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage([In] ref MSG message);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint MsgWaitForMultipleObjectsEx(uint count, [In] IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MessageBeep(uint type);
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, uint apartment);
    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
}
