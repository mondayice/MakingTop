# findings — 环境与技术事实

## 环境（2026-09-23 勘察）
- Windows 10 22H2 (19045)，Git Bash
- 工作目录 D:\Repository\Tool\MakingTop 为空，非 git 仓库
- 无 .NET SDK、无 MSBuild/VS（2022 目录为卸载残留）
- 已装 .NET Desktop Runtime 6.0.31 / 8.0.19（WPF 程序可直接运行）
- Windows 自带 csc.exe (Framework 4.8) 可用但仅 C# 5
- 用户已选择：正式安装 .NET 8 SDK

## 技术事实
- WPF net8 默认可在 app.manifest 声明 PerMonitorV2 获得逐显示器 DPI 感知
- AllowsTransparency=True 的 WPF 窗口无非客户区，可用 SetWindowPos 以物理像素精确控制
- SetWindowPos 的 hWndInsertAfter 语义：被定位窗口放到 insertAfter 之下 → 想让 overlay 在 target 之上，应 SetWindowPos(target, overlay, ...)
- EVENT_OBJECT_DESTROY(0x8001) 触达时窗口可能已半销毁，只清理自身状态、不要再碰该窗口
- LL 鼠标钩子回调必须极快，重活 BeginInvoke 甩给 UI 线程
- SetSystemCursor 会接管并销毁传入的 HCURSOR，需为每个替换的光标 ID 创建独立句柄；SPI_SETCURSORS 从注册表整体还原
- WPF ContextMenu 弹在托盘上：先 SetForegroundWindow(宿主hwnd) 再 IsOpen=true，否则点击外部不关闭
- NOTIFYICONDATA 完整 V3 布局（含 guidItem）即可获得 128 字符 tooltip 支持
- PowerShell EnumWindows 回调内 Write-Output 不冒泡到管道，需收集进 ArrayList 再输出；FindWindowW 按 title 找 HwndWrapper 窗口不可靠，用 EnumWindows+PID 过滤稳妥
- Material push_pin 图标路径（24×24）："M16 9V4h1c.55 0 1-.45 1-1s-.45-1-1-1H7c-.55 0-1 .45-1 1s.45 1 1 1h1v5c0 1.66-1.34 3-3 3v2h5.97v7l1 1 1-1v-7H19v-2c-1.66 0-3-1.34-3-3z"
- 本机现为 .NET 8 SDK 8.0.425（winget 安装），dotnet build 可用
- **委托生命周期铁律**：SetWinEventHook/SetWindowsHookEx 等原生回调的委托必须用字段保住引用，否则 GC 回收后触发即 `Environment.FailFast`（coreclr 0x80131623），进程无 MessageBox 秒退；事件日志 .NET Runtime Id=1025 记录可定位
- WPF 项目（UseWPF）隐式 using 不含 System.IO，用 Path/File 需显式 using
