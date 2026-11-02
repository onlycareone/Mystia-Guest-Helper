using System;
using System.Collections.Generic;

namespace MystiaGuestTag;

/// <summary>
/// 硬编码的稀客喜好表（按用户要求手写，不依赖游戏内数据）。
/// 键为 SpecialGuest.stringId（内部 label，已由 MystiaAI / MetaMystia 实测确认拼写）；
/// 比较时统一小写并去掉非字母数字字符，避免 Akyuu/Akyu 这类拼写差异漏识别。
///
/// 酒水标签用的是游戏内真实标签名（2026-11-01 对照 BWIKI 酒水一览的 Tag 过滤器）：
///   无酒精 / 低酒精 / 中酒精 / 高酒精 / 可加冰 / 可加热 / 烧酒 / 清酒 / 鸡尾酒 /
///   西洋酒 / 利口酒 / 啤酒 / 直饮 / 水果 / 甘 / 辛 / 苦 / 气泡 / 古典 / 现代 / 提神
///
/// 注意：这三个人的酒水标签是**按人设推的**（下表注释写清依据），不是从游戏数据里读的。
/// 运行时只要 SpecialGuest.LikeBevTag 读得出来，就以游戏数据为准（RecommendBeverages 里
/// 实时标签优先，硬编码只做兜底），所以推错也不会显示错。
/// </summary>
internal static class GuestTagDatabase
{
    internal readonly struct Entry
    {
        public Entry(string name, string[] tags, string[] bevTags)
        {
            Name = name;
            Tags = tags;
            BeverageTags = bevTags;
        }

        /// <summary>中文显示名。</summary>
        public string Name { get; }

        /// <summary>喜好食物标签（中文，硬编码）。</summary>
        public string[] Tags { get; }

        /// <summary>喜好酒水标签（中文，硬编码，按人设推断；运行时数据优先）。</summary>
        public string[] BeverageTags { get; }
    }

    private static readonly string[] AkyuuTags = { "汤羹", "清淡", "甜", "和风", "文化底蕴" };

    private static readonly Dictionary<string, Entry> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // 莉格露·奈特巴格：萤火虫妖怪，孩子气、爱吃甜的 → 甘口、果味、低度
        ["Wriggle"] = new Entry(
            "莉格露",
            new[] { "甜", "猎奇", "生", "肉" },
            new[] { "甘", "水果", "低酒精", "气泡" }),

        // 稗田阿求：人类史学家，不能喝酒，喜欢和风/古典 → 无酒精的茶与古典风味
        // 游戏内 stringId 实测为 Akyuu（MetaMystia 注释 (3)Akyuu）；同时收录 Akyu 兼容写法
        ["Akyuu"] = new Entry(
            "阿求",
            AkyuuTags,
            new[] { "无酒精", "甘", "古典", "可加热" }),
        ["Akyu"] = new Entry(
            "阿求",
            AkyuuTags,
            new[] { "无酒精", "甘", "古典", "可加热" }),

        // 露米娅：吃人的暗妖，口味重 → 烈酒、辛口
        ["Rumia"] = new Entry(
            "露米娅",
            new[] { "猎奇", "肉", "招牌", "生", "饱腹" },
            new[] { "高酒精", "辛", "烧酒", "直饮" }),
    };

    // TODO: 确认 Hook 点/字段：若某些稀客的 stringId 与上表不一致，看 BepInEx\\LogOutput.log 里
    //       "[GuestTag] 发现未收录稀客" 一行打印的 stringId / Id，把条目补进 Table 即可。
    //       另外 SpecialGuest.Id（int）也可作为备用键，但当前版本未取得三个稀客的数值 ID，暂不启用。

    /// <summary>按 stringId 查表；命中返回 true。</summary>
    internal static bool TryGet(string? stringId, out Entry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(stringId)) return false;

        var raw = stringId!.Trim();
        if (Table.TryGetValue(raw, out entry)) return true;

        var normalized = Normalize(raw);
        if (normalized.Length == 0) return false;

        foreach (var pair in Table)
        {
            if (string.Equals(Normalize(pair.Key), normalized, StringComparison.Ordinal))
            {
                entry = pair.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>统一成小写字母数字，去掉下划线/空格/连字符等装饰。</summary>
    private static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer, 0, length);
    }
}
