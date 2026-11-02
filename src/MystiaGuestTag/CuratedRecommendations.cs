using System;
using System.Collections.Generic;

namespace MystiaGuestTag;

/// <summary>
/// 「查表法」推荐名单（m03044 / m03065）：全部硬编码在 C# 里，不读任何外部文件、不做 IO。
/// 表里没有的稀客、或者当前策略下那一档是空的，都会由调用方退回动态计算。
/// 玩家想改名单，直接改这里的数组再重新编译即可（刻意不用 JSON，避免运行期解析出错把游戏搞崩）。
/// </summary>
internal static class CuratedRecommendations
{
    private sealed class Entry
    {
        internal string[] HighFood = Array.Empty<string>();
        internal string[] LowFood = Array.Empty<string>();
        internal string[] FewFood = Array.Empty<string>();
        internal string[] HighDrinks = Array.Empty<string>();
        internal string[] LowDrinks = Array.Empty<string>();
    }

    private static readonly Dictionary<string, Entry> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Marisa"] = new Entry
            {
                HighFood = new[] { "大奢宴", "豚骨拉面" },
                LowFood = new[] { "豆腐味噌", "土豆可乐饼" },
                FewFood = new[] { "豆腐味噌", "土豆可乐饼" },
                HighDrinks = new[] { "天狗踊", "水獭祭" },
                LowDrinks = new[] { "梅酒", "猩红恶魔" },
            },
            ["Wriggle"] = new Entry
            {
                HighFood = new[] { "大奢宴", "幻昙花糕" },
                LowFood = new[] { "力量汤", "豆腐味噌" },
                FewFood = new[] { "力量汤", "豆腐味噌" },
                HighDrinks = new[] { "水獭祭", "天狗踊" },
                LowDrinks = new[] { "梅酒", "果味High Ball" },
            },
            ["Rumia"] = new Entry
            {
                HighFood = new[] { "大奢宴", "豚骨拉面" },
                LowFood = new[] { "炙猪肉饭团", "炸八目鳗" },
                FewFood = new[] { "炙猪肉饭团", "力量汤" },
                HighDrinks = new[] { "水獭祭", "天狗踊" },
                LowDrinks = new[] { "梅酒", "猩红恶魔" },
            },
            ["Akyuu"] = new Entry
            {
                HighFood = new[] { "幻昙花糕", "大奢宴" },
                LowFood = new[] { "冷豆腐", "豆腐味噌" },
                FewFood = new[] { "冷豆腐", "豆腐味噌" },
                HighDrinks = new[] { "水獭祭", "梅酒" },
                LowDrinks = new[] { "红魔馆红茶", "梅酒" },
            },
            ["Chen"] = new Entry
            {
                HighFood = new[] { "豚骨拉面", "大奢宴" },
                LowFood = new[] { "炙猪肉饭团", "土豆可乐饼" },
                FewFood = new[] { "炙猪肉饭团", "土豆可乐饼" },
                HighDrinks = new[] { "天狗踊", "水獭祭" },
                LowDrinks = new[] { "梅酒", "果味SOUR" },
            },
        };

    /// <summary>旧版本用过的别名，一起指到同一份数据。</summary>
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Akyu"] = "Akyuu",
        };

    /// <summary>保留这个入口方便以后扩展；当前版本不读文件，等于空操作。</summary>
    internal static void Initialize()
    {
    }

    /// <summary>按策略取菜品名单；返回空数组表示这一档没配（调用方退回动态计算）。</summary>
    internal static string[] LookupFood(string stringId, RecommendStrategy strategy)
    {
        if (!TryGet(stringId, out var entry)) return Array.Empty<string>();

        return strategy switch
        {
            RecommendStrategy.PriceHigh => entry.HighFood,
            RecommendStrategy.PriceLow => entry.LowFood,
            RecommendStrategy.FewIngredients => entry.FewFood,
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>按策略取酒水名单；少料易做对酒水没意义，按低价那一档取。</summary>
    internal static string[] LookupDrinks(string stringId, RecommendStrategy strategy)
    {
        if (!TryGet(stringId, out var entry)) return Array.Empty<string>();

        return strategy == RecommendStrategy.PriceHigh ? entry.HighDrinks : entry.LowDrinks;
    }

    private static bool TryGet(string stringId, out Entry entry)
    {
        var id = (stringId ?? string.Empty).Trim();
        if (id.Length == 0)
        {
            entry = null!;
            return false;
        }

        if (Aliases.TryGetValue(id, out var alias)) id = alias;
        return Table.TryGetValue(id, out entry!);
    }
}
