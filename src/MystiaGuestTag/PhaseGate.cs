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
