using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace MystiaGuestTag;

/// <summary>
/// 诊断相关的统一入口（m07921）。
/// 为什么需要它：BepInEx 的 LogDebug 只有在“已经拼好字符串”之后才会被丢弃，
/// 而插值字符串构建 + StringBuilder 分配本身就是白天掉帧的来源之一。
/// 这里把「是否输出」判断提到最前面，没开 Verbose 时连字符串都不会生成。
/// </summary>
internal static class Diag
{
    /// <summary>是否输出调试日志（来自 cfg 的 Logging.VerboseLog，默认 false）。</summary>
    internal static bool Verbose;

    /// <summary>固定字符串日志：只在开 Verbose 时输出。</summary>
    internal static void Dbg(string message)
    {
        if (!Verbose) return;                                                  // 没开：直接走人，零分配
        Plugin.Logger.LogDebug(message);                                       // 开了：真正写日志
    }

    /// <summary>插值日志：传委托进来，没开 Verbose 时连字符串都不会拼。</summary>
    internal static void DbgSb(Func<string> build)
    {
        if (!Verbose) return;                                                  // 没开：连委托都不调用
        Plugin.Logger.LogDebug(build());                                       // 开了：拼一次
    }

    // ---------------- 轻量计时（用于抓“OnGUI 到底多贵”） ----------------

    private static long _lastSlowTicks;                                        // 上次慢帧日志的时间戳
    private static long _lastLogTicks;                                         // 上一次记账的时间戳
    private static int _frames;                                                // 期间进了几次 OnGUI
    private static double _worstMs;                                            // 期间最慢的一次（毫秒）

    /// <summary>
    /// 记录一次 OnGUI postfix 的耗时：只在「单次 > 2ms」或「每 30 秒一条汇总」时写 Info，
    /// 日志量极低，但能直接看出是不是我们在吃 CPU。
    /// </summary>
    internal static void NoteFrame(long startedTicks)
    {
        var now = Stopwatch.GetTimestamp();                                    // 当前高精度时间
        var ms = (now - startedTicks) * 1000.0 / Stopwatch.Frequency;          // 换算成毫秒
        _frames++;                                                             // 计数 +1
        if (ms > _worstMs) _worstMs = ms;                                      // 记住最慢

        if (ms >= 2.0 && now - _lastSlowTicks > Stopwatch.Frequency * 5)        // 单次 >= 2ms 且距上次 > 5 秒
        {
            _lastSlowTicks = now;                                              // 记时间
            Plugin.Logger.LogInfo($"[GuestTag] 慢帧：OnGUI 用了 {ms:F2} ms（第 {UnityEngine.Time.frameCount} 帧）");
        }

        if (now - _lastLogTicks > Stopwatch.Frequency * 30)                    // 每 30 秒一条汇总
        {
            var avg = _frames > 0 ? _worstMs / _frames : 0;                     // 只是粗略占比，够用
            Plugin.Logger.LogInfo($"[GuestTag] 性能：30 秒内 OnGUI {_frames} 次，最慢 {_worstMs:F2} ms，平均 {avg:F3} ms");
            _lastLogTicks = now;                                               // 复位窗口
            _frames = 0;                                                       // 清零
            _worstMs = 0;                                                      // 清零
        }
    }
}
