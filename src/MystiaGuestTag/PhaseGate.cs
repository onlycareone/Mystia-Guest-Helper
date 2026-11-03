using System;
using GameData.RunTime.Common;

namespace MystiaGuestTag;

/// <summary>
/// 营业相位闸门：只在夜晚营业（Work）阶段读游戏对象。
/// 读档 / 启动加载期游戏对象处于半成品状态，此时访问 IL2CPP 属性有 native AV 风险，
/// 闸门本身只读一个 static 枚举属性，任何异常一律视为「非营业」。
/// </summary>
internal static class PhaseGate
{
    /// <summary>
    /// 上一次相位查询的时间戳与结果。
    /// 为什么记忆：OnGUI 一帧会进好几次（Layout / Repaint / 鼠标事件），而
    /// RunTimeScheduler.CurrentGamePhase 是一次 IL2CPP 静态属性调用。
    /// 白天跑图时每帧都去问一遍纯属浪费，0.25 秒问一次足够（相位切换不是毫秒级的事）。
    /// </summary>
    private static int _lastFrame = -999;
    private static bool _lastResult;

    /// <summary>
    /// 带记忆的相位闸门：白天（非营业）返回 false，调用方直接 return，不做任何计算与绘制。
    /// </summary>
    internal static bool IsNightWorkPhaseFast()
    {
        // m08103：先按「场景名」判白天——这一步不碰 IL2CPP，白天连相位都不问。
        if (SceneGate.IsDayScene()) return false;

        // 按帧节流：OnGUI 一帧会进好几次（Layout / Repaint / 事件），
        // 但同一个 Time.frameCount 下相位不可能变，所以同帧直接复用结果，跨帧最多 15 帧问一次。
        if (UnityEngine.Time.frameCount - _lastFrame < 15) return _lastResult; // 同一帧 / 15 帧内：复用缓存

        _lastFrame = UnityEngine.Time.frameCount;                     // 记下这次查询的帧号
        _lastResult = IsNightWorkPhase();                             // 真正去问游戏（带异常保护）
        return _lastResult;                                           // 返回结果
    }

    internal static bool IsNightWorkPhase()
    {
        try
        {
            return RunTimeScheduler.CurrentGamePhase == RunTimeScheduler.GamePhase.Work;
        }
        catch
        {
            return false;
        }
    }
}
