# LiteDesktopSwitcher

[English](README.en.md)

一个只做一件事的 Windows 小工具：按 `Alt+1` 到 `Alt+9`，直接切换到对应的虚拟桌面。

它没有设置界面和托盘菜单，启动后在后台运行。不联网，不收集遥测，也不需要安装运行库。

## 下载与使用

1. 从下载运行`LiteDesktopSwitcher.exe`
2. 使用 `Alt+1` 到 `Alt+9` 切换虚拟桌面。

程序只会注册已有桌面对应的快捷键。例如当前有 4 个桌面时，只注册 `Alt+1` 到 `Alt+4`。如果快捷键被其他程序占用，会显示提示并定时重试。

程序没有窗口。需要退出时，请在任务管理器中结束 `LiteDesktopSwitcher.exe`。

## 开机启动

按 `Win+R`，输入 `shell:startup`，把 `LiteDesktopSwitcher.exe` 或它的快捷方式放进打开的文件夹。

更新前请先在任务管理器中退出旧版本。程序采用单实例运行，旧版本仍在后台时，新版本会直接退出。

## 兼容性

- 64 位 Windows 10 / Windows 11
- 系统自带的 .NET Framework 4.x
- 最多支持 9 个虚拟桌面

程序优先使用 Windows 的虚拟桌面 COM 接口；接口不可用时，会在确认当前桌面位置后回退到系统快捷键。该 COM 接口没有公开文档，因此未来较大的 Windows 更新仍可能带来兼容性变化。

发布的 EXE 暂未进行代码签名，Windows SmartScreen 首次运行时可能显示提示。请从本仓库的 Release 下载，并用发布页提供的 SHA-256 校验值核对文件。

## 日志与自检

运行日志位于：

```text
%LOCALAPPDATA%\LiteDesktopSwitcher\LiteDesktopSwitcher.log
```

下面的自检只会枚举桌面、检查 COM 接口并临时注册测试热键，不会切换桌面：

```powershell
.\LiteDesktopSwitcher.exe --self-test
$LASTEXITCODE
Get-Content .\LiteDesktopSwitcher.selftest.txt
```

## 从源码编译

在 64 位 Windows 10/11 上运行：

```powershell
.\build.ps1
```

脚本使用系统自带的 64 位 .NET Framework C# 编译器，不会下载依赖。

## 致谢

核心交互参考了 [choyy/VirtualDesktopSwitcher](https://github.com/choyy/VirtualDesktopSwitcher)。原项目采用 MIT 许可证，版权信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 许可证

[MIT](LICENSE)
