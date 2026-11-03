using System;
using System.Collections.Generic;
using GameData.Core.Collections;
using NightScene.CookingUtility;
using UnityEngine;

namespace MystiaGuestTag;

/// <summary>
/// 厨具闸门（m03688）：营业中扫描台上真正摆着的 CookController，拿到它们各自的 Cooker.Type，
/// 用来把「今晚根本没法做的菜」从推荐池里踢掉。
///
/// 为什么这样读：
/// - 游戏里一道菜只声明 `Recipe.CookerType`（Pot/Grill/Fryer/Steamer/CuttingBoard），
///   并没有「菜谱要求某个厨具等级」的字段，所以这里按「种类」判定能不能做；
/// - 玩家今晚带了哪些厨具，最可靠的运行时来源就是场景里活着的 CookController 上的 Cooker；
/// - 读不到（读档瞬间、厨具未加载）时不做过滤，绝不因为读失败把整张菜单清空。
/// </summary>
internal static class CookwareFilter
{
    private const string Tag = "[GuestTag]";

    /// <summary>扫描间隔：厨具是常驻对象，2 秒一次足够，别每帧 FindObjectsOfType。</summary>
    private const float ScanIntervalSeconds = 30f;      // 30 秒扫一次就够（厨具是常驻对象，别拿 FindObjectsOfType 当轮询）

    private static readonly HashSet<Cooker.CookerType> Present = new();
    private static HashSet<Cooker.CookerType>? _overrideTypes;
    private static bool _overrideParsed;
    private static float _lastScanTime = -999f;
    private static bool _logged;
    private static bool _rawLogged;  // 营业配置原始值是否已打印（m08354 诊断用）

    /// <summary>台上至少有一种厨具时才启用过滤；否则视为「读不到」，一律放行。</summary>
    internal static bool HasAnyCooker
    {
        get
        {
            EnsureScanned();
            return Present.Count > 0;
        }
    }

    /// <summary>这道菜需要的厨具种类，台上有没有。</summary>
    internal static bool IsAvailable(Cooker.CookerType type)
    {
        try
        {
            EnsureScanned();
            if (Present.Count == 0) return true; // 读不到就不拦
            return type == Cooker.CookerType.Empty || Present.Contains(type);
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"{Tag} 厨具判定失败（放行）：{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 这道菜要求的厨具，今晚厨房里有没有（m08215）。
    /// 规则：
    ///   - 菜谱没写厨具（空列表）→ 放行；
    ///   - 台上一个厨具都没扫到（读不到）→ 放行，绝不因为读失败把整张菜单清空；
    ///   - 只要菜要求的任意一种厨具在场 → 放行；
    ///   - 一种都没有 → 拦截。
    /// </summary>
    internal static bool IsAnyAvailable(int[]? types)
    {
        try
        {
            if (types == null || types.Length == 0) return true; // 菜谱没写厨具：不拦
            EnsureScanned();                                     // 确保扫过厨具
            if (Present.Count == 0) return true;                 // 读不到：不拦

            foreach (var id in types)                            // 逐个需要的厨具
            {
                if (id <= 0) continue;                           // Empty / 非法 id：跳过
                if (Present.Contains((Cooker.CookerType)id)) return true; // 在场：这道菜能做
            }

            return false;                                        // 一种都不在场：拦
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"{Tag} 厨具判定失败（放行）：{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>台上是否至少有一种厨具（读得到厨具时过滤才生效）。</summary>
    internal static bool FilterActive
    {
        get
        {
            EnsureScanned();
            return Present.Count > 0;
        }
    }

    /// <summary>给 UI/日志用的可读描述，例如「煮锅、烧烤架」。</summary>
    internal static string Describe()
    {
        EnsureScanned();
        if (Present.Count == 0) return "读不到（本次不按厨具过滤）";

        var parts = new List<string>();
        foreach (var type in Present) parts.Add(DisplayName(type));
        parts.Sort(StringComparer.Ordinal);
        return string.Join("、", parts);
    }

    /// <summary>重新扫描（例如进出营业、换厨具后想立刻生效）。</summary>
    internal static void Invalidate() => _lastScanTime = -999f;

    private static void EnsureScanned()
    {
        if (_overrideParsed && _overrideTypes != null) return; // 玩家手填优先，不用再扫
        if (!PhaseGate.IsNightWorkPhaseFast()) return;         // 白天（大地图）绝不扫描（m07921：FindObjectsOfType 很贵）

        var now = Time.unscaledTime;
        if (now - _lastScanTime < ScanIntervalSeconds) return;
        _lastScanTime = now;

        try
        {
            Present.Clear();

            // 第一优先：今晚营业配置里的厨具（IzakayaConfigure.CookerConfigure）。
            // 这是玩家「真的摆上台」的那几台，比场景里 FindObjectsOfType 更准
            // （场景里可能还留着没带出来的厨具对象，用户 m08280 遇到的「只带 2 台却扫出 5 种」就是这个原因）。
            if (TryScanFromConfigure())
            {
                FinishScan();
                return;
            }

            // 第二优先：场景里活着的 CookController（营业配置读不到时的兜底）
            var controllers = UnityEngine.Object.FindObjectsOfType<CookController>();
            if (controllers != null)
            {
                foreach (var controller in controllers)
                {
                    try
                    {
                        if (controller == null || controller.Pointer == IntPtr.Zero) continue;
                        var cooker = controller.Cooker;
                        if (cooker == null || cooker.Pointer == IntPtr.Zero) continue;

                        var type = cooker.Type;
                        if (type == Cooker.CookerType.Empty) continue;
                        Present.Add(type);
                    }
                    catch
                    {
                        // 单台厨具读失败无所谓，继续扫下一台
                    }
                }
            }

            // 游戏里一台都没扫到，再看玩家有没有在 cfg 里手填 [Cookware] Override
            if (Present.Count == 0)
            {
                var overrideTypes = ParseOverride();
                if (overrideTypes != null)
                {
                    foreach (var type in overrideTypes) Present.Add(type);
                }
            }

            FinishScan();
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"{Tag} 扫描厨具失败（本次不按厨具过滤）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>扫描收尾：只打一次「今晚有哪些厨具」的日志。</summary>
    private static void FinishScan()
    {
        if (_logged) return;                                              // 只打一次
        _logged = true;                                                    // 标记已打
        Plugin.Logger.LogInfo($"{Tag} 今晚厨具：{Describe()}");             // 例如「今晚厨具：煮锅、烧烤架」
    }

    /// <summary>
    /// 从营业配置读取今晚的厨具（m08280）。
    /// 返回 true 表示读到了（已经写进 Present），false 表示读不到、让调用方退回场景扫描。
    /// 值的含义：优先按 CookerType(1..5) 解释；不在 1..5 的，尝试 DataBaseCore.Cookers[id].Type 映射。
    /// </summary>
    private static bool TryScanFromConfigure()
    {
        try
        {
            var configure = GameData.RunTime.NightSceneUtility.IzakayaConfigure.Instance; // 营业配置单例
            if (configure == null || configure.Pointer == IntPtr.Zero) return false;      // 还没进营业：读不到

            var raw = configure.CookerConfigure;                                          // 今晚厨具（int[]）
            if (raw == null || raw.Length == 0) return false;                             // 空：交给场景扫描

            var rawText = new List<string>();                                             // 原始值，写日志用
            var added = 0;                                                                // 成功识别的数量

            foreach (var value in raw)                                                    // 逐个厨具
            {
                rawText.Add(value.ToString());                                            // 记录原值

                if (value > 0 && value <= (int)Cooker.CookerType.CuttingBoard)            // 1..5 直接当 CookerType
                {
                    Present.Add((Cooker.CookerType)value);                                // 记下这台厨具
                    added++;                                                              // 计数
                    continue;                                                             // 下一个
                }

                // 大于 5：当作数据库 id，查 DataBaseCore.Cookers 拿到 Cooker 再取 Type
                try
                {
                    var cookers = DataBaseCore.Cookers;                                   // 厨具数据库
                    if (cookers != null && cookers.TryGetValue(value, out var cooker) && cooker != null)
                    {
                        var type = cooker.Type;                                           // 厨具种类
                        if (type != Cooker.CookerType.Empty)                              // 有效种类
                        {
                            Present.Add(type);                                            // 记下这台厨具
                            added++;                                                      // 计数
                        }
                    }
                }
                catch
                {
                    // 单台映射失败不影响其它厨具
                }
            }

            if (!_rawLogged)                                              // 原始值只打一次
            {
                _rawLogged = true;                                        // 标记已打
                Plugin.Logger.LogInfo($"{Tag} 厨具（营业配置原始值）: [{string.Join(",", rawText)}] → 识别为 {Describe()}"); // 关键诊断行
            }

            return added > 0;                                                             // 识别到至少一台才算成功
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"{Tag} 读营业配置厨具失败（退回场景扫描）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>解析 cfg 里的 `[Cookware] Override`，例如 `煮锅 = 2; 油锅 = 1`（数字仅作备注，按种类启用）。</summary>
    private static HashSet<Cooker.CookerType>? ParseOverride()
    {
        try
        {
            if (!GuestTagConfig.UseCookwareOverride.Value) return null;

            var raw = GuestTagConfig.CookwareOverride.Value;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var result = new HashSet<Cooker.CookerType>();
            foreach (var chunk in raw!.Split(new[] { ';', '|', '\n', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = chunk;
                var eq = token.IndexOf('=');
                if (eq < 0) eq = token.IndexOf('：');
                if (eq < 0) eq = token.IndexOf(':');
                if (eq >= 0) token = token.Substring(0, eq);

                var type = ParseType(token);
                if (type != Cooker.CookerType.Empty) result.Add(type);
            }

            _overrideParsed = true;
            _overrideTypes = result.Count > 0 ? result : null;
            if (result.Count > 0)
            {
                Plugin.Logger.LogInfo($"{Tag} 使用 cfg 手填厨具：{DescribeSet(result)}");
            }

            return _overrideTypes;
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"{Tag} 解析 [Cookware] Override 失败：{ex.Message}");
            return null;
        }
    }

    private static string DescribeSet(HashSet<Cooker.CookerType> set)
    {
        var parts = new List<string>();
        foreach (var type in set) parts.Add(DisplayName(type));
        parts.Sort(StringComparer.Ordinal);
        return string.Join("、", parts);
    }

    private static Cooker.CookerType ParseType(string token)
    {
        token = token.Trim();
        if (token.Length == 0) return Cooker.CookerType.Empty;

        if (token.IndexOfAny(new[] { '锅', '煮' }) >= 0 && token.IndexOf('油') < 0) return Cooker.CookerType.Pot;
        if (token.IndexOf('烤') >= 0) return Cooker.CookerType.Grill;
        if (token.IndexOf('油') >= 0 || token.IndexOf('炸') >= 0) return Cooker.CookerType.Fryer;
        if (token.IndexOf('蒸') >= 0) return Cooker.CookerType.Steamer;
        // 「砧板」「案板」「料理台」「切××」都算料理台（CuttingBoard）
        if (token.IndexOf('砧') >= 0 || token.IndexOf('板') >= 0 || token.IndexOf("料理") >= 0 || token.IndexOf('切') >= 0)
        {
            return Cooker.CookerType.CuttingBoard;
        }

        if (Enum.TryParse<Cooker.CookerType>(token, true, out var parsed)) return parsed;
        return Cooker.CookerType.Empty;
    }

    internal static string DisplayName(Cooker.CookerType type)
    {
        switch (type)
        {
            case Cooker.CookerType.Pot: return "煮锅";
            case Cooker.CookerType.Grill: return "烧烤架";
            case Cooker.CookerType.Fryer: return "油锅";
            case Cooker.CookerType.Steamer: return "蒸锅";
            case Cooker.CookerType.CuttingBoard: return "料理台";
            default: return "空";
        }
    }
}
