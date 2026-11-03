using System;

namespace MystiaGuestTag;

/// <summary>
/// 场景级硬闸门（m08103）：只读 Unity 自己的当前场景名，一次都不碰 IL2CPP 对象。
/// 为什么需要它：相位判定（RunTimeScheduler.CurrentGamePhase）本身就是一次 IL2CPP 静态属性调用，
/// 白天跑图时哪怕 15 帧问一次，也会持续产生托管调用开销。这里先用纯 Unity API 判「这是不是白天场景」，
/// 是白天就直接返回，连相位都不问。
/// </summary>
internal static class SceneGate
{
    private static int _cachedFrame = -1;                 // 缓存对应的帧号
    private static string _sceneName = string.Empty;      // 缓存的场景名
    private static string _lastLogged = string.Empty;     // 上次打印过的场景名（避免刷屏）

    /// <summary>当前激活场景名（同一帧只问一次 Unity）。</summary>
    internal static string SceneName()
    {
        var frame = UnityEngine.Time.frameCount;          // 当前帧号
        if (frame == _cachedFrame) return _sceneName;     // 同帧：直接复用

        _cachedFrame = frame;                             // 记下帧号
        try
        {
            _sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
        }
        catch
        {
            _sceneName = string.Empty;                    // 读失败就当未知，不影响后续判断
        }

        if (!string.Equals(_sceneName, _lastLogged, StringComparison.Ordinal))   // 场景变了才打一行
        {
            _lastLogged = _sceneName;                                            // 记下来
            Plugin.Logger.LogInfo($"[GuestTag] 当前场景：{(_sceneName.Length == 0 ? "(未知)" : _sceneName)}");
        }

        return _sceneName;
    }

    /// <summary>是否白天场景（名字里带 day 就算；读不到名字返回 false，交给相位闸门兜底）。</summary>
    internal static bool IsDayScene()
    {
        var name = SceneName();                                                       // 取场景名
        return name.Length > 0 && name.IndexOf("day", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
