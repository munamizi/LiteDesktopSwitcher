<p align="center">
  <img src="assets/litedesktopswitcher.png" alt="LiteDesktopSwitcher 图标" width="96" height="96">
</p>

<h1 align="center">LiteDesktopSwitcher</h1>

<p align="center">轻量的 Windows 虚拟桌面切换工具，基于 C# 与系统自带 .NET Framework 4.x 编写。</p>
<p align="center">按 <kbd>Alt</kbd> + <kbd>1</kbd> 到 <kbd>Alt</kbd> + <kbd>9</kbd> 直接切换到对应虚拟桌面，后台静默运行且不联网。</p>

## 安装

1. 确认系统为 64 位 Windows 10 或 Windows 11，程序直接使用系统自带的 .NET Framework 4.x，无需安装 AutoHotkey 或额外运行库。
2.  [GitHub Releases](https://github.com/munamizi/LiteDesktopSwitcher/releases/latest) 的 **Assets** 下载 `LiteDesktopSwitcher.exe`，放到常用目录后双击运行。程序在后台静默运行，没有主窗口和托盘图标。

程序暂未进行数字签名，首次运行时 Windows SmartScreen 可能提示未知发布者，确认来源后放行即可。

## 使用

按 `Win+Tab` 可以查看或新建虚拟桌面。`Alt+1` 到 `Alt+9` 对应任务视图中从左到右的桌面顺序，最多支持前 9 个桌面。程序只会注册当前已有桌面对应的快捷键（例如只有 4 个桌面时，只注册 `Alt+1` 到 `Alt+4`）；运行期间新增或删除桌面，热键范围会自动更新，无需重启。

- **开机自启**：按 `Win+R` 输入 `shell:startup`，把 `LiteDesktopSwitcher.exe` 的快捷方式放进打开的文件夹。
- **退出与更新**：在任务管理器中结束 `LiteDesktopSwitcher.exe`。程序为单实例运行，更新前需先结束旧进程再替换并启动，否则新进程会直接退出。
- **快捷键冲突**：如果某个 `Alt+数字` 已被其他软件占用，程序会弹窗提示；等对应快捷键释放后，程序会自动重试注册。

### 日志与自检

遇到切换失效或异常时，可查看运行日志：

```text
%LOCALAPPDATA%\LiteDesktopSwitcher\LiteDesktopSwitcher.log
```

也可以带 `--self-test` 参数运行自检，检查桌面枚举、COM 接口和热键注册机制，并在程序同目录生成 `LiteDesktopSwitcher.selftest.txt`，期间不会切换桌面：

```powershell
.\LiteDesktopSwitcher.exe --self-test
```

## 架构

基于 C# / .NET Framework 4.x（Windows x64）编写，单文件源码直接编译为单个 EXE，无第三方依赖，切换过程完全在本机完成且不联网；通过命名 Mutex 保证单实例运行。

程序拆分为「热键线程 + 桌面切换线程」协作运行：

- **热键线程**：通过原生 Win32 `RegisterHotKey` 监听全局热键，按当前桌面数量动态维护注册范围；收到按键后只投递目标编号，不阻塞输入响应，并在切换期间合并连续请求、保留最后一次目标。
- **桌面切换线程（`DesktopWorker`）**：优先读取当前会话的注册表状态（兼容 RDP 与多会话）跟踪桌面变化，调用 Windows 虚拟桌面 COM 接口直达目标，支持多版本 Windows 10 / 11 接口布局，并在 Explorer 重启后自动重建连接。
- **兼容回退（`NativeDesktopFallback`）**：COM 接口重试仍失败时，仅在能确认当前桌面位置的前提下，改用系统快捷键 `Win+Ctrl+←/→` 逐步切换并逐次核对位置。Windows 虚拟桌面 COM 接口并未公开，后续系统大版本更新后可能需要跟进适配。

## 编译

无需安装 Visual Studio 或下载第三方包。在仓库根目录运行 `build.ps1`，脚本会调用系统自带的 64 位 .NET Framework C# 编译器（`csc.exe`）生成 `LiteDesktopSwitcher.exe`，并输出 SHA-256 校验值：

```powershell
.\build.ps1
```

---

采用 [MIT 许可证](LICENSE)。快捷键交互思路参考了 [choyy/VirtualDesktopSwitcher](https://github.com/choyy/VirtualDesktopSwitcher)，版权信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
