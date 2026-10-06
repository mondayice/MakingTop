// -----------------------------------------------------------------------------
// MakingTop · Windows 窗口置顶托盘小工具
// Copyright (c) 2026 Mondayice (mondayice123@163.com)  Licensed under the MIT License.
// 作者: Mondayice <mondayice123@163.com>
// -----------------------------------------------------------------------------

using System.IO;

namespace MakingTop;

/// <summary>
/// 轻量诊断日志：%LOCALAPPDATA%\MakingTop\diag.log。
/// 只记录置顶链路的关键节点（开始/结果/取消原因），正常使用每天仅数行；
/// 超过 512KB 轮转为 .old。写入失败静默吞掉，绝不影响主流程。
/// 用户反馈「偶发」类问题时，此日志是唯一的现场证据来源。
/// </summary>
internal static class DiagLog
{
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakingTop");
    private static readonly string FilePath = Path.Combine(Dir, "diag.log");
    private const long MaxBytes = 512 * 1024;

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    string old = FilePath + ".old";
                    File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath,
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}", DateTime.Now, msg, Environment.NewLine));
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
