// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  All Rights Reserved.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.IO;
using System.Text.Json;

namespace MakingTop;

/// <summary>
/// 应用设置（JSON 持久化到 %APPDATA%\MakingTop\settings.json）。
/// 保存 4 个全局快捷键的组合文本。
/// null = 未设置（应用默认值）；空串 = 用户显式清除（不绑定）。
/// </summary>
internal class AppSettings
{
    // 默认快捷键（避免常见冲突的语义化组合）
    public const string DefaultSelect = "Ctrl+Alt+P";        // P = Pin
    public const string DefaultUnpinCurrent = "Ctrl+Alt+U";  // U = Unpin
    public const string DefaultUnpinAll = "Ctrl+Alt+L";      // L = 释放全部
    public const string DefaultShowPanel = "Ctrl+Alt+M";     // M = Manage

    public string? HotkeySelect { get; set; } = DefaultSelect;
    public string? HotkeyUnpinCurrent { get; set; } = DefaultUnpinCurrent;
    public string? HotkeyUnpinAll { get; set; } = DefaultUnpinAll;
    public string? HotkeyShowPanel { get; set; } = DefaultShowPanel;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MakingTop");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时回退到默认值，不影响启动
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 保存失败不致命：快捷键本次会话内仍有效
        }
    }
}
