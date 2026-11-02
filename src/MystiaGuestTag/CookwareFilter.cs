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
    private const float ScanIntervalSeconds = 2f;

    private static readonly HashSet<Cooker.CookerType> Present = new();
    private static HashSet<Cooker.CookerType>? _overrideTypes;
    private static bool _overrideParsed;
    private static float _lastScanTime = -999f;
    private static bool _logged;

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
            Plugin.Logger.LogDebug($"{Tag} 厨具判定失败（放行）：{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    /// <summary>给 UI/日志用的可读描述，例如「煮锅、烤架」。</summary>
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
        var now = Time.unscaledTime;
        if (_overrideParsed && _overrideTypes != null) return; // 玩家手填优先，不用再扫
        if (now - _lastScanTime < ScanIntervalSeconds) return;
        _lastScanTime = now;

        try
        {
            Present.Clear();

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

            if (!_logged)
            {
                _logged = true;
                Plugin.Logger.LogInfo($"{Tag} 厨具：{Describe()}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug($"{Tag} 扫描厨具失败（本次不按厨具过滤）：{ex.GetType().Name}: {ex.Message}");
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
            Plugin.Logger.LogDebug($"{Tag} 解析 [Cookware] Override 失败：{ex.Message}");
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
        if (token.IndexOf('砧') >= 0 || token.IndexOf('板') >= 0) return Cooker.CookerType.CuttingBoard;

        if (Enum.TryParse<Cooker.CookerType>(token, true, out var parsed)) return parsed;
        return Cooker.CookerType.Empty;
    }

    internal static string DisplayName(Cooker.CookerType type)
    {
        switch (type)
        {
            case Cooker.CookerType.Pot: return "煮锅";
            case Cooker.CookerType.Grill: return "烤架";
            case Cooker.CookerType.Fryer: return "油锅";
            case Cooker.CookerType.Steamer: return "蒸笼";
            case Cooker.CookerType.CuttingBoard: return "砧板";
            default: return "空";
        }
    }
}
