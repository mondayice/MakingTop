// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.Windows.Input;
using static MakingTop.NativeMethods;

namespace MakingTop;

/// <summary>一个快捷键组合：Win32 修饰键位掩码 + 虚拟键码 + 展示文本。</summary>
internal readonly record struct HotkeyCombo(uint Mods, uint Vk, string Display)
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;

    public string Serialized => Serialize(Mods, Vk);

    /// <summary>序列化格式："Ctrl+Alt+S"（与设置持久化、反解析配套）。</summary>
    public static string Serialize(uint mods, uint vk)
    {
        var parts = new List<string>(4);
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(VkToName(vk));
        return string.Join("+", parts);
    }

    /// <summary>解析 "Ctrl+Alt+S" 形式的组合文本；非法返回 false。</summary>
    public static bool TryParse(string? text, out HotkeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        string? keyName = null;
        foreach (var raw in text.Split('+'))
        {
            var token = raw.Trim();
            if (token.Length == 0) return false;
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win" or "windows": mods |= MOD_WIN; continue;
            }
            if (keyName != null) return false; // 出现两个非修饰键
            keyName = token;
        }
        if (keyName == null) return false;

        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key)) return false;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return false;

        combo = new HotkeyCombo(mods, (uint)vk, Serialize(mods, (uint)vk));
        return true;
    }

    private static string VkToName(uint vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey((int)vk);
            return key.ToString();
        }
        catch
        {
            return vk.ToString();
        }
    }
}

/// <summary>
/// 全局热键服务：基于 RegisterHotKey，挂在托盘宿主窗口上，
/// WM_HOTKEY 经 TrayIconService.HotkeyPressed 转发到这里再抛出。
/// </summary>
internal sealed class HotkeyService
{
    public const int IdSelect = 1;        // 开始/取消选择置顶窗口
    public const int IdUnpinCurrent = 2;  // 取消当前（最近置顶）窗口
    public const int IdUnpinAll = 3;      // 取消所有置顶
    public const int IdShowPanel = 4;     // 弹出/隐藏置顶管理面板

    private readonly TrayIconService _tray;

    /// <summary>热键触发（参数为热键 id）。</summary>
    public event Action<int>? HotkeyPressed;

    public HotkeyService(TrayIconService tray)
    {
        _tray = tray;
        tray.HotkeyPressed += id => HotkeyPressed?.Invoke(id);
    }

    /// <summary>
    /// 绑定/换绑/解绑一个热键（立即生效）。
    /// comboText 为空 = 解绑。
    /// 返回 (是否成功, 状态描述文本，供设置界面显示)。
    /// </summary>
    public (bool Ok, string Status) Apply(int id, string? comboText)
    {
        IntPtr hwnd = _tray.HostHwnd;
        UnregisterHotKey(hwnd, id); // 先解绑旧的（换绑/解绑语义）

        if (string.IsNullOrWhiteSpace(comboText))
            return (true, "未绑定");

        if (!HotkeyCombo.TryParse(comboText, out var combo))
            return (false, "组合无效");

        if (!RegisterHotKey(hwnd, id, combo.Mods, combo.Vk))
            return (false, "注册失败：快捷键被占用");

        return (true, $"已绑定 {combo.Display}");
    }
}
