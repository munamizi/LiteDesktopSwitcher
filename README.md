<p align="center">
  <img src="assets/litedesktopswitcher.png" alt="LiteDesktopSwitcher 图标" width="96" height="96">
</p>

<h1 align="center">LiteDesktopSwitcher</h1>

<p align="center">轻量的 Windows 虚拟桌面切换工具，基于 C# 与系统自带 .NET Framework 4.x 编写。</p>
<p align="center">按 <kbd>Alt</kbd> + <kbd>1</kbd> 到 <kbd>Alt</kbd> + <kbd>9</kbd> 直接切换到对应虚拟桌面，后台静默运行且不联网。</p>

## 安装

1. 确认系统为 64 位 Windows 10 或 Windows 11，程序直接使用系统自带的 .NET Framework 4.x，无需安装 AutoHotkey 或额外运行库。
2. 前往 [GitHub Releases](https://github.com/munamizi/LiteDesktopSwitcher/releases/latest) 的 **Assets** 下载 `LiteDesktopSwitcher.exe`，放到常用目录后双击运行。程序在后台静默运行，没有主窗口和托盘图标。

程序暂未进行数字签名，首次运行时 Windows SmartScreen 可能提示未知发布者，确认来源后放行即可。

## 使用

按 `Win+Tab` 可以查看或新建虚拟桌面。`Alt+1` 到 `Alt+9` 对应任务视图中从左到右的桌面顺序，最多支持前 9 个桌面。程序只会注册当前已有桌面对应的快捷键（例如只有 4 个桌面时，只注册 `Alt+1` 到 `Alt+4`）；运行期间新增或删除桌面，热键范围会自动更新，无需重启。

- **开机自启**：按 `Win+R` 输入 `shell:startup`，把 `LiteDesktopSwitcher.exe` 的快捷方式放进打开的文件夹。
- **退出与更新**：在程序目录运行 `.\LiteDesktopSwitcher.exe --exit`，或在任务管理器中结束 `LiteDesktopSwitcher.exe`。等旧进程退出后，再替换并启动新版本。
- **快捷键冲突**：如果某个 `Alt+数字` 已被其他软件占用，程序会弹窗提示；程序会每隔 30 秒重试注册。

### 日志与自检

遇到切换失效或异常时，可查看运行日志：

```text
%LOCALAPPDATA%\LiteDesktopSwitcher\LiteDesktopSwitcher.log
```

也可以带 `--self-test` 参数运行自检，检查桌面拓扑、注册表监听、COM、热键绑定和焦点接口，并在程序同目录生成 `LiteDesktopSwitcher.selftest.txt`，期间不会切换桌面：

```powershell
.\LiteDesktopSwitcher.exe --self-test
```

## 架构

基于 C# / .NET Framework 4.x（Windows x64）编写，单文件源码直接编译为单个 EXE，无第三方依赖，切换过程完全在本机完成且不联网；通过命名 Mutex 保证单实例运行。

程序拆分为「热键线程 + 桌面切换线程」协作运行：

- **热键线程**：在仅消息窗口上注册全局热键。桌面数确定后才开始注册，收到按键时把最新目标交给切换线程。热键冲突提示在后台弹出，定时器只在有热键需要重试时运行。
- **桌面切换线程（`DesktopWorker`）**：监听桌面注册表键的变更，正常空闲时无需轮询。桌面列表变化时更新热键范围，有独立会话列表时按会话读取。收到 Explorer 重启通知后重新挂接监听并重建 COM 连接。
- **窗口焦点**：确认切到目标桌面后，若系统已把焦点交给那里可用的窗口，就保留现有焦点。否则尝试激活目标桌面最上层的可用窗口。切到空桌面时，若焦点仍留在已隐藏的旧窗口上，尝试把焦点交回任务栏或桌面。
- **兼容回退（`NativeDesktopFallback`）**：COM 切换失败时，仅在能确认当前桌面位置的前提下，用 `Win+Ctrl+←/→` 逐步切换并核对位置。若系统不支持任何已知的私有 COM 接口，程序会直接使用回退，等 Explorer 重启后再探测。

## 编译

无需安装 Visual Studio 或下载第三方包。在仓库根目录运行 `build.ps1`，脚本会调用系统自带的 64 位 .NET Framework C# 编译器（`csc.exe`）生成 `LiteDesktopSwitcher.exe`，并输出 SHA-256 校验值：

```powershell
.\build.ps1
```

---

采用 [MIT 许可证](LICENSE)。快捷键交互思路参考了 [choyy/VirtualDesktopSwitcher](https://github.com/choyy/VirtualDesktopSwitcher)，版权信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
