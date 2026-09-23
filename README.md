# MakingTop · 窗口置顶托盘小工具

无主窗口的 Windows 托盘小工具：把任意窗口置顶，并在其左上角显示一枚可点击的 Notion 橙「图钉」悬浮图标。事件驱动、零轮询，空闲 CPU 占用约 0%。

![风格](https://img.shields.io/badge/%E9%A3%8E%E6%A0%BC-Notion%20%E6%9A%96%E6%A9%9F-E16259)
![平台](https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-Windows%2010%2F11-37352F)
![作者](https://img.shields.io/badge/%E4%BD%9C%E8%80%85-Mondayice-E16259)

## 功能

| 交互 | 说明 |
|------|------|
| **开始选择置顶窗口** | 托盘菜单点击后，光标变为橙色图钉 → 点击任意窗口即置顶；再进入选择模式点击同一窗口则取消置顶（Esc 取消选择） |
| **悬浮图钉** | 被置顶窗口客户区左上角显示 24×24 圆角图钉（跟随窗口移动/缩放/DPI，多窗口各自独立）；点击图钉 = 直接取消该窗口置顶 |
| **取消当前选中窗口置顶** | 随置顶数量自动变形：**0 个**→灰色禁用；**1 个**→显示「取消置顶：窗口标题」，点击直接取消；**多个**→hover 展开子列表列出全部置顶窗口（按置顶顺序），点击某一项取消对应窗口 |
| **设置（快捷键）** | Apple 风格设置窗口：录制 **4 个全局快捷键**——开始选择置顶 / 取消当前窗口置顶 / 取消所有置顶 / 打开置顶管理面板。修改立即生效，保存到 `%APPDATA%\MakingTop\settings.json`。**默认已绑定**：`Ctrl+Alt+P` 选择置顶、`Ctrl+Alt+U` 取消当前、`Ctrl+Alt+L` 全部取消、`Ctrl+Alt+M` 管理面板；录制规则：需含 Ctrl/Alt/Shift/Win 之一（或单独 F 功能键），Esc 取消、Backspace 清除；支持「恢复默认」与「全部清除」，冲突时行内提示 |
| **置顶管理面板** | Apple 风格弹窗：列出系统中**全部可管理的顶层窗口**（置顶的排最前带橙色圆点，最小化窗口有标注），每行一个开关实时反映置顶状态——开=置顶、关=取消；任何来源的置顶变化都会即时同步开关；支持「全部取消」。默认快捷键 `Ctrl+Alt+M`，也可从设置窗口一键打开 |
| **取消所有窗口置顶** | 一键还原全部（所有悬浮图标随之消失） |
| **退出程序** | 先取消所有置顶（系统恢复原状），再移除托盘图标并结束进程 |

细节行为：

- 目标窗口**关闭** → 自动移除置顶状态与悬浮图标
- 目标窗口**最小化** → 悬浮图标暂时隐藏，还原后自动恢复
- 目标窗口被其他工具**取消置顶** → 本工具静默跟随清理
- Explorer 崩溃重启后托盘图标自动补挂
- 选择模式期间点击被「吞掉」，不会误触目标窗口里的按钮
- 单实例运行（重复启动只提示）；不开机自启；仅托盘菜单退出才结束进程

## 编译与运行

### 环境要求

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（本机已安装 8.0.425；运行只需 .NET 8 Desktop Runtime）

### 编译

```bat
cd /d D:\Repository\Tool\MakingTop
dotnet build -c Release
```

产物：`bin\Release\net8.0-windows\MakingTop.exe`

### 运行

双击 `MakingTop.exe`，或在终端：

```bat
bin\Release\net8.0-windows\MakingTop.exe
```

任务栏托盘出现橙色图钉图标 → 右键打开菜单开始使用。

### 可选：发布绿色单文件

```bat
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

产物在 `bin\Release\net8.0-windows\win-x64\publish\`，可拷贝到无 .NET 环境的机器运行。

## 交互验收清单

1. 启动后托盘出现橙色图钉图标
2. 托盘右键 →【开始选择置顶窗口】→ 光标变为橙色图钉
3. 点击任意窗口 → 该窗口置顶，左上角出现浅橙圆角图钉悬浮图标
4. 拖动/缩放被置顶窗口 → 悬浮图标实时跟随、无拖影
5. 最小化 → 图标隐藏；还原 → 图标恢复
6. 悬浮图标 hover → 变不透明、底色加深；点击 → 缩小动画后窗口取消置顶、图标消失
7. 多个窗口置顶 → 各自独立图标；【取消所有窗口置顶】→ 全部消失
8. Esc 可取消选择模式；【退出程序】→ 所有置顶还原、托盘图标消失、进程结束

## 已知限制

- **管理员权限**：程序默认以管理员身份运行（每次启动会弹 UAC 确认），因此可以置顶同样以管理员运行的目标窗口（任务管理器、管理员终端等）。
- **独占全屏 DirectX 游戏**：exclusive fullscreen 模式下置顶窗口/悬浮图标可能被游戏画面覆盖（Windows 系统限制）；无边框窗口化游戏不受影响。
- 选择模式下光标替换的是系统级光标，程序异常退出时也会在下次启动光标设置刷新后还原（正常路径退出时立即还原）。

## 技术说明

- **栈**：C# / WPF（net8.0-windows）+ 纯 Win32 P/Invoke（user32/gdi32/shell32），零第三方 NuGet 包
- **跟随机制**：`SetWinEventHook` 订阅 `EVENT_OBJECT_LOCATIONCHANGE / REORDER / MINIMIZESTART / MINIMIZEEND / OBJECT_DESTROY`，事件驱动 + Dispatcher 脏标记合并（一帧最多重定位一次），空闲零轮询
- **Z 序维护**：点击激活等操作把目标窗口顶到图标之上时，把**自己的 overlay 窗口**重新插到目标正上方（同线程同步操作，无跨进程阻塞/异步失败问题）；置顶/取消置顶的目标窗口操作用 `SWP_ASYNCWINDOWPOS` 异步投递防卡死
- **悬浮图标**：无边框透明 WPF 窗口（`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`），`SetWindowPos` 按物理像素定位在目标客户区左上角 +6px；`app.manifest` 声明 PerMonitorV2，跨显示器 DPI 正确
- **Z 序**：悬浮图标常驻 TOPMOST band，目标窗口被槽到图标正下方；顺序已正确时不调用 `SetWindowPos`，避免事件风暴
- **选择模式**：`WH_MOUSE_LL / WH_KEYBOARD_LL` 低级钩子 + `SetSystemCursor` 临时替换系统光标为橙色图钉（热点=针尖），退出时 `SPI_SETCURSORS` 从注册表整体还原
- **托盘**：`Shell_NotifyIcon` + 隐藏 `HwndSource` 回调窗口，监听 `TaskbarCreated` 广播自动恢复图标
- **全局热键**：`RegisterHotKey` 挂在托盘宿主窗口，`WM_HOTKEY` 分发；配置 JSON 持久化（System.Text.Json）
- **Apple 风格窗口**：无边框圆角 + 柔和投影 + 卡片分组 + Apple 开关（150ms ease-out）+ 打开淡入缩放动画；控件库见 `Controls.xaml`
- **自动化自检**：`MakingTop.exe --selftest` 会把当前前台窗口置顶 3 秒并校验悬浮图标真实创建，结果写入 `%TEMP%\MakingTop-selftest.log`（退出码 0=通过），随后自动还原并退出

## 许可证

本项目基于 [MIT License](LICENSE) 开源发布。

Copyright © 2026 Mondayice (mondayice123@163.com)

程序与源码的版权信息同时嵌入在 exe 元数据（文件属性 → 详细信息）与各源码文件头部；
程序图标由 `tools/IconGen` 从主程序同一份矢量路径生成，保证托盘 / 悬浮图标 / exe 图标视觉一致。
