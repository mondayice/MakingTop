// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Windows.Interop;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>
/// 托盘图标服务：Shell_NotifyIcon + 隐藏回调窗口。
/// - 右键/左键点击托盘图标 → 抛出事件（由 App 弹出菜单）
/// - 监听 TaskbarCreated 广播，Explorer 崩溃重启后自动补挂图标
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    /// <summary>托盘回调自定义消息（WM_APP 段，避免与系统消息冲突）。</summary>
    public const int CallbackMessage = WM_APP + 1;

    private const uint TrayId = 1;
    private const string TipText = "MakingTop · 窗口置顶小工具";

    private readonly HwndSource _host;
    private readonly uint _taskbarCreatedMsg;
    private IntPtr _icon;
    private bool _added;

    /// <summary>左键单击托盘图标。</summary>
    public event Action? TrayLeftClick;

    /// <summary>右键单击托盘图标。</summary>
    public event Action? TrayRightClick;

    /// <summary>全局热键触发（参数为 RegisterHotKey 时的 id）。</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>托盘宿主窗口句柄（弹菜单前给它前台焦点，保证菜单点击外部可关闭）。</summary>
    public IntPtr HostHwnd => _host.Handle;

    public TrayIconService()
    {
        // TaskbarCreated：Explorer 重启时系统广播该消息，托盘图标需要重新挂载
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        // 隐藏的顶层窗口（不 Show，仅接收消息）
        var p = new HwndSourceParameters("MakingTopTrayHost")
        {
            WindowStyle = 0x00000000,      // WS_OVERLAPPED（不可见即可）
            PositionX = -32000,
            PositionY = -32000,
            Width = 0,
            Height = 0,
        };
        _host = new HwndSource(p);
        _host.AddHook(WndProc);

        _icon = IconFactory.CreateTrayIconHandle();
    }

    /// <summary>挂载托盘图标。</summary>
    public void Show()
    {
        var nid = BuildData();
        if (Shell_NotifyIconW(NIM_ADD, ref nid))
            _added = true;
    }

    /// <summary>托盘图标点击后重绘图标（Explorer 主题变化等场景可选调用）。</summary>
    public void RefreshIcon()
    {
        if (!_added) return;
        var nid = BuildData();
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    private NOTIFYICONDATA BuildData()
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _host.Handle,
            uID = TrayId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (uint)CallbackMessage,
            hIcon = _icon,
            szTip = TipText,
        };
        return nid;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // lParam 低 16 位是鼠标消息
            int mouseMsg = (short)((long)lParam & 0xFFFF);
            switch (mouseMsg)
            {
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    TrayRightClick?.Invoke();
                    handled = true;
                    break;
                case WM_LBUTTONUP:
                case NIN_SELECT:
                case WM_LBUTTONDBLCLK:
                    TrayLeftClick?.Invoke();
                    handled = true;
                    break;
            }
        }
        else if (msg == (int)_taskbarCreatedMsg && _added)
        {
            // Explorer 重启后任务栏重建 → 重新挂图标
            var nid = BuildData();
            Shell_NotifyIconW(NIM_ADD, ref nid);
            handled = true;
        }
        else if (msg == WM_HOTKEY)
        {
            // 全局热键：wParam 为注册时的 id
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            var nid = BuildData();
            Shell_NotifyIconW(NIM_DELETE, ref nid);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _host.RemoveHook(WndProc);
        _host.Dispose();
    }
}
