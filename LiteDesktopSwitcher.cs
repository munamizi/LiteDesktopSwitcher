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
    internal const uint WM_HOTKEY = 0x0312;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_CLOSE = 0x0010;
    internal const uint WM_DESKTOP_COUNT = 0x8041;
    internal const uint WM_FOCUS_DESKTOP = 0x8042;
    private const int HotKeyBase = 0x4C40;
    private static readonly UIntPtr TimerId = new UIntPtr(0x4C44);

    /*
     * 启动单实例、执行安全自检，或请求运行中的实例退出。
     *
     * Args:
     *     args - 命令行参数，支持 --self-test 和 --exit。
     * Returns:
     *     退出码，正常为 0，窗口创建、消息循环或验证失败时为非零。
     */
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Run();
        if (args.Length > 0 && String.Equals(args[0], "--exit", StringComparison.OrdinalIgnoreCase))
            return _requestExit();

        bool created;
        using (Mutex mutex = new Mutex(true, MutexName, out created))
        {
            if (!created)
                return 0;

            Logger.Write("启动；Windows build=" + Native.GetWindowsBuild());
            try
            {
                return _run();
            }
            catch (Exception ex)
            {
                Logger.Write("主线程运行失败：" + ex.Message);
                return 1;
            }
            finally
            {
                Logger.Write("退出");
                mutex.ReleaseMutex();
            }
        }
    }

    // 关闭同一会话中的消息窗口，并等待原进程释放文件。
    private static int _requestExit()
    {
        IntPtr window = Native.FindWindowEx(MessageWindow.MessageOnlyParent, IntPtr.Zero, MessageWindow.ClassName, null);
        if (window == IntPtr.Zero)
            return 0;

        uint processId;
        if (Native.GetWindowThreadProcessId(window, out processId) == 0)
            return 0;
        try
        {
            using (Process process = Process.GetProcessById((int)processId))
            {
                if (Native.PostMessage(window, WM_CLOSE, UIntPtr.Zero, IntPtr.Zero))
                    return process.WaitForExit(5000) ? 0 : 1;
                return process.HasExited ? 0 : 1;
            }
        }
        catch (ArgumentException)
        {
            return 0; // 原实例已退出。
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Logger.Write("发送退出命令失败：" + ex.Message);
            return 1;
        }
    }

    // 在主 STA 线程分发窗口消息，按依赖关系释放热键、工作线程和窗口。
    private static int _run()
    {
        uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        if (taskbarCreated == 0)
            throw new InvalidOperationException("注册 TaskbarCreated 失败，Win32=" + Marshal.GetLastWin32Error());

        using (MessageWindow window = new MessageWindow(true))
        using (MessageWindow shellListener = new MessageWindow(false))
        using (DesktopWorker worker = new DesktopWorker(window.Handle))
        using (HotKeySet hotKeys = new HotKeySet(window.Handle))
        {
            window.OnMessage = delegate(uint message, UIntPtr wParam)
            {
                if (message == WM_HOTKEY)
                {
                    int target = unchecked((int)wParam.ToUInt64()) - HotKeyBase;
                    if (target >= 0 && target < 9 && !Native.HasExtraModifier())
                        worker.RequestSwitch(target);
                }
                else if (message == WM_DESKTOP_COUNT || (message == WM_TIMER && wParam == TimerId))
                    hotKeys.Update(worker.DesktopCount);
                else if (message == WM_FOCUS_DESKTOP)
                    worker.ActivatePendingWindow();
                else if (message == WM_CLOSE)
                    Native.PostQuitMessage(0);
                else
                    return false;
                return true;
            };

            // HWND_MESSAGE 不接收系统广播，因此用始终隐藏的顶层窗口接收 Explorer 通知。
            shellListener.OnMessage = delegate(uint message, UIntPtr wParam)
            {
                if (message != taskbarCreated)
                    return false;
                worker.RequestReconnect();
                return true;
            };

            worker.Start();
            Native.MSG msg;
            int result;
            while ((result = Native.GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            if (result < 0)
                Logger.Write("消息循环失败，Win32=" + Marshal.GetLastWin32Error());
            return result < 0 ? 1 : 0;
        }
    }

    private sealed class HotKeySet : IDisposable
    {
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_NOREPEAT = 0x4000;
        private readonly IntPtr window;
        private readonly bool[] registered = new bool[9];
        private readonly int[] retryAfter = new int[9];
        private readonly bool[] retryPending = new bool[9];
        private int wantedCount = -1;
        private bool conflictShown;
        private bool timerRunning;

        /*
         * 将热键和冲突重试定时器绑定到同一窗口。
         *
         * Args:
         *     window - 主线程创建的仅消息窗口句柄。
         * Returns:
         *     已初始化的热键集合，暂未注册任何热键。
         */
        public HotKeySet(IntPtr window)
        {
            this.window = window;
        }

        /*
         * 根据桌面数量更新热键，并按需启动或关闭冲突重试。
         *
         * 内容：
         *     注销超出当前范围的热键并清除它们的重试状态。
         *     到期后重新注册失败的热键，首次占用冲突在后台提示。
         *
         * Args:
         *     desktopCount - 当前桌面数，未知时不注册热键，最多注册前 9 个。
         * Returns:
         *     无返回值，更新窗口的热键注册和定时器。
         */
        public void Update(int desktopCount)
        {
            int wanted = Math.Min(Math.Max(desktopCount, 0), 9);
            int now = Environment.TickCount;
            bool needsRetry = false;

            for (int i = wanted; i < registered.Length; i++)
            {
                if (registered[i])
                {
                    Native.UnregisterHotKey(window, HotKeyBase + i);
                    registered[i] = false;
                }
                retryPending[i] = false;
            }

            for (int i = 0; i < wanted; i++)
            {
                if (registered[i])
                    continue;
                if (retryPending[i] && unchecked(now - retryAfter[i]) < 0)
                {
                    needsRetry = true;
                    continue;
                }

                if (Native.RegisterHotKey(window, HotKeyBase + i, MOD_ALT | MOD_NOREPEAT, (uint)('1' + i)))
                {
                    registered[i] = true;
                    retryPending[i] = false;
                    continue;
                }

                int error = Marshal.GetLastWin32Error();
                Logger.Write("Alt+" + (i + 1) + " 注册失败，Win32=" + error);
                retryPending[i] = error == 1409; // ERROR_HOTKEY_ALREADY_REGISTERED
                if (!retryPending[i])
                    continue;

                retryAfter[i] = unchecked(now + 30000);
                needsRetry = true;
                if (!conflictShown)
                {
                    conflictShown = true;
                    int number = i + 1;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Native.MessageBox(IntPtr.Zero,
                            "Alt+" + number + " 已被其他程序占用。程序会每隔 30 秒重试。\n\n详情见日志：\n" + Logger.Path,
                            "LiteDesktopSwitcher", 0x00040030); // MB_TOPMOST | MB_ICONWARNING
                    });
                }
            }

            if (needsRetry && !timerRunning)
                timerRunning = Native.SetTimer(window, TimerId, 30000, IntPtr.Zero) != UIntPtr.Zero;
            else if (!needsRetry && timerRunning)
            {
                Native.KillTimer(window, TimerId);
                timerRunning = false;
            }

            if (wanted != wantedCount)
            {
                wantedCount = wanted;
                Logger.Write(wanted == 0 ? "等待桌面计数，暂不注册热键" : "桌面数变化；所需热键 Alt+1.." + wanted);
            }
        }

        /*
         * 关闭冲突重试定时器并注销已注册的热键。
         *
         * Args:
         *     无。
         * Returns:
         *     无返回值，释放此集合绑定到窗口的资源。
         */
        public void Dispose()
        {
            if (timerRunning)
                Native.KillTimer(window, TimerId);
            for (int i = 0; i < registered.Length; i++)
            {
                if (registered[i])
                    Native.UnregisterHotKey(window, HotKeyBase + i);
            }
        }
    }
}

internal sealed class MessageWindow : IDisposable
{
    internal const string ClassName = "LiteDesktopSwitcher.MessageWindow";
    internal static readonly IntPtr MessageOnlyParent = new IntPtr(-3);
    private static readonly Native.WindowProc windowProc = _dispatch;
    private static readonly Dictionary<IntPtr, MessageWindow> windows = new Dictionary<IntPtr, MessageWindow>();
    private static readonly IntPtr module = Native.GetModuleHandle(null);
    private static bool classRegistered;

    public IntPtr Handle { get; private set; }
    public Func<uint, UIntPtr, bool> OnMessage;

    /*
     * 在调用线程创建仅消息窗口或接收系统广播的隐藏窗口。
     *
     * Args:
     *     messageOnly - 为 true 时创建 HWND_MESSAGE，为 false 时创建隐藏的顶层窗口。
     * Returns:
     *     已创建的窗口对象，Handle 保存原生句柄。
     */
    public MessageWindow(bool messageOnly)
    {
        if (!classRegistered)
        {
            Native.WNDCLASS windowClass = new Native.WNDCLASS();
            windowClass.proc = windowProc;
            windowClass.instance = module;
            windowClass.className = ClassName;
            if (Native.RegisterClass(ref windowClass) == 0)
                throw new InvalidOperationException("注册窗口类失败，Win32=" + Marshal.GetLastWin32Error());
            classRegistered = true;
        }

        Handle = Native.CreateWindowEx(0, ClassName, null, messageOnly ? 0u : 0x80000000u,
            0, 0, 0, 0, messageOnly ? MessageOnlyParent : IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("创建隐藏窗口失败，Win32=" + Marshal.GetLastWin32Error());
        windows.Add(Handle, this);
    }

    // 窗口消息回调禁止让托管异常越过 Win32 边界。
    private static IntPtr _dispatch(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam)
    {
        try
        {
            MessageWindow instance;
            if (windows.TryGetValue(window, out instance) && instance.OnMessage != null && instance.OnMessage(message, wParam))
                return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Logger.Write("窗口消息处理失败；消息=" + message + "，" + ex.Message);
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    /*
     * 在创建窗口的线程销毁窗口。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，销毁后不再调用该窗口的回调。
     */
    public void Dispose()
    {
        windows.Remove(Handle);
        Native.DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }
}
internal sealed class DesktopWorker : IDisposable
{
    private readonly Thread thread;
    private readonly IntPtr messageWindow;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private readonly ManualResetEvent ready = new ManualResetEvent(false);
    private readonly IntPtr[] wakeHandles;
    private readonly IntPtr[] registryHandles = new IntPtr[3];
    private SwitchRequest pendingRequest;
    private volatile SwitchRequest latestRequest;
    private FocusRequest pendingFocus;
    private int desktopCount = -1;
    private int stopping;
    private int reconnect;
    private int workerThreadId;
    private int pumpedMessageCount;
    private bool started;

    private sealed class SwitchRequest
    {
        public int Target;
    }

    private sealed class FocusRequest
    {
        public SwitchRequest Switch;
        public IntPtr Window;
        public bool IsShell;
    }

    /*
     * 创建负责桌面枚举和切换的 STA 工作线程。
     *
     * Args:
     *     messageWindow - 用于通知主线程的仅消息窗口，自检时可传零。
     * Returns:
     *     工作线程对象，调用 Start 后开始运行。
     */
    public DesktopWorker(IntPtr messageWindow)
    {
        this.messageWindow = messageWindow;
        wakeHandles = new IntPtr[] { wake.SafeWaitHandle.DangerousGetHandle() };
        registryHandles[0] = wakeHandles[0];
        thread = new Thread(_threadMain);
        thread.IsBackground = true;
        thread.Name = "Desktop COM worker";
        thread.SetApartmentState(ApartmentState.STA);
    }

    public int DesktopCount { get { return Thread.VolatileRead(ref desktopCount); } }
    public bool IsAlive { get { return thread.IsAlive; } }
    public uint WorkerThreadId { get { return unchecked((uint)Thread.VolatileRead(ref workerThreadId)); } }
    public int PumpedMessageCount { get { return Thread.VolatileRead(ref pumpedMessageCount); } }

    /*
     * 启动工作线程。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，工作线程开始初始化。
     */
    public void Start()
    {
        thread.Start();
        started = true;
    }

    /*
     * 等待工作线程完成第一次初始化尝试。
     *
     * Args:
     *     milliseconds - 最长等待时间，单位为毫秒。
     * Returns:
     *     初始化尝试结束时为 true，是否取得桌面数需要单独检查。
     */
    public bool WaitUntilReady(int milliseconds) { return ready.WaitOne(milliseconds); }

    /*
     * 记录最新的目标桌面，唤醒工作线程。
     *
     * Args:
     *     target - 从 0 开始的目标桌面索引。
     * Returns:
     *     无返回值，覆盖尚未处理的切换请求。
     */
    public void RequestSwitch(int target)
    {
        SwitchRequest request = new SwitchRequest { Target = target };
        latestRequest = request;
        Interlocked.Exchange(ref pendingRequest, request);
        wake.Set();
    }

    /*
     * 请求工作线程重建 COM 会话和注册表监听。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，唤醒工作线程处理 Explorer 通知。
     */
    public void RequestReconnect()
    {
        Interlocked.Exchange(ref reconnect, 1);
        wake.Set();
    }

    /*
     * 在处理热键的主线程激活工作线程选中的窗口。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，请求仍有效且所在桌面已确认时交给前台复核。
     */
    public void ActivatePendingWindow()
    {
        FocusRequest focus = Interlocked.Exchange(ref pendingFocus, null);
        if (focus == null || focus.Switch != latestRequest || _shouldCancel(focus.Switch.Target))
            return;

        int count;
        int current;
        bool currentKnown;
        if (VirtualDesktopRegistry.TryRead(out count, out current, out currentKnown) &&
            currentKnown && current == focus.Switch.Target)
            WindowFocus.Activate(focus.Window, focus.IsShell);
    }

    // 初始化 STA 和线程消息队列，异常后释放旧会话再重建。
    private void _threadMain()
    {
        Interlocked.Exchange(ref workerThreadId, unchecked((int)Native.GetCurrentThreadId()));
        int initHr = Native.CoInitializeEx(IntPtr.Zero, 0x2);
        bool comInitialized = initHr >= 0;

        try
        {
            if (!comInitialized && initHr != unchecked((int)0x80010106))
                Logger.Write("COM 初始化失败：" + Native.HResult(initHr));

            Native.MSG ignored;
            Native.PeekMessage(out ignored, IntPtr.Zero, 0, 0, 0);
            while (Thread.VolatileRead(ref stopping) == 0)
            {
                Interlocked.Exchange(ref reconnect, 0);
                try
                {
                    using (DesktopBackend backend = new DesktopBackend())
                    using (RegistryChangeWatch root = new RegistryChangeWatch(VirtualDesktopRegistry.RootPath))
                    using (RegistryChangeWatch session = new RegistryChangeWatch(VirtualDesktopRegistry.SessionPath))
                        _runWorkerSession(backend, root, session);
                }
                catch (Exception ex)
                {
                    _publishCount(-1);
                    ready.Set();
                    Logger.Write("工作会话异常，准备重建：" + ex);
                    try { _waitForEvents(500, wakeHandles); }
                    catch (Exception waitError) { Logger.Write("工作线程等待失败：" + waitError.Message); Thread.Sleep(500); }
                }
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

    // 先挂接通知再读取初始状态，空闲时等待事件和 STA 消息。
    private void _runWorkerSession(DesktopBackend backend, RegistryChangeWatch root, RegistryChangeWatch session)
    {
        registryHandles[1] = root.EventHandle;
        registryHandles[2] = session.EventHandle;
        root.Arm();
        session.Arm();

        byte[] previousIds = null;
        _refreshCount(backend, ref previousIds, true);
        ready.Set();

        int signal = 0;
        while (Thread.VolatileRead(ref stopping) == 0 && Thread.VolatileRead(ref reconnect) == 0)
        {
            // 优先吸收已到达的拓扑通知，避免新热键继续使用旧桌面顺序。
            bool rootChanged = signal == 1 || root.Changed.WaitOne(0);
            bool sessionChanged = signal == 2 || session.Changed.WaitOne(0);
            if (rootChanged || (signal < 0 && !root.IsArmed))
                root.Arm();
            if (sessionChanged || (signal < 0 && !session.IsArmed))
                session.Arm();
            if (rootChanged || sessionChanged || signal < 0)
                _refreshCount(backend, ref previousIds, DesktopCount <= 0 && !backend.FallbackOnly);

            SwitchRequest request = Interlocked.Exchange(ref pendingRequest, null);
            if (request != null)
            {
                _switchVerified(backend, request);
                signal = 0;
                continue;
            }

            bool missingCount = DesktopCount <= 0 && !backend.FallbackOnly;
            bool missingWatch = !root.IsArmed || !session.IsArmed;
            signal = _waitForEvents(missingCount || missingWatch ? 2000 : -1, registryHandles);
        }
    }
    // 等待句柄并分发 STA 消息，-1 表示超时，非负值为已触发的句柄位置。
    private int _waitForEvents(int timeoutMilliseconds, IntPtr[] handles)
    {
        const uint WAIT_TIMEOUT = 258;
        const uint WAIT_FAILED = 0xFFFFFFFF;
        const uint QS_ALLINPUT = 0x04FF;
        const uint MWMO_INPUTAVAILABLE = 0x0004;

        Stopwatch watch = timeoutMilliseconds < 0 ? null : Stopwatch.StartNew();
        while (Thread.VolatileRead(ref stopping) == 0)
        {
            uint remaining = watch == null ? UInt32.MaxValue :
                (uint)Math.Max(timeoutMilliseconds - watch.ElapsedMilliseconds, 0);
            uint result = Native.MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result < handles.Length)
                return (int)result;
            if (result == handles.Length)
            {
                _pumpPendingMessages();
                if (watch != null && watch.ElapsedMilliseconds >= timeoutMilliseconds)
                    return -1;
                continue;
            }
            if (result == WAIT_TIMEOUT)
                return -1;
            if (result == WAIT_FAILED)
                throw new InvalidOperationException("MsgWaitForMultipleObjectsEx 失败，Win32=" + Marshal.GetLastWin32Error());
            throw new InvalidOperationException("MsgWaitForMultipleObjectsEx 返回异常值=" + result);
        }
        return -1;
    }

    // 分发 STA 消息，让 COM 在等待热键和注册表通知期间仍能处理回调。
    private void _pumpPendingMessages()
    {
        Native.MSG message;
        while (Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 0x0001))
        {
            Interlocked.Increment(ref pumpedMessageCount);
            if (message.message == 0x0012) // WM_QUIT 无需分发。
                continue;
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    // 只在桌面列表变化或计数尚未取得时查询 COM，普通桌面切换只更新注册表快照。
    private void _refreshCount(DesktopBackend backend, ref byte[] previousIds, bool forceProbe)
    {
        int registryCount;
        int current;
        bool currentKnown;
        byte[] ids;
        bool registryAvailable = VirtualDesktopRegistry.TryRead(out registryCount, out current, out currentKnown, out ids);
        bool topologyChanged = NeedsTopologyRefresh(previousIds, ids);
        previousIds = ids;
        if (!topologyChanged && !forceProbe)
            return;

        if (topologyChanged && !forceProbe)
        {
            backend.Reset();
            Logger.Write("检测到桌面拓扑变化，重建 COM 管理器；注册表桌面数=" + registryCount);
        }

        int comCount;
        int hr;
        bool comAvailable = backend.TryGetCount(out comCount, out hr);
        _publishCount(ResolveDesktopCount(comAvailable, comCount, registryAvailable, registryCount));
    }

    // 桌面数发生变化就通知主线程，热键注册始终留在主线程。
    private void _publishCount(int count)
    {
        if (Interlocked.Exchange(ref desktopCount, count) != count && messageWindow != IntPtr.Zero)
            Native.PostMessage(messageWindow, Program.WM_DESKTOP_COUNT, UIntPtr.Zero, IntPtr.Zero);
    }

    /*
     * 判断桌面列表是否新增、删除、重排或变为不可读。
     *
     * Args:
     *     previousIds - 上次读取的桌面 ID 字节序列，缺失时为 null。
     *     currentIds - 当前按桌面顺序排列的 ID 字节序列，缺失时为 null。
     * Returns:
     *     列表内容变化时为 true，只切换当前桌面时为 false。
     */
    internal static bool NeedsTopologyRefresh(byte[] previousIds, byte[] currentIds)
    {
        if (previousIds == null || currentIds == null)
            return previousIds != currentIds;
        if (previousIds.Length != currentIds.Length)
            return true;

        for (int i = 0; i < previousIds.Length; i++)
        {
            if (previousIds[i] != currentIds[i])
                return true;
        }
        return false;
    }

    internal static int ResolveDesktopCount(bool comAvailable, int comCount, bool registryAvailable, int registryCount)
    {
        if (registryAvailable && registryCount > 0)
            return registryCount;
        return comAvailable && comCount > 0 ? comCount : -1;
    }

    // 有不同的新目标、收到 Explorer 通知或准备退出时，停止处理当前切换。
    private bool _shouldCancel(int target)
    {
        SwitchRequest pending = Interlocked.CompareExchange(ref pendingRequest, null, null);
        return Thread.VolatileRead(ref stopping) != 0 || Thread.VolatileRead(ref reconnect) != 0 ||
            (pending != null && pending.Target != target);
    }

    // 依次尝试 COM、重连和系统快捷键，只在位置确认后安排焦点恢复。
    private void _switchVerified(DesktopBackend backend, SwitchRequest request)
    {
        int target = request.Target;
        if (_shouldCancel(target))
            return;

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
        {
            _queueFocus(request);
            return;
        }

        int hr = 0;
        bool confirmed;
        if (!backend.FallbackOnly)
        {
            bool issued = backend.TrySwitch(target, out hr);
            if (issued && _waitForTarget(target, 900, out confirmed))
            {
                if (confirmed) _queueFocus(request);
                return;
            }

            if (_shouldCancel(target))
                return;
            if (!backend.FallbackOnly)
            {
                Logger.Write("COM 切换首次失败；目标=" + (target + 1) + "，HRESULT=" + Native.HResult(hr) +
                    (issued ? "，结果未验证" : String.Empty));
                backend.Reset();
                if (_shouldCancel(target))
                    return;
                issued = backend.TrySwitch(target, out hr);
                if (issued && _waitForTarget(target, 900, out confirmed))
                {
                    if (confirmed) _queueFocus(request);
                    return;
                }
            }
        }

        if (_shouldCancel(target))
            return;

        int refreshedCount;
        VirtualDesktopRegistry.TryRead(out refreshedCount, out current, out currentKnown);
        if (refreshedCount > 0)
            count = refreshedCount;

        // 接口熔断时直接回退，避免每次按键重复记录相同的 GUID 错误。
        if (!backend.FallbackOnly)
            Logger.Write("COM 重试失败；目标=" + (target + 1) + "，HRESULT=" + Native.HResult(hr) + "；启用系统快捷键回退");
        if (NativeDesktopFallback.TrySwitch(currentKnown ? current : -1, target, count, _pumpForWorker,
            delegate { return _shouldCancel(target); }) && _waitForTarget(target, 1500, out confirmed))
        {
            if (confirmed) _queueFocus(request);
        }
        else if (!_shouldCancel(target))
        {
            Logger.Write("切换最终失败；目标=" + (target + 1));
            Native.MessageBeep(0x10);
        }
    }

    // 观察目标索引；只在注册表读到目标时允许恢复焦点。
    private bool _waitForTarget(int target, int timeoutMilliseconds, out bool confirmed)
    {
        Stopwatch watch = Stopwatch.StartNew();
        bool currentObserved = false;
        confirmed = false;
        while (watch.ElapsedMilliseconds < timeoutMilliseconds && !_shouldCancel(target))
        {
            int count;
            int current;
            bool currentKnown;
            if (VirtualDesktopRegistry.TryRead(out count, out current, out currentKnown) && currentKnown)
            {
                currentObserved = true;
                if (current == target)
                {
                    confirmed = true;
                    return true;
                }
            }
            _pumpForWorker(30);
        }
        // 缺少当前位置时，可沿用切换调用的成功结果，但不能据此激活窗口。
        return !_shouldCancel(target) && !currentObserved;
    }

    // 在工作线程筛选窗口，最终激活交回收到热键的主线程。
    private void _queueFocus(SwitchRequest request)
    {
        if (messageWindow == IntPtr.Zero || request != latestRequest || _shouldCancel(request.Target))
            return;

        bool isShell;
        IntPtr window = WindowFocus.SelectTarget(_pumpForWorker, delegate { return _shouldCancel(request.Target); },
            out isShell);
        if (window == IntPtr.Zero || request != latestRequest || _shouldCancel(request.Target))
            return;

        Interlocked.Exchange(ref pendingFocus, new FocusRequest
        {
            Switch = request,
            Window = window,
            IsShell = isShell
        });
        Native.PostMessage(messageWindow, Program.WM_FOCUS_DESKTOP, UIntPtr.Zero, IntPtr.Zero);
    }

    // 切换验证期间保留注册表事件给会话循环，仍可被新热键或退出信号唤醒。
    private void _pumpForWorker(int milliseconds)
    {
        _waitForEvents(milliseconds, wakeHandles);
    }

    /*
     * 通知工作线程退出并释放线程同步对象。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，若原生调用超时则保留句柄供尚未退出的线程使用。
     */
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0)
            return;

        wake.Set();
        if (started && !thread.Join(2500))
        {
            Logger.Write("工作线程未能在 2.5 秒内退出；保留同步句柄直到进程结束");
            return;
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
    private static readonly Guid IID_ManagerWin11Transition = new Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E");
    private static readonly Guid IID_DesktopWin10 = new Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4");
    private static readonly Guid IID_DesktopWin11Early = new Guid("536D3495-B208-4CC9-AE26-DE8111275BF8");
    private static readonly Guid IID_DesktopWin11 = new Guid("3F07F4BE-B107-441A-AF0F-39D82529072C");

    private IntPtr manager;
    private ManagerLayout layout;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    public bool FallbackOnly { get; private set; }
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

    /*
     * 枚举私有 COM 后端报告的桌面数量。
     *
     * Args:
     *     count - 返回桌面数，枚举失败时为 -1。
     *     hr - 返回初始化或枚举操作的 HRESULT。
     * Returns:
     *     成功取得桌面数时为 true。
     */
    public bool TryGetCount(out int count, out int hr)
    {
        count = -1;
        if (!_ensureInitialized(out hr))
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

    /*
     * 使用当前 Windows 版本的私有 COM 接口切换桌面。
     *
     * Args:
     *     target - 从 0 开始的目标桌面索引。
     *     hr - 初始化或切换失败时返回相应的 HRESULT。
     * Returns:
     *     COM 接受切换请求时为 true，是否到达目标由调用者验证。
     */
    public bool TrySwitch(int target, out int hr)
    {
        hr = unchecked((int)0x80004005);
        if (!_ensureInitialized(out hr))
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

    // 接口不受支持时跳过探测；未就绪等临时失败仍可重试。
    private bool _ensureInitialized(out int hr)
    {
        if (manager != IntPtr.Zero)
        {
            hr = 0;
            return true;
        }
        if (FallbackOnly)
        {
            hr = E_NOINTERFACE;
            return false;
        }


        return _initialize(out hr);
    }

    // 区分 Explorer 暂时未就绪、候选接口验证失败和明确不支持接口。
    private bool _initialize(out int hr)
    {
        IntPtr shell = IntPtr.Zero;
        IntPtr provider = IntPtr.Zero;
        Guid shellClsid = CLSID_ImmersiveShell;
        Guid unknownIid = IID_IUnknown;
        hr = Native.CoCreateInstance(ref shellClsid, IntPtr.Zero, 4, ref unknownIid, out shell);
        bool allNoInterface = true;
        try
        {
            if (hr < 0 || shell == IntPtr.Zero)
                return false;

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
                    IntPtr candidate;
                    Guid service = serviceIds[s];
                    Guid iid = candidates[c].ManagerIid;
                    hr = queryService(provider, ref service, ref iid, out candidate);
                    allNoInterface &= hr == E_NOINTERFACE;
                    if (hr < 0 || candidate == IntPtr.Zero)
                    {
                        if (candidate != IntPtr.Zero) Marshal.Release(candidate);
                        continue;
                    }

                    manager = candidate;
                    layout = candidates[c];
                    int validatedCount;
                    int validationHr;
                    if (ValidateCurrent(out validatedCount, out validationHr))
                    {
                        backendName = layout.Name;

                        Logger.Write("虚拟桌面后端=" + backendName + "；桌面数=" + validatedCount);
                        hr = 0;
                        return true;
                    }

                    Reset();
                    hr = validationHr;
                }
            }
        }
        finally
        {
            if (provider != IntPtr.Zero) Marshal.Release(provider);
            if (shell != IntPtr.Zero) Marshal.Release(shell);
        }

        if (allNoInterface)
        {
            FallbackOnly = true;
            Logger.Write("没有匹配当前 Windows build " + Native.GetWindowsBuild() + " 的私有桌面接口；仅使用系统快捷键回退");
        }
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

    /*
     * 按当前 Windows build 列出可探测的私有 COM 接口。
     *
     * Args:
     *     无。
     * Returns:
     *     按匹配优先级排列的候选接口和方法槽位。
     */
    private static ManagerLayout[] GetCandidates()
    {
        int build = Native.GetWindowsBuild();
        List<ManagerLayout> result = new List<ManagerLayout>();
        if (build >= 22000)
        {
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11, DesktopIid = IID_DesktopWin11, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win11-current" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11Transition, DesktopIid = IID_DesktopWin11,
                HasMonitorParameter = false, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win11-transition" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11Early, DesktopIid = IID_DesktopWin11Early,
                GetDesktopsSlot = build >= 22489 ? 8 : 7, SwitchDesktopSlot = build >= 22489 ? 10 : 9,
                HasMonitorParameter = true, Name = build >= 22489 ? "Win11-early-22H2" : "Win11-22000" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin10, DesktopIid = IID_DesktopWin10, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win10-compatible" });
        }
        else
        {
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin10, DesktopIid = IID_DesktopWin10, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win10" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11Transition, DesktopIid = IID_DesktopWin11,
                HasMonitorParameter = false, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, Name = "Win11-transition" });
            result.Add(new ManagerLayout { ManagerIid = IID_ManagerWin11Early, DesktopIid = IID_DesktopWin11Early, GetDesktopsSlot = 7, SwitchDesktopSlot = 9, HasMonitorParameter = true, Name = "Win10-21H2-monitor" });
        }
        return result.ToArray();
    }

    private void ResetOnFailure(int hr)
    {
        if (hr < 0)
            Reset();
    }

    /*
     * 释放当前的私有 COM 管理器。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，清除管理器和槽位布局，保留当前会话的接口熔断判定。
     */
    public void Reset()
    {
        IntPtr previous = manager;
        manager = IntPtr.Zero;
        layout = null;
        backendName = "无";
        if (previous != IntPtr.Zero)
            Marshal.Release(previous);
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
    internal const string RootPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    internal static readonly string SessionPath = RootPath.Replace(@"\VirtualDesktops",
        @"\SessionInfo\" + Process.GetCurrentProcess().SessionId + @"\VirtualDesktops");    private static readonly int build = Native.GetWindowsBuild();

    /*
     * 读取当前桌面数和桌面索引。
     *
     * Args:
     *     count - 返回桌面数。
     *     currentIndex - 返回从 0 开始的当前索引，未知时为 -1。
     *     currentKnown - 返回当前位置是否可以确认。
     * Returns:
     *     读取到有效桌面列表时为 true。
     */
    public static bool TryRead(out int count, out int currentIndex, out bool currentKnown)
    {
        byte[] ids;
        return TryRead(out count, out currentIndex, out currentKnown, out ids);
    }

    /*
     * 读取当前会话可用的桌面拓扑和当前位置。
     *
     * Args:
     *     count - 返回有效列表中的桌面数，未知时为 -1。
     *     currentIndex - 返回从 0 开始的当前索引，未知时为 -1。
     *     currentKnown - 返回当前位置是否可以确认。
     *     ids - 返回按桌面顺序排列的 ID 字节序列，缺失时为 null。
     * Returns:
     *     读取到有效桌面列表时为 true。
     */
    internal static bool TryRead(out int count, out int currentIndex, out bool currentKnown, out byte[] ids)
    {
        count = -1;
        currentIndex = -1;
        currentKnown = false;
        ids = null;
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

            using (RegistryKey session = Registry.CurrentUser.OpenSubKey(SessionPath))
            {
                if (session != null)
                {
                    sessionIds = session.GetValue("VirtualDesktopIDs") as byte[];
                    sessionCurrent = session.GetValue("CurrentVirtualDesktop") as byte[];
                }
            }

            currentIndex = SelectCurrentIndex(rootIds, sessionIds, rootCurrent, sessionCurrent, build, out ids);
            if (ids == null)
                return false;

            count = ids.Length / 16;
            currentKnown = currentIndex >= 0;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Write("读取桌面拓扑失败：" + ex.Message);
            return false;
        }
    }

    /*
     * 从注册表快照选出桌面列表并确定当前位置。
     *
     * 内容：
     *     会话独立列表只匹配同会话的当前位置。
     *     根列表在 Win11 优先匹配根位置，在 Win10 优先匹配会话位置。
     *
     * Args:
     *     rootIds - 根键桌面列表。
     *     sessionIds - 当前会话的桌面列表。
     *     rootCurrent - 根键当前位置。
     *     sessionCurrent - 当前会话的桌面位置。
     *     windowsBuild - Windows build，22000 起按 Win11 优先级处理。
     *     ids - 返回选中的有效桌面列表，两个列表均无效时为 null。
     * Returns:
     *     当前桌面的索引，无法匹配时为 -1。
     */
    internal static int SelectCurrentIndex(byte[] rootIds, byte[] sessionIds, byte[] rootCurrent,
        byte[] sessionCurrent, int windowsBuild, out byte[] ids)
    {
        if (_isValidIdList(sessionIds))
        {
            ids = sessionIds;
            return _findCurrentIndex(ids, sessionCurrent);
        }

        ids = _isValidIdList(rootIds) ? rootIds : null;
        if (ids == null)
            return -1;

        bool preferRoot = windowsBuild >= 22000;
        int index = _findCurrentIndex(ids, preferRoot ? rootCurrent : sessionCurrent);
        return index >= 0 ? index : _findCurrentIndex(ids, preferRoot ? sessionCurrent : rootCurrent);
    }

    // 桌面列表由一个或多个连续的 16 字节 GUID 组成。
    private static bool _isValidIdList(byte[] ids)
    {
        return ids != null && ids.Length >= 16 && ids.Length % 16 == 0;
    }

    // 查找桌面 ID，无有效值或匹配时返回 -1。
    private static int _findCurrentIndex(byte[] ids, byte[] current)
    {
        if (current == null || current.Length != 16)
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

internal sealed class RegistryChangeWatch : IDisposable
{
    private const uint REG_NOTIFY_CHANGE_NAME = 1;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 4;
    private readonly string path;
    internal readonly AutoResetEvent Changed = new AutoResetEvent(false);
    private RegistryKey key;

    /*
     * 创建一个可以在注册表键重建后重新挂接的监听器。
     *
     * Args:
     *     path - HKEY_CURRENT_USER 下要监听的相对路径。
     * Returns:
     *     尚未挂接通知的监听器对象。
     */
    public RegistryChangeWatch(string path)
    {
        this.path = path;
    }

    public IntPtr EventHandle { get { return Changed.SafeWaitHandle.DangerousGetHandle(); } }
    public bool IsArmed { get { return key != null; } }

    /*
     * 重新挂接一次注册表通知。
     *
     * 内容：
     *     目标键存在时监听值变化。
     *     目标键不存在时监听最近的父键，等子键创建后再切回目标键。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，IsArmed 表示是否成功挂接。
     */
    public void Arm()
    {
        _closeKey();
        Changed.Reset();
        string watchPath = path;
        bool missingTarget = false;
        while (watchPath.Length > 0)
        {
            RegistryKey next = Registry.CurrentUser.OpenSubKey(watchPath, RegistryKeyPermissionCheck.ReadSubTree,
                System.Security.AccessControl.RegistryRights.Notify);
            if (next != null)
            {
                bool isParent = watchPath != path;
                bool registered = false;
                try
                {
                    uint filter = isParent ? REG_NOTIFY_CHANGE_NAME : REG_NOTIFY_CHANGE_LAST_SET;
                    int result = Native.RegNotifyChangeKeyValue(next.Handle, isParent, filter, EventHandle, true);
                    if (result == 0)
                    {
                        key = next;
                        registered = true;
                    }
                }
                finally
                {
                    if (!registered) next.Dispose();
                }

                if (registered)
                {
                    // 父键挂接完成后再查一次，避免漏掉打开父键与注册通知之间创建的目标键。
                    if (isParent && missingTarget)
                    {
                        using (RegistryKey created = Registry.CurrentUser.OpenSubKey(path,
                            RegistryKeyPermissionCheck.ReadSubTree, System.Security.AccessControl.RegistryRights.Notify))
                        {
                            if (created != null)
                                Changed.Set();
                        }
                    }
                    return;
                }
            }

            if (watchPath == path)
                missingTarget = next == null;
            int delimiter = watchPath.LastIndexOf('\\');
            if (delimiter < 0)
                return;
            watchPath = watchPath.Substring(0, delimiter);
        }
    }

    // 关闭旧键，取消它尚未触发的通知。
    private void _closeKey()
    {
        if (key != null)
        {
            key.Dispose();
            key = null;
        }
    }

    /*
     * 释放注册表键和通知事件。
     *
     * Args:
     *     无。
     * Returns:
     *     无返回值，关闭键时一并撤销通知。
     */
    public void Dispose()
    {
        _closeKey();
        Changed.Dispose();
    }
}
internal static class WindowFocus
{
    private const uint DWMWA_CLOAKED = 14;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private static readonly Guid managerClass = new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        /*
         * 查询指定窗口是否位于当前虚拟桌面。
         *
         * Args:
         *     window - 要检查的顶层窗口句柄。
         *     current - 返回窗口是否属于当前虚拟桌面。
         * Returns:
         *     COM 调用的 HRESULT。
         */
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool current);
    }

    /*
     * 检查当前位置的前台窗口，必要时选择一个待激活的顶层窗口。
     *
     * 内容：
     *     前台已在目标桌面时保留原有焦点。
     *     按 Z-Order 选择可交互窗口，给 DWM 一小段时间完成过渡。
     *     仅当没有普通窗口且原前台被 Cloak 时尝试任务栏或桌面壳窗口。
     *
     * Args:
     *     wait - 短暂等待并分发 STA 消息。
     *     canceled - 切换已被取消时返回 true。
     *     isShell - 返回选中的窗口是否是空桌面兜底的系统壳窗口。
     * Returns:
     *     待激活的窗口句柄，无需激活或没有候选时为零。
     */
    public static IntPtr SelectTarget(Action<int> wait, Func<bool> canceled, out bool isShell)
    {
        IntPtr currentForeground = IntPtr.Zero;
        isShell = false;
        IVirtualDesktopManager manager = _openManager();
        try
        {
            for (int attempt = 0; attempt < 3 && !canceled(); attempt++)
            {
                currentForeground = Native.GetForegroundWindow();
                if (_isDesktopWindow(currentForeground, manager, false))
                    return IntPtr.Zero;

                IntPtr selected = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr window, IntPtr data)
                {
                    if (canceled())
                        return false;
                    if (!_isDesktopWindow(window, manager, true))
                        return true;
                    selected = window;
                    return false;
                }, IntPtr.Zero);
                if (selected != IntPtr.Zero)
                    return selected;
                if (attempt < 2)
                    wait(40);
            }

            int cloaked;
            if (canceled() || currentForeground == IntPtr.Zero ||
                !_tryReadCloak(currentForeground, out cloaked) || cloaked == 0)
                return IntPtr.Zero;

            isShell = true;
            IntPtr taskbar = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
            return taskbar != IntPtr.Zero ? taskbar : Native.GetShellWindow();
        }
        finally
        {
            if (manager != null) Marshal.ReleaseComObject(manager);
        }
    }

    /*
     * 在热键窗口所在的主线程复核现场并尝试设置前台。
     *
     * 步骤：
     *     1. 保留当前桌面的有效前台，复核普通候选或空桌面的壳兜底条件。
     *     2. 直接请求激活。若失败，探测前台线程并短暂连接输入队列重试。
     *
     * Args:
     *     window - 工作线程选中的窗口。
     *     isShell - 窗口是否为空桌面的系统壳兜底。
     * Returns:
     *     无返回值，复核通过时尝试激活目标窗口。
     */
    public static void Activate(IntPtr window, bool isShell)
    {
        IVirtualDesktopManager manager = _openManager();
        try
        {
            IntPtr currentForeground = Native.GetForegroundWindow();
            if (!_canActivate(window, isShell, currentForeground, manager))
                return;

            if (!Native.SetForegroundWindow(window))
                _retryActivation(window, isShell, manager);
        }
        finally
        {
            if (manager != null) Marshal.ReleaseComObject(manager);
        }
    }

    // 保留当前桌面的有效前台，只在旧前台仍被 Cloak 时允许壳窗口兜底。
    private static bool _canActivate(IntPtr window, bool isShell, IntPtr currentForeground, IVirtualDesktopManager manager)
    {
        if (_isDesktopWindow(currentForeground, manager, false))
            return false;
        if (!isShell)
            return _isDesktopWindow(window, manager, true);

        int foregroundCloak;
        int shellCloak;
        return currentForeground != IntPtr.Zero && _tryReadCloak(currentForeground, out foregroundCloak) &&
            foregroundCloak != 0 && Native.IsWindowVisible(window) &&
            _tryReadCloak(window, out shellCloak) && shellCloak == 0;
    }

    // 只在前台线程响应探测且复核仍通过时连接输入队列，重试结束后立即断开。
    private static void _retryActivation(IntPtr window, bool isShell, IVirtualDesktopManager manager)
    {
        IntPtr currentForeground = Native.GetForegroundWindow();
        if (currentForeground == IntPtr.Zero || currentForeground == window)
            return;

        uint processId;
        uint fgThread = Native.GetWindowThreadProcessId(currentForeground, out processId);
        uint curThread = Native.GetCurrentThreadId();
        if (fgThread == 0 || fgThread == curThread)
            return;

        UIntPtr result;
        if (Native.SendMessageTimeout(currentForeground, 0, UIntPtr.Zero, IntPtr.Zero, 0x0002, 50, out result) == IntPtr.Zero)
            return;

        // 只连接已探测过的线程，同时复核等待期间是否出现了可用前台。
        currentForeground = Native.GetForegroundWindow();
        if (currentForeground == IntPtr.Zero || currentForeground == window ||
            Native.GetWindowThreadProcessId(currentForeground, out processId) != fgThread ||
            !_canActivate(window, isShell, currentForeground, manager) ||
            !Native.AttachThreadInput(curThread, fgThread, true))
            return;

        try
        {
            Native.BringWindowToTop(window);
            Native.SetForegroundWindow(window);
        }
        finally { Native.AttachThreadInput(curThread, fgThread, false); }
    }

    /*
     * 检查穿透样式判定、窗口枚举和公开虚拟桌面接口。
     *
     * Args:
     *     无。
     * Returns:
     *     样式判定正确且能查询窗口属性与桌面归属时为 true，不改变焦点。
     */
    public static bool CheckInterfaces()
    {
        if (!_isClickThroughOverlay(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_APPWINDOW) ||
            _isClickThroughOverlay(WS_EX_LAYERED) || _isClickThroughOverlay(WS_EX_TRANSPARENT) ||
            _isClickThroughOverlay(0))
            return false;

        IVirtualDesktopManager manager = _openManager();
        if (manager == null)
            return false;

        try
        {
            bool ok = false;
            Native.EnumWindows(delegate(IntPtr window, IntPtr data)
            {
                int cloaked;
                Native.RECT rect;
                bool current;
                if (_tryReadCloak(window, out cloaked) && Native.GetWindowRect(window, out rect) &&
                    _tryQueryCurrent(manager, window, out current))
                    ok = true;
                return !ok;
            }, IntPtr.Zero);
            return ok;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    // 筛除壳窗口、Cloak 窗口和穿透浮层，候选窗口还需满足可交互条件。
    private static bool _isDesktopWindow(IntPtr window, IVirtualDesktopManager manager, bool candidate)
    {
        if (window == IntPtr.Zero || !Native.IsWindowVisible(window) || Native.IsIconic(window))
            return false;

        int cloaked;
        if (!_tryReadCloak(window, out cloaked) || cloaked != 0)
            return false;

        StringBuilder name = new StringBuilder(256);
        if (Native.GetClassName(window, name, name.Capacity) == 0)
            return false;
        switch (name.ToString())
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "Windows.UI.Core.CoreWindow":
                return false;
        }

        long style = Native.GetWindowLongPtr(window, -20).ToInt64(); // GWL_EXSTYLE
        if (_isClickThroughOverlay(style))
            return false;

        if (candidate)
        {
            Native.RECT rect;
            if (!Native.IsWindowEnabled(window) || !Native.GetWindowRect(window, out rect) ||
                rect.right <= rect.left || rect.bottom <= rect.top)
                return false;

            if ((style & WS_EX_NOACTIVATE) != 0 || ((style & WS_EX_TOOLWINDOW) != 0 && (style & WS_EX_APPWINDOW) == 0))
                return false;
        }

        bool current;
        return !_tryQueryCurrent(manager, window, out current) || current;
    }

    // 只有同时具备分层和点击穿透样式的窗口才按穿透浮层排除。
    private static bool _isClickThroughOverlay(long style)
    {
        const long mask = WS_EX_LAYERED | WS_EX_TRANSPARENT;
        return (style & mask) == mask;
    }

    // 读取 DWM 的 Cloak 位掩码，查询失败的窗口不能充当普通候选。
    private static bool _tryReadCloak(IntPtr window, out int cloaked)
    {
        return Native.DwmGetWindowAttribute(window, DWMWA_CLOAKED, out cloaked, sizeof(int)) >= 0;
    }

    // 尝试打开公开桌面管理器，失败时由已确认桌面与 Cloak 属性筛选窗口。
    private static IVirtualDesktopManager _openManager()
    {
        try { return (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(managerClass)); }
        catch (Exception) { return null; }
    }

    // 区分 COM 调用失败与调用成功但窗口不在当前桌面。
    private static bool _tryQueryCurrent(IVirtualDesktopManager manager, IntPtr window, out bool current)
    {
        current = false;
        try { return manager != null && manager.IsWindowOnCurrentVirtualDesktop(window, out current) >= 0; }
        catch (Exception) { return false; }
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

    /*
     * 用系统快捷键逐步移动到目标桌面，并核对每一步。
     *
     * Args:
     *     current - 已确认的当前桌面索引。
     *     target - 从 0 开始的目标索引。
     *     count - 当前桌面数量。
     *     wait - 等待指定毫秒数并分发工作线程的消息。
     *     canceled - 收到新目标、重连或退出时返回 true。
     * Returns:
     *     已按步骤到达目标时为 true，失败或取消时为 false。
     */
    public static bool TrySwitch(int current, int target, int count, Action<int> wait, Func<bool> canceled)
    {
        Stopwatch releaseWait = Stopwatch.StartNew();
        while (!canceled() && AnyModifierDown() && releaseWait.ElapsedMilliseconds < 2000)
            Pause(wait, 10);
        if (canceled())
            return false;
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
            if (canceled())
                return false;
            int expected = current + (direction == VK_LEFT ? -1 : 1);
            if (!SendOne(direction))
                return false;
            if (!_waitForRegistryIndex(expected, 1200, wait, canceled))
            {
                if (!canceled())
                    Logger.Write("回退中止：系统快捷键后未到达桌面 " + (expected + 1));
                return false;
            }
            current = expected;
            // 注册表更新可能早于动画结束；留出稳定时间，禁止动画堆积。
            Pause(wait, 250);
        }
        return !canceled() && current == target;
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

    // 等待当前步到达预期桌面，新请求到来时停止旧路径。
    private static bool _waitForRegistryIndex(int expectedIndex, int timeout, Action<int> wait, Func<bool> canceled)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!canceled() && watch.ElapsedMilliseconds < timeout)
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
    /*
     * 运行桌面切换器自检，写出报告和总退出码。
     *
     * 步骤：
     *     1. 检查真实桌面拓扑、Win10/Win11 注册表规则和变更通知。
     *     2. 检查私有 COM 枚举、焦点接口和独立 STA 消息泵。
     *     3. 检查仅消息窗口的热键绑定和 x64 输入结构，写出报告。
     *
     * Args:
     *     无。
     * Returns:
     *     所有检查通过时为 0，至少一项未通过时为 1。测试不会切换桌面或激活窗口。
     */
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
            ? "正常，桌面数=" + registryCount + "，当前桌面=" + (currentKnown ? "索引 " + current : "不可确认")
            : "不可用"));

        byte[] first = new Guid("11111111-1111-1111-1111-111111111111").ToByteArray();
        byte[] second = new Guid("22222222-2222-2222-2222-222222222222").ToByteArray();
        byte[] rootIds = new byte[32];
        Buffer.BlockCopy(first, 0, rootIds, 0, 16);
        Buffer.BlockCopy(second, 0, rootIds, 16, 16);
        byte[] selectedIds;
        bool sessionPriorityOk = VirtualDesktopRegistry.SelectCurrentIndex(rootIds, second, first, second, 22631, out selectedIds) == 0 &&
            selectedIds == second &&
            VirtualDesktopRegistry.SelectCurrentIndex(rootIds, rootIds, first, null, 22631, out selectedIds) == -1;
        report.AppendLine("RDP Session 隔离：" + (sessionPriorityOk ? "正常" : "失败"));

        bool windows11Ok = VirtualDesktopRegistry.SelectCurrentIndex(rootIds, null, second, first, 22631, out selectedIds) == 1 &&
            selectedIds == rootIds &&
            VirtualDesktopRegistry.SelectCurrentIndex(rootIds, new byte[1], new byte[16], first, 22000, out selectedIds) == 0;
        report.AppendLine("Win11 注册表优先级（含 Win10 升级残留）：" + (windows11Ok ? "正常" : "失败"));

        bool windows10Ok = VirtualDesktopRegistry.SelectCurrentIndex(rootIds, null, second, first, 19045, out selectedIds) == 0 &&
            VirtualDesktopRegistry.SelectCurrentIndex(rootIds, null, second, null, 19045, out selectedIds) == 1;
        report.AppendLine("Win10 注册表优先级：" + (windows10Ok ? "正常" : "失败"));

        byte[] reorderedIds = new byte[32];
        Buffer.BlockCopy(second, 0, reorderedIds, 0, 16);
        Buffer.BlockCopy(first, 0, reorderedIds, 16, 16);
        bool topologyRefreshOk = DesktopWorker.NeedsTopologyRefresh(first, rootIds) &&
            DesktopWorker.NeedsTopologyRefresh(rootIds, first) &&
            DesktopWorker.NeedsTopologyRefresh(rootIds, reorderedIds) &&
            !DesktopWorker.NeedsTopologyRefresh(rootIds, (byte[])rootIds.Clone()) &&
            DesktopWorker.ResolveDesktopCount(true, 4, true, 5) == 5 &&
            DesktopWorker.ResolveDesktopCount(true, 5, false, -1) == 5;
        bool registryWatchOk = _testRegistryWatch();
        report.AppendLine("动态桌面拓扑刷新：" + (topologyRefreshOk && registryWatchOk
            ? "正常（列表变化、值变更通知及键重建）"
            : "失败，拓扑判断=" + topologyRefreshOk + "，注册表通知=" + registryWatchOk));

        int initHr = Native.CoInitializeEx(IntPtr.Zero, 0x2);
        bool backendOk;
        bool focusOk;
        try
        {
            int count;
            int hr;
            using (DesktopBackend backend = new DesktopBackend())
            {
                backendOk = backend.TryGetCount(out count, out hr);
                report.AppendLine("私有 COM 枚举：" + (backendOk ? "正常，桌面数=" + count : "不可用，HRESULT=" + Native.HResult(hr)));
            }
            focusOk = WindowFocus.CheckInterfaces();
        }
        finally { if (initHr >= 0) Native.CoUninitialize(); }
        report.AppendLine("焦点辅助接口：" + (focusOk ? "正常（穿透样式、DWM、顶层窗口和公开虚拟桌面接口）" : "失败"));

        bool workerPumpOk;
        using (DesktopWorker worker = new DesktopWorker(IntPtr.Zero))
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

        bool hotKeyOk = _testWindowHotKey();
        report.AppendLine("仅消息窗口热键绑定：" + (hotKeyOk ? "正常（注册、消息分发及注销）" : "失败"));
        int inputSize = Marshal.SizeOf(typeof(Native.INPUT));
        report.AppendLine("SendInput x64 结构：" + (inputSize == 40 ? "正常（40 字节）" : "异常（" + inputSize + " 字节）"));

        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LiteDesktopSwitcher.selftest.txt");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        Logger.Write(report.ToString().Replace(Environment.NewLine, " | "));
        return registryOk && sessionPriorityOk && windows11Ok && windows10Ok && topologyRefreshOk &&
            registryWatchOk && backendOk && focusOk && workerPumpOk && hotKeyOk && inputSize == 40 ? 0 : 1;
    }

    // 用独立临时键验证父键监听、目标值监听，以及删除后重新挂接。
    private static bool _testRegistryWatch()
    {
        string parentPath = @"Software\LiteDesktopSwitcher.SelfTest-" + Guid.NewGuid().ToString("N");
        const string childName = "VirtualDesktops";
        try
        {
            using (RegistryKey parent = Registry.CurrentUser.CreateSubKey(parentPath))
            {
                try
                {
                    using (RegistryChangeWatch watch = new RegistryChangeWatch(parentPath + @"\" + childName))
                    {
                        watch.Arm();
                        using (RegistryKey child = parent.CreateSubKey(childName))
                        {
                            if (!watch.Changed.WaitOne(1500)) return false;
                            watch.Arm();
                            child.SetValue("Test", 1);
                            if (!watch.Changed.WaitOne(1500)) return false;
                        }

                        watch.Arm();
                        parent.DeleteSubKey(childName);
                        if (!watch.Changed.WaitOne(1500)) return false;
                        watch.Arm();

                        using (RegistryKey child = parent.CreateSubKey(childName))
                        {
                            if (!watch.Changed.WaitOne(1500)) return false;
                            watch.Arm();
                            child.SetValue("Test", 2);
                            return watch.Changed.WaitOne(1500);
                        }
                    }
                }
                finally { parent.DeleteSubKey(childName, false); }
            }
        }
        catch (Exception ex)
        {
            Logger.Write("自检注册表通知失败：" + ex.Message);
            return false;
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKey(parentPath, false); }
            catch (Exception ex) { Logger.Write("清理自检临时注册表键失败：" + ex.Message); }
        }
    }

    // 仅注册 Alt+F24，并投递测试消息检查实际 HWND 的窗口过程。
    private static bool _testWindowHotKey()
    {
        try
        {
            using (MessageWindow window = new MessageWindow(true))
            {
                const int testId = 0x4C7F;
                UIntPtr testParameter = new UIntPtr(testId);
                bool handled = false;
                window.OnMessage = delegate(uint message, UIntPtr wParam)
                {
                    if (message != Program.WM_HOTKEY || wParam != testParameter)
                        return false;
                    handled = true;
                    return true;
                };

                if (!Native.RegisterHotKey(window.Handle, testId, 0x4001, 0x87))
                {
                    Logger.Write("自检热键注册失败，Win32=" + Marshal.GetLastWin32Error());
                    return false;
                }

                bool removed = false;
                try
                {
                    Native.MSG message;
                    if (Native.PostMessage(window.Handle, Program.WM_HOTKEY, testParameter, IntPtr.Zero) &&
                        Native.PeekMessage(out message, window.Handle, Program.WM_HOTKEY, Program.WM_HOTKEY, 1))
                        Native.DispatchMessage(ref message);
                }
                finally { removed = Native.UnregisterHotKey(window.Handle, testId); }
                return handled && removed;
            }
        }
        catch (Exception ex)
        {
            Logger.Write("自检消息窗口失败：" + ex.Message);
            return false;
        }
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowProc(IntPtr window, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASS
    {
        public uint style;
        public WindowProc proc;
        public int classExtra;
        public int windowExtra;
        public IntPtr instance;
        public IntPtr icon;
        public IntPtr cursor;
        public IntPtr background;
        public string menuName;
        public string className;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /*
     * 获取当前进程中的模块句柄。
     *
     * Args:
     *     moduleName - 模块名，null 表示主程序。
     * Returns:
     *     模块句柄，失败时为零。
     */
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string moduleName);

    /*
     * 注册窗口类。
     *
     * Args:
     *     windowClass - 窗口过程、模块和类名。
     * Returns:
     *     窗口类原子值，失败时为零。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClass(ref WNDCLASS windowClass);

    /*
     * 创建窗口。
     *
     * Args:
     *     exStyle - 扩展样式。
     *     className - 已注册的窗口类名。
     *     name - 窗口标题，可为空。
     *     style - 窗口样式。
     *     x - 水平坐标。
     *     y - 垂直坐标。
     *     width - 窗口宽度。
     *     height - 窗口高度。
     *     parent - 父窗口或 HWND_MESSAGE。
     *     menu - 菜单句柄或子窗口 ID。
     *     instance - 注册窗口类的模块句柄。
     *     parameter - 传给创建消息的附加值。
     * Returns:
     *     窗口句柄，失败时为零。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string name,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    /*
     * 按 Win32 默认规则处理窗口消息。
     *
     * Args:
     *     window - 接收消息的窗口句柄。
     *     message - 消息编号。
     *     wParam - 消息的第一参数。
     *     lParam - 消息的第二参数。
     * Returns:
     *     由消息类型决定的默认处理结果。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    /*
     * 销毁当前线程创建的窗口。
     *
     * Args:
     *     window - 要销毁的窗口句柄。
     * Returns:
     *     销毁成功时为 true。
     */
    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(IntPtr window);

    /*
     * 按类名和标题查找窗口。
     *
     * Args:
     *     parent - 父窗口句柄，零表示顶层窗口，HWND_MESSAGE 表示仅消息窗口。
     *     after - 从该窗口之后开始查找，零表示从头开始。
     *     className - 要查找的窗口类名。
     *     name - 要匹配的窗口标题，null 表示任意标题。
     * Returns:
     *     匹配的窗口句柄，找不到时为零。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string name);

    /*
     * 取得系统范围内的注册窗口消息编号。
     *
     * Args:
     *     name - 消息名称。
     * Returns:
     *     窗口消息编号，失败时为零。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string name);

    /*
     * 异步向指定窗口投递消息。
     *
     * Args:
     *     window - 目标窗口句柄。
     *     message - 消息编号。
     *     wParam - 消息的第一参数。
     *     lParam - 消息的第二参数。
     * Returns:
     *     成功放入消息队列时为 true。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    /*
     * 通知当前线程退出消息循环。
     *
     * Args:
     *     exitCode - 随 WM_QUIT 传递的退出码。
     * Returns:
     *     无返回值，向当前线程投递 WM_QUIT。
     */
    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    /*
     * 获取创建窗口的线程与进程。
     *
     * Args:
     *     window - 要查询的窗口句柄。
     *     processId - 返回窗口所属进程的 ID。
     * Returns:
     *     线程 ID，失败时为零。
     */
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /*
     * 获取当前接收用户输入的前台窗口。
     *
     * Args:
     *     无。
     * Returns:
     *     前台窗口句柄，无前台窗口时为零。
     */
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /*
     * 获取桌面 Shell 窗口。
     *
     * Args:
     *     无。
     * Returns:
     *     桌面壳窗口句柄，找不到时为零。
     */
    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    /*
     * 检查窗口是否具有可见状态。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     * Returns:
     *     窗口及其父窗口可见时为 true。
     */
    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr window);

    /*
     * 检查窗口是否最小化。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     * Returns:
     *     窗口已最小化时为 true。
     */
    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr window);

    /*
     * 检查窗口是否启用输入。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     * Returns:
     *     可接收键盘或鼠标输入时为 true。
     */
    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(IntPtr window);

    /*
     * 获取窗口的屏幕坐标范围。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     *     rect - 返回窗口的四条边界。
     * Returns:
     *     成功读取边界时为 true。
     */
    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

    /*
     * 读取窗口类名。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     *     name - 接收类名的字符串缓冲区。
     *     capacity - 缓冲区字符容量。
     * Returns:
     *     写入的字符数，失败时为零。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    /*
     * 读取窗口的原生属性。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     *     index - 要读取的属性索引。
     * Returns:
     *     该索引对应的属性值。
     */
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    /*
     * 枚举顶层窗口。
     *
     * Args:
     *     callback - 处理每个窗口，返回 false 时结束枚举。
     *     data - 传给回调的调用者数据。
     * Returns:
     *     完整枚举时为 true，提前结束或失败时为 false。
     */
    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr data);

    /*
     * 请求 Windows 将窗口置为前台。
     *
     * Args:
     *     window - 待激活的目标窗口句柄。
     * Returns:
     *     Windows 接受前台切换时为 true。
     */
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

    /*
     * 在有限时间内向窗口发送消息以探测其响应。
     *
     * Args:
     *     window - 目标窗口句柄。
     *     message - 消息编号。
     *     wParam - 消息的第一参数。
     *     lParam - 消息的第二参数。
     *     flags - 发送选项，包括遇到挂起线程时停止等待。
     *     timeout - 等待超时，单位为毫秒。
     *     result - 返回目标窗口对消息的处理结果。
     * Returns:
     *     发送成功时为非零，超时或失败时为零。
     */
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);

    /*
     * 连接或断开两个线程的输入队列。
     *
     * Args:
     *     thread - 当前线程的 ID。
     *     targetThread - 另一线程的 ID。
     *     attach - 为 true 时连接，为 false 时断开。
     * Returns:
     *     操作成功时为 true。
     */
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint thread, uint targetThread, bool attach);

    /*
     * 请求将窗口移到 Z-Order 顶部。
     *
     * Args:
     *     window - 待置顶的窗口句柄。
     * Returns:
     *     操作成功时为 true。
     */
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool BringWindowToTop(IntPtr window);

    /*
     * 读取整数型的 DWM 窗口属性。
     *
     * Args:
     *     window - 待检查的窗口句柄。
     *     attribute - DWM 属性编号。
     *     value - 返回属性值。
     *     size - 输出缓冲区的字节数。
     * Returns:
     *     查询操作的 HRESULT。
     */
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);

    /*
     * 为打开的注册表键挂接变更通知。
     *
     * Args:
     *     key - 具有 KEY_NOTIFY 权限的注册表键句柄。
     *     watchSubtree - 是否包含所有子键。
     *     filter - 需要监听的变化类型掩码。
     *     changed - 注册表变化时触发的事件句柄。
     *     asynchronous - 为 true 时异步通知。
     * Returns:
     *     Win32 错误码，成功时为 0。
     */
    [DllImport("advapi32.dll")]
    internal static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key,
        bool watchSubtree, uint filter, IntPtr changed, bool asynchronous);

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
