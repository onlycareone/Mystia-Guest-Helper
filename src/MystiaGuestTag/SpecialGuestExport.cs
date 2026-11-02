using System;
using System.IO;
using System.Linq;
using System.Text;
using GameData.Core.Collections.CharacterUtility;
using GameData.Core.Collections.NightSceneUtility;
using GameData.CoreLanguage.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MystiaGuestTag;

/// <summary>
/// 把游戏里的全量稀客权重数据导出成 JSON，方便做外部工具 / 发帖附数据。
///
/// 数据来源（全部来自游戏运行时对象，没有任何硬编码）：
///   DataBaseCharacter.GetAllSpecialGuests()      → 全部稀客（含 DLC，已展开映射）
///   SpecialGuest.StringId / Id                   → 内部名 / 数字 id
///   SpecialGuest.Text.Name                       → 当前语言下的名字
///   SpecialGuest.likeFoodTag / likeBevTag         → Il2CppReferenceArray&lt;WeightedTag&gt;（tagId + weight）
///   SpecialGuest.HateFoodTagOriginal             → 厌恶的食物标签 id 数组
///   SpecialGuest.fundRange / enduranceLimit      → 预算区间 / 耐心，顺带导出
///   DataBaseLanguage.GetFoodTag / GetBeverageTag → 标签短名
///
/// 触发点：Harmony postfix 挂在 DataBaseCharacter.PreinitSpecialGuests()（游戏加载稀客数据后立刻导出），
/// 另外在插件 Load 与营业期 OnGUI 里各兜一次，保证文件一定会生成。
/// </summary>
internal static class SpecialGuestExport
{
    private const string FileName = "cc.mystia.guesttag.specialguests.json";
    private const string HarmonyTag = "[GuestTag]";
    private const char NL = (char)10;
    private const char QUOTE = (char)34;
    private const char BACKSLASH = (char)92;

    private static bool _done;
    private static bool _initAttempted;

    internal static void Install(Harmony harmony)
    {
        // 多个触发点：稀客数据可能在「预初始化」「进白天场景」「刷新地图 NPC」「进营业」任一时刻才就绪，
        // 每个点上 TryExportOnce 都是幂等的，谁先跑到就谁导出。
        Patch(harmony, typeof(DataBaseCharacter), nameof(DataBaseCharacter.PreinitSpecialGuests));
        Patch(harmony, AccessTools.TypeByName("DayScene.SceneManager"), "OnFirstEnterDaySceneFinish");
        Patch(harmony, AccessTools.TypeByName("DayScene.SceneManager"), "RefreshCurrentMapNPCs");
        Patch(harmony, AccessTools.TypeByName("GameData.Core.Collections.CharacterUtility.DataBaseCharacter"), "PreinitSpecialGuests");
    }

    private static void Patch(Harmony harmony, Type? type, string methodName)
    {
        if (type == null)
        {
            return;
        }

        try
        {
            var target = AccessTools.Method(type, methodName);
            if (target == null)
            {
                return;
            }

            if (Harmony.GetPatchInfo(target)?.Postfixes?.Any(p => p.owner == Plugin.PluginGuid) == true)
            {
                return; // 同一次启动里别重复挂（上面两个等价入口指向同一个方法时会走到这里）
            }

            harmony.Patch(target, postfix: new HarmonyMethod(typeof(SpecialGuestExport), nameof(OnDataLoaded)));
            Plugin.Logger.LogInfo($"{HarmonyTag} 已挂载稀客权重导出点: {type.Name}.{methodName}()");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug($"{HarmonyTag} 挂载导出点 {type.Name}.{methodName}() 失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnDataLoaded()
    {
        TryExportOnce();
    }

    /// <summary>导出一次（幂等）。数据还没加载好时返回 false，调用方可以下次再试。</summary>
    internal static bool TryExportOnce()
    {
        if (_done)
        {
            return true;
        }

        try
        {
            Il2CppReferenceArray<SpecialGuest> guests;
            try
            {
                guests = DataBaseCharacter.GetAllSpecialGuests();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug($"{HarmonyTag} 稀客数据尚未就绪：{ex.GetType().Name}");
                return false;
            }

            if (guests == null || guests.Length == 0)
            {
                // 白天场景里数据表可能还没被游戏预初始化过：主动调一次游戏的初始化函数再读。
                if (!_initAttempted)
                {
                    _initAttempted = true;
                    try
                    {
                        DataBaseCharacter.PreinitSpecialGuests();
                        Plugin.Logger.LogInfo($"{HarmonyTag} 主动调用了 DataBaseCharacter.PreinitSpecialGuests()");
                    }
                    catch (Exception initEx)
                    {
                        Plugin.Logger.LogDebug($"{HarmonyTag} 主动初始化稀客数据失败（已忽略）：{initEx.GetType().Name}: {initEx.Message}");
                    }

                    try
                    {
                        guests = DataBaseCharacter.GetAllSpecialGuests();
                    }
                    catch (Exception retryEx)
                    {
                        Plugin.Logger.LogDebug($"{HarmonyTag} 初始化后重读稀客数据失败（已忽略）：{retryEx.GetType().Name}");
                        return false;
                    }
                }

                if (guests == null || guests.Length == 0)
                {
                    return false;
                }
            }

            var json = BuildJson(guests);
            var path = Path.Combine(BepInEx.Paths.ConfigPath, FileName);
            Directory.CreateDirectory(BepInEx.Paths.ConfigPath);
            File.WriteAllText(path, json, new UTF8Encoding(false));

            _done = true;
            Plugin.Logger.LogInfo($"{HarmonyTag} 已导出稀客权重数据：{path}（{guests.Length} 位稀客）");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 导出稀客权重数据失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
            _done = true; // 失败也只尝试一次，别每帧重试
            return false;
        }
    }

    private static string BuildJson(Il2CppReferenceArray<SpecialGuest> guests)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append('{').Append(NL);
        sb.Append("  ").Append(Quote("source")).Append(": ").Append(Quote("Touhou Mystia Izakaya 4.4.0e / Mystia Guest Tag " + Plugin.PluginVersion)).Append(',').Append(NL);
        sb.Append("  ").Append(Quote("generatedAt")).Append(": ").Append(Quote(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',').Append(NL);
        sb.Append("  ").Append(Quote("count")).Append(": ").Append(guests.Length).Append(',').Append(NL);
        sb.Append("  ").Append(Quote("guests")).Append(": [").Append(NL);

        var first = true;
        foreach (var guest in guests)
        {
            if (guest == null)
            {
                continue;
            }

            if (!first)
            {
                sb.Append(',').Append(NL);
            }

            first = false;
            WriteGuest(sb, guest);
        }

        sb.Append(NL).Append("  ]").Append(NL).Append('}').Append(NL);
        return sb.ToString();
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<int, string>? _namesById;
    private static bool _namesTried;

    private static string ReadLocalizedName(SpecialGuest guest)
    {
        var fallback = SafeString(() => guest.Text != null ? guest.Text.Name : string.Empty);
        try
        {
            if (!_namesTried)
            {
                _namesTried = true;
                _namesById = GameData.CoreLanguage.Collections.DataBaseLanguage.GetAllSpecialGuestsNames();
            }

            if (_namesById != null && _namesById.TryGetValue(guest.Id, out var localized) &&
                !string.IsNullOrWhiteSpace(localized))
            {
                return localized;
            }
        }
        catch (Exception)
        {
            // 读不到本地化名就退回 Text.Name
        }

        return fallback;
    }

    private static void WriteGuest(StringBuilder sb, SpecialGuest guest)
    {
        var stringId = SafeString(() => guest.StringId);
        var id = SafeInt(() => guest.Id);
        var name = ReadLocalizedName(guest);
        if (string.IsNullOrEmpty(name))
        {
            name = stringId;
        }

        int fundLow;
        int fundHigh;
        SafeFundRange(guest, out fundLow, out fundHigh);

        sb.Append("    {").Append(NL);
        sb.Append("      ").Append(Quote("id")).Append(": ").Append(id).Append(',').Append(NL);
        sb.Append("      ").Append(Quote("stringId")).Append(": ").Append(Quote(stringId)).Append(',').Append(NL);
        sb.Append("      ").Append(Quote("name")).Append(": ").Append(Quote(name)).Append(',').Append(NL);
        sb.Append("      ").Append(Quote("fundRange")).Append(": [").Append(fundLow).Append(", ").Append(fundHigh).Append("],").Append(NL);
        sb.Append("      ").Append(Quote("enduranceLimit")).Append(": ").Append(SafeFloat(() => guest.enduranceLimit)).Append(',').Append(NL);

        sb.Append("      ").Append(Quote("likeFood")).Append(": ");
        WriteWeightedTags(sb, SafeWeightedArray(() => guest.likeFoodTag), beverage: false);
        sb.Append(',').Append(NL);

        sb.Append("      ").Append(Quote("likeBev")).Append(": ");
        WriteWeightedTags(sb, SafeWeightedArray(() => guest.likeBevTag), beverage: true);
        sb.Append(',').Append(NL);

        sb.Append("      ").Append(Quote("hateFood")).Append(": ");
        WriteTagIds(sb, SafeTagIds(() => guest.HateFoodTagOriginal), beverage: false);
        sb.Append(NL);

        sb.Append("    }");
    }

    private static void WriteWeightedTags(StringBuilder sb, Il2CppReferenceArray<SpecialGuest.WeightedTag>? tags, bool beverage)
    {
        if (tags == null || tags.Length == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        var first = true;
        foreach (var tag in tags)
        {
            if (tag == null)
            {
                continue;
            }

            int tagId;
            int weight;
            try
            {
                tagId = tag.tagId;
                weight = tag.weight;
            }
            catch
            {
                continue;
            }

            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append("{ ").Append(Quote("tagId")).Append(": ").Append(tagId)
              .Append(", ").Append(Quote("tag")).Append(": ").Append(Quote(TagName(tagId, beverage)))
              .Append(", ").Append(Quote("weight")).Append(": ").Append(weight).Append(" }");
        }

        sb.Append(']');
    }

    private static void WriteTagIds(StringBuilder sb, Il2CppStructArray<int>? ids, bool beverage)
    {
        if (ids == null || ids.Length == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        var first = true;
        foreach (var tagId in ids)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append(Quote(TagName(tagId, beverage)));
        }

        sb.Append(']');
    }

    private static string TagName(int tagId, bool beverage)
    {
        try
        {
            var text = beverage ? DataBaseLanguage.GetBeverageTag(tagId) : DataBaseLanguage.GetFoodTag(tagId);
            return string.IsNullOrEmpty(text) ? "#" + tagId : text;
        }
        catch
        {
            return "#" + tagId;
        }
    }

    private static string SafeString(Func<string> get)
    {
        try
        {
            return get() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int SafeInt(Func<int> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeFloat(Func<float> get)
    {
        try
        {
            return get().ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return "0";
        }
    }

    private static void SafeFundRange(SpecialGuest guest, out int low, out int high)
    {
        try
        {
            var range = guest.fundRange;
            low = range.x;
            high = range.y;
        }
        catch
        {
            low = 0;
            high = 0;
        }
    }

    private static Il2CppReferenceArray<SpecialGuest.WeightedTag>? SafeWeightedArray(Func<Il2CppReferenceArray<SpecialGuest.WeightedTag>> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return null;
        }
    }

    private static Il2CppStructArray<int>? SafeTagIds(Func<Il2CppStructArray<int>> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return null;
        }
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return emptyQuoted;
        }

        var sb = new StringBuilder(value.Length + 8);
        sb.Append(QUOTE);
        foreach (var ch in value!)
        {
            if (ch == QUOTE)
            {
                sb.Append(BACKSLASH).Append(QUOTE);
            }
            else if (ch == BACKSLASH)
            {
                sb.Append(BACKSLASH).Append(BACKSLASH);
            }
            else if (ch == NL)
            {
                sb.Append(ch);
            }
            else if (ch == (char)13)
            {
                sb.Append(ch);
            }
            else if (ch < (char)0x20)
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        sb.Append(QUOTE);
        return sb.ToString();
    }

    private static readonly string emptyQuoted = string.Concat(QUOTE, QUOTE);
}
