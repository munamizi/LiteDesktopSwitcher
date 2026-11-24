<p align="center">
  <img src="assets/litedesktopswitcher.png" alt="LiteDesktopSwitcher 图标，两张灰色桌面与一支切换箭头" width="96" height="96">
</p>

<h1 align="center">LiteDesktopSwitcher</h1>

<p align="center">轻量的 Windows 虚拟桌面切换工具。按 <kbd>Alt</kbd> + <kbd>1</kbd> 到 <kbd>Alt</kbd> + <kbd>9</kbd>，直达对应桌面。</p>
<p align="center"><a href="README.en.md">English</a></p>

## 安装

适用于 64 位 Windows 10 和 Windows 11，使用 Windows 自带的 .NET Framework 4.x，无需安装 AutoHotkey。

1. 从 [GitHub Releases](https://github.com/munamizi/LiteDesktopSwitcher/releases/latest) 的 **Assets** 下载 `LiteDesktopSwitcher.exe`。
2. 将 EXE 放到常用目录，双击运行。程序在后台运行，没有主窗口或托盘图标。

EXE 暂未进行数字签名，首次运行时 Windows SmartScreen 可能提示未知发布者。

## 使用

按 `Win+Tab` 查看或新建虚拟桌面。数字键对应任务视图中从左到右的桌面顺序。例如按 `Alt+3` 会切换到第 3 个桌面。运行期间新增或删除桌面，程序会自动更新可用的快捷键范围，最多支持前 9 个桌面。

- **开机启动**　按 `Win+R`，输入 `shell:startup`，把 EXE 的快捷方式放进打开的文件夹。
- **退出或更新**　在任务管理器中结束 `LiteDesktopSwitcher.exe`。更新时先退出旧版，再替换并运行 EXE。
- **快捷键冲突**　程序会弹出提示。释放被其他程序占用的快捷键后，LiteDesktopSwitcher 会自动重试注册。

遇到切换异常时，可查看 `%LOCALAPPDATA%\LiteDesktopSwitcher\LiteDesktopSwitcher.log`。以 `--self-test` 参数运行 EXE 会检查桌面枚举和热键机制，在 EXE 同目录生成自检报告，期间不会切换桌面。

## 架构

程序用 C# 编写，以单个 x64 EXE 运行。切换过程在本机完成，程序无需联网。

- **热键线程**　通过 Win32 `RegisterHotKey` 注册全局快捷键。收到 `Alt+数字` 后，把目标桌面编号交给后台线程处理。
- **桌面切换线程**　读取 Windows 注册表中的桌面列表，数量变化时重建桌面管理接口。切换时优先用 Windows 的虚拟桌面 COM 接口直达目标。
- **兼容回退**　COM 切换重试仍失败时，程序在能确认当前桌面位置的情况下，用系统的 `Win+Ctrl+←/→` 逐步到达目标，每一步都核对位置。COM 接口未公开，Windows 更新后可能需要适配。

在仓库目录运行 `build.ps1` 即可编译。脚本调用系统自带的 64 位 .NET Framework C# 编译器。

---

项目采用 [MIT 许可证](LICENSE)。核心交互参考了 [choyy/VirtualDesktopSwitcher](https://github.com/choyy/VirtualDesktopSwitcher)，版权信息见 [第三方声明](THIRD_PARTY_NOTICES.md)。
