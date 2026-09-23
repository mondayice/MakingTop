# MakingTop 任务计划 — Windows 窗口置顶托盘小工具

## 目标
开发一款纯原生依赖的 Windows 托盘小工具：选择模式置顶任意窗口、被置顶窗口左上角显示可点击的 Notion 橙悬浮图钉图标、托盘右键菜单管理置顶。事件驱动、零轮询、低占用。

## 已确认决策
| 决策 | 结论 |
|------|------|
| 编译方式 | 正式安装 .NET 8 SDK（用户确认），`dotnet build`，net8.0-windows + UseWPF |
| 退出行为 | 退出时自动取消所有置顶（用户确认） |
| 「取消当前选中窗口置顶」 | = 最近一次置顶的窗口，菜单项动态显示其标题 |
| 选择模式 | 单发模式：一次点击完成置顶/取消即退出选择模式；Esc 取消；选择期间点击被吞掉防误触 |
| 视觉 | Notion 橙：#E16259/#FDEDEC/#FAD2CF/#F7F6F3/#37352F/#9B9A97/#E9E9E7 |
| 依赖 | 零第三方包，仅 WPF + Win32 P/Invoke |

## 阶段
- [x] 阶段1：需求澄清与环境勘察（无 SDK/MSBuild；有 Desktop Runtime 8.0.19；目录为空）
- [x] 阶段2：方案设计并获用户批准
- [x] 阶段3：安装 .NET 8 SDK（winget，8.0.425）
- [x] 阶段4：编写全部源码（csproj/manifest/Theme/NativeMethods/IconFactory/Tray/Menu/PinManager/Overlay/Selection/App）
- [x] 阶段5：编译调通（0 错误 0 警告）
- [x] 阶段6：运行验证（进程存活/托盘宿主窗口存在/无崩溃）+ README

## 关键技术点
- 跟随：SetWinEventHook(OUTOFCONTEXT|SKIPOWNPROCess) 事件驱动 + Dispatcher 脏标记合并（每帧最多一次重定位）
- 悬浮图标：无边框透明 WPF 窗口，WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW，SetWindowPos 物理像素定位（客户区左上角+6px）
- z 序：overlay 置于 TOPMOST band，target 槽到 overlay 正下方；GetWindow(GW_HWNDPREV) 检查避免 SetWindowPos 风暴
- 选择模式：WH_MOUSE_LL/WH_KEYBOARD_LL + SetSystemCursor 换橙色图钉光标，退出时 SystemParametersInfo(SPI_SETCURSORS) 还原
- 托盘：Shell_NotifyIconV2 + 隐藏 HwndSource 回调窗口 + TaskbarCreated 广播恢复图标；SetForegroundWindow 修菜单不消失问题
- DPI：app.manifest 声明 PerMonitorV2，overlay 尺寸=24×(dpi/96)，Viewbox 拉伸内容

## 遇到的错误
| 错误 | 尝试次数 | 解决方案 |
|------|---------|---------|
| 用户点击置顶后进程 FailFast 崩溃、悬浮图标未显示 | 1 | 事件日志定位：WinEventDelegate 被 GC 回收后系统回调踩空（FailFast）。修复：PinManager 用字段保住委托引用；并新增 --selftest 自动化验证置顶→悬浮图标全链路，自检 PASS |
| 编译错误 5 处（缺 using、三元作语句、可访问性） | 2 | 补 using System.Windows / System.Windows.Media.Animation / System.IO；改 if-else；ctor 改 internal |
