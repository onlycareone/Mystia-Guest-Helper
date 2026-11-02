using System;
using System.Collections.Generic;
using System.Linq;
using GameData.Core.Collections;
using GameData.Core.Collections.NightSceneUtility;
using GameData.RunTime.NightSceneUtility;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MystiaGuestTag;

/// <summary>
/// 按稀客喜好标签给「推荐菜品」打分（只读，不改游戏状态）。
///
/// 候选来源（优先级从高到低）：
///   1) 当晚玩家自己选的菜单：IzakayaConfigure.Instance.DailyRecipes（List&lt;Recipe&gt;）
///   2) 兜底：全菜谱表 DataBaseCore.Recipes（静态属性，interop 已核实 static=True）
///
/// 打分：菜的 Tags（int[] 标签 id）与稀客 LikeFoodTag 交集 ×2，与 HateFoodTag 交集 ×3 扣分；
/// 只保留正分，按分数降序取前 3。菜名取 Sellable.Text.Name（LanguageBase.Name，当前语言，
/// 与 MetaMystia 的 recipe.Food.Text.Name 用法一致）。
///
/// 任何一步失败都返回 null（面板退化成不显示推荐），不影响稀客识别本身。
/// </summary>
internal static class RecipeRecommender
{
    private const string HarmonyTag = "[GuestTag]";

    // 菜品/酒水各列几个：由 cfg 的 Display.MaxRecipesPerGuest / MaxDrinksPerGuest 决定，
    // 每次取值都读配置，玩家改完重开游戏就生效（代码里不再写死）。

    /// <summary>推荐结果缓存时长：换菜单/换日会变，但不值得每帧重算。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>没匹配到东西时缓存短一点，菜单/客人状态一变就尽快重算（避免「只有第一个有推荐」）。</summary>
    private static readonly TimeSpan EmptyCacheTtl = TimeSpan.FromSeconds(6);

    private sealed class CacheEntry
    {
        public CacheEntry(RecommendationResult result)
        {
            Result = result;
            CreatedUtc = DateTime.UtcNow;
        }

        public RecommendationResult Result { get; }
        public DateTime CreatedUtc { get; }
    }

    /// <summary>
    /// 推荐结果：一条按当前策略（低价优先 / 高价优先 / 少料易做）排好的列表。
    /// <see cref="HasPreference"/>=false 表示这位稀客对该类食物/酒水**没有任何喜好标签**，
    /// 和「有喜好但没匹配上」（HasPreference=true、Items 为空）是两回事。
    /// </summary>
    internal readonly struct RecommendationResult
    {
        private RecommendationResult(string[] items, bool hasPreference, string label)
        {
            Items = items ?? Array.Empty<string>();
            HasPreference = hasPreference;
            Label = label ?? string.Empty;
        }

        public static RecommendationResult NoPreference => new(Array.Empty<string>(), false, string.Empty);

        public static RecommendationResult NoMatch => new(Array.Empty<string>(), true, string.Empty);

        public static RecommendationResult With(string[] items, string label) => new(items, true, label);

        /// <summary>排好序、已经带售价的显示文本，如「二天一流 (¥90)」。</summary>
        public string[] Items { get; }

        /// <summary>这一行的前缀，随策略变，如「低价优先推荐」「高价优先酒水」「少料易做推荐」。</summary>
        public string Label { get; }

        public bool HasPreference { get; }

        public bool HasMatch => Items.Length > 0;

        /// <summary>兼容旧日志：把 Items 原样给出去。</summary>
        public string[] Names => Items;

        /// <summary>兼容旧日志里的「高价 / 低价」视图：策略化之后不再拆分，统一放到 High。</summary>
        public string[] High => Items;

        public string[] Low => Array.Empty<string>();

        public bool Merged => false;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();
    private static bool _dailyMenuFailureLogged;
    private static bool _allRecipesFailureLogged;
    private static bool _beverageMenuFailureLogged;
    private static bool _allBeveragesFailureLogged;

    // 「有喜好但没匹配上」只记一次日志，免得每 6 秒刷一行
    private static readonly HashSet<string> _foodNoMatchLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _bevNoMatchLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _noMatchGate = new();

    internal static void ClearCache()
    {
        lock (CacheGate)
        {
            Cache.Clear();
        }
    }

    /// <summary>推荐菜品：先按喜好标签过滤，再按真实售价拆成「高价前 N / 低价前 N」两组。</summary>
    internal static RecommendationResult Recommend(SpecialGuest guest, string stringId, int budget)
    {
        // 预算参与缓存 key：同一位稀客预算变了（加菜/喝汤），推荐必须重算
        // 策略也参与缓存 key：面板上切了策略必须立刻重算，不能吃旧结果
        var key = (stringId ?? string.Empty) + "@" + budget + "#" + GuestTagConfig.GetStrategy();
        if (TryGetCached(key, out var cached)) return cached;

        var result = ComputeWithCuratedTable(guest, stringId ?? string.Empty, key, budget, beverage: false);
        StoreCache(key, result);
        return result;
    }

    /// <summary>
    /// 查表优先（m03044）：名单里这一档有菜就直接按名单出，名单为空或全被预算挡掉才退回动态计算。
    /// </summary>
    private static RecommendationResult ComputeWithCuratedTable(
        SpecialGuest guest,
        string stringId,
        string key,
        int budget,
        bool beverage)
    {
        try
        {
            return ComputeWithCuratedTableCore(guest, stringId, key, budget, beverage);
        }
        catch (Exception ex)
        {
            // 兜底：推荐算挂了也只是不显示，绝对不允许把异常抛回游戏
            Plugin.Logger.LogWarning($"{HarmonyTag} 推荐计算失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
            return RecommendationResult.NoMatch;
        }
    }

    private static RecommendationResult ComputeWithCuratedTableCore(
        SpecialGuest guest,
        string stringId,
        string key,
        int budget,
        bool beverage)
    {
        var strategy = GuestTagConfig.GetStrategy();
        var names = beverage
            ? CuratedRecommendations.LookupDrinks(stringId, strategy)
            : CuratedRecommendations.LookupFood(stringId, strategy);

        // m03399：高价优先必须给「预算内最贵」的菜，名单只有两三条根本做不到，
        // 所以这一档直接交给动态排序（候选池已经是「现在做得出」的菜）。
        // m03688：查表名单只是「可选提示」，默认关闭 —— 默认走纯标签/预算/食材/厨具的动态过滤，
        // 这样以后游戏出任何新 DLC 稀客都不需要改代码。
        if (GuestTagConfig.UseCuratedHints.Value && strategy != RecommendStrategy.PriceHigh && names.Length > 0)
        {
            var allowed = beverage
                ? CollectStockedBeverageNames()
                : CollectAvailableFoodNames();

            var curated = ResolveCurated(names, strategy, budget, beverage, allowed);
            if (curated.HasMatch)
            {
                // 名单只有两三条：剩下的位置用动态结果补满，别让「最多 3 条」变成只有 1 条
                var dynamicResult = beverage
                    ? ComputeBeverages(guest, key, null, budget)
                    : Compute(guest, key, budget);

                var merged = curated.Items
                    .Concat(dynamicResult.HasMatch ? dynamicResult.Items : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .Take(GuestTagConfig.GetMaxRecipes())
                    .ToArray();

                Plugin.Logger.LogDebug(
                    $"{HarmonyTag} 稀客 [{stringId}] 命中查表名单（{GuestTagConfig.StrategyDisplayName(strategy)}）：" +
                    $"{string.Join(" / ", merged)}");

                return RecommendationResult.With(merged, curated.Label);
            }

            Plugin.Logger.LogDebug(
                $"{HarmonyTag} 稀客 [{stringId}] 的查表名单在 {GuestTagConfig.StrategyDisplayName(strategy)} 下被过滤光了，退回动态计算");
        }

        return beverage ? ComputeBeverages(guest, key, null, budget) : Compute(guest, key, budget);
    }

    /// <summary>把名单里的菜名换成「名字 (¥价格)」，按预算过滤，保持名单原顺序。</summary>
    internal static RecommendationResult ResolveCurated(
        string[] names,
        RecommendStrategy strategy,
        int budget,
        bool beverage,
        HashSet<string>? allowed)
    {
        var label = GuestTagConfig.StrategyDisplayName(strategy) + (beverage ? "酒水" : "推荐");
        var items = new List<string>();
        var priced = 0;

        foreach (var raw in names)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0) continue;

            var price = beverage ? FindBeveragePrice(name) : FindFoodPrice(name);

            // m03354：以前价格 0 会走 else 分支，把「水獭祭」这种没查到价的条目原样印出来，
            // 面板上就成了「水獭祭、天狗踊 (¥70)」。现在查不到价就直接跳过。
            if (price <= 0)
            {
                LogCuratedNoPriceOnce(name, beverage);
                continue;
            }

            // m03399：名单里的菜必须是「这局真能做出来」的，否则就会推大江户船祭那种神仙菜。
            if (allowed != null && allowed.Count > 0 && !allowed.Contains(name))
            {
                LogCuratedNotInMenuOnce(name, beverage);
                continue;
            }

            if (budget > 0 && price > budget) continue;

            // m03142：名单里的固定菜可能太便宜（比如预算 557 却推 14 块），
            // 低价优先时直接跳过去，让动态计算去挑预算 10%~30% 的菜。
            if (budget > 0 && strategy == RecommendStrategy.PriceLow &&
                (price < budget * LowBandMinRatio || price > budget * LowBandMaxRatio))
            {
                continue;
            }

            priced++;
            var text = beverage
                ? FormatPricedItem(name, price, StockOfBeverage(name))
                : FormatItem(name, price);
            if (!items.Contains(text)) items.Add(text);
        }

        // 一个价格都没查到（比如酒水单还没生成）就先别用名单：退回动态计算，
        // 免得面板上出现一串没价格的裸名字（m03065 实测踩到过）。
        if (items.Count == 0 || priced == 0) return RecommendationResult.NoMatch;

        return RecommendationResult.With(items.ToArray(), label);
    }

    private static bool TryGetCached(string key, out RecommendationResult result)
    {
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                var ttl = cached.Result.HasMatch ? CacheTtl : EmptyCacheTtl;
                if (DateTime.UtcNow - cached.CreatedUtc < ttl)
                {
                    result = cached.Result;
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    private static void StoreCache(string key, RecommendationResult result)
    {
        lock (CacheGate)
        {
            Cache[key] = new CacheEntry(result);
        }
    }

    private static RecommendationResult Compute(SpecialGuest guest, string stringId, int budget)
    {
        // 传进来的是缓存 key（stringId@预算#策略），日志里只留纯 stringId
        var logId = stringId.IndexOf('@') is var at && at > 0 ? stringId[..at] : stringId;

        try
        {
            var likeIds = ReadLikeTags(guest);
            if (likeIds.Count == 0) return RecommendationResult.NoPreference;

            var hateIds = ReadHateTags(guest);

            var (candidates, source) = CollectCandidates();
            var prepped = ReadPreppedSnapshot();
            var downgraded = false;
            if (candidates.Count == 0)
            {
                lock (_noMatchGate)
                {
                    if (_foodNoMatchLogged.Add("empty-pool:" + logId))
                    {
                        Plugin.Logger.LogDebug(
                            $"{HarmonyTag} 稀客 [{logId}] 的候选池为空（没有能做的菜），本次不推荐");
                    }
                }

                return RecommendationResult.NoMatch;
            }

            var scored = new List<(int Score, string Name, int Price, int Ingredients)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            var scanned = candidates.Count;

            ScoreCandidates(candidates, likeIds, hateIds, scored, seenNames);

            // m03399：冴月麟那种「菜单里没有合口味的菜」以前直接摆烂显示无匹配。
            // 现在放宽成：只要不带厌恶标签、价格读得到就给（排序仍按策略）。
            if (scored.Count == 0)
            {
                var relaxed = 0;
                foreach (var recipe in candidates)
                {
                    try
                    {
                        if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;
                        var food = recipe.Food;
                        if (food == null || food.Pointer == IntPtr.Zero) continue;

                        if (ReadTagArray(food.Tags).Any(tag => hateIds.Contains(tag))) continue;

                        var name = ReadName(food);
                        if (string.IsNullOrWhiteSpace(name) || !seenNames.Add(name!)) continue;

                        var price = ReadPrice(name!, food);
                        if (price <= 0) continue;

                        scored.Add((1, name!, price, ReadComplexity(recipe)));
                        relaxed++;
                    }
                    catch
                    {
                        // 单条失败跳过
                    }
                }

                if (relaxed > 0)
                {
                    downgraded = true;
                    source += "（菜单里没有更合口味的菜，已放宽标签）";
                    Plugin.Logger.LogDebug(
                        $"{HarmonyTag} 稀客 [{logId}] 菜单里没有合口味的菜，放宽标签后给了 {relaxed} 道（都不带厌恶标签）");
                }
            }

            if (scored.Count == 0)
            {
                LogFoodNoMatchOnce(logId, source, scanned);
                return RecommendationResult.NoMatch;
            }

            // 预算过滤：比客人钱包贵的菜直接排除，免得点了他付不起当场黑脸走人
            var affordable = budget > 0
                ? scored.Where(s => s.Price <= budget).ToList()
                : scored;

            if (affordable.Count == 0)
            {
                Plugin.Logger.LogDebug(
                    $"{HarmonyTag} 稀客 [{logId}] 预算 ¥{budget} 买不起任何合口味的菜（候选 {scored.Count} 个）");
                return RecommendationResult.NoMatch;
            }

            // 高价优先（m03216）：不再在「完全匹配喜好」的小菜里打转。
            // 直接放宽到「不带厌恶标签」的整张菜单（当晚菜单 + 全菜谱表），从里面挑预算内的大菜。
            if (GuestTagConfig.GetStrategy() == RecommendStrategy.PriceHigh)
            {
                var wide = new List<(int Score, string Name, int Price, int Ingredients)>();
                CollectNeutralFallback(candidates, hateIds, wide, seenNames);
                // m03275：不再扫 DataBaseCore.Recipes（会推没解锁/没食材的菜），
                // 候选池已在上游限制成「当晚菜单 ∩ 现在能做」。

                // 合并时按名字去重，完全匹配的条目优先（保留它更高的 score）
                var seenPool = new HashSet<string>(StringComparer.Ordinal);
                var pool = new List<(int Score, string Name, int Price, int Ingredients)>();
                foreach (var item in affordable) if (seenPool.Add(item.Name)) pool.Add(item);
                foreach (var item in wide)
                {
                    if (item.Price <= 0) continue;
                    if (budget > 0 && item.Price > budget) continue;
                    if (seenPool.Add(item.Name)) pool.Add(item);
                }

                // m03565：以前「只要 ≥¥150 这一档」会把列表压成一道（只有大奢宴够贵时，
                // 面板上就只剩「大奢宴 ×36」）。现在改成整池按价格降序排、顺次取满显示条数，
                // 让玩家有「大奢宴、豚骨拉面」这样的选择空间。
                if (pool.Count == 0)
                {
                    Plugin.Logger.LogDebug(
                        $"{HarmonyTag} 稀客 [{logId}] 高价优先：预算 ¥{budget} 内一道菜都没有，返回空");
                    return RecommendationResult.NoMatch;
                }

                var desired = Math.Max(3, GuestTagConfig.GetMaxRecipes());

                var ranked = pool
                    .OrderByDescending(x => x.Price)
                    .ThenByDescending(x => x.Score)
                    .ThenBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();

                // 备菜栏里已经做好的排前面，但不再把它当成唯一来源（以前 readyNow 会把列表截成 1~2 道）
                if (prepped != null && prepped.Count > 0)
                {
                    var ready = ranked.Where(x => prepped.Contains(x.Name)).ToList();
                    if (ready.Count > 0)
                    {
                        var rest = ranked.Where(x => !prepped.Contains(x.Name)).ToList();
                        ranked = ready.Concat(rest).ToList();
                    }
                }

                var expensive = ranked.Take(desired).ToList();
                affordable = expensive;
                source += $"（放宽标签，按价格从高到低取前 {expensive.Count} 道）";

                Plugin.Logger.LogDebug(
                    $"{HarmonyTag} 稀客 [{logId}] 高价池：可做 {ranked.Count} 道、预算 ¥{budget} 内，取前 {expensive.Count} 道：" +
                    string.Join(" / ", expensive.Select(x => $"{x.Name} (¥{x.Price})")));
            }

            // 低价优先：合口味的菜最便宜都比 60 贵 → 说明便宜的都被厌恶标签挡了，放宽后再挑便宜的
            var needCheap = GuestTagConfig.GetStrategy() == RecommendStrategy.PriceLow &&
                            affordable.Min(x => x.Price) > CheapPriceCeiling;

            if (needCheap && candidates.Count > 0)
            {
                var extra = new List<(int Score, string Name, int Price, int Ingredients)>();
                CollectNeutralFallback(candidates, hateIds, extra, seenNames);

                var usable = budget > 0 ? extra.Where(x => x.Price <= budget).ToList() : extra;
                if (usable.Count > 0)
                {
                    affordable.AddRange(usable);
                    source += "（含放宽标签的便宜菜）";
                }
            }

            // m03142：低价优先不是「越便宜越好」。预算高的客人推十几块的菜根本赚不到钱，
            // 所以先把池子收进「预算的 10%~30%」；找不到就放宽标签再找；还是不行的有钱客人至少别低于 20 块。
            if (GuestTagConfig.GetStrategy() == RecommendStrategy.PriceLow && budget > 0)
            {
                var bandLow = budget * LowBandMinRatio;
                var bandHigh = budget * LowBandMaxRatio;

                var inBand = affordable.Where(x => x.Price >= bandLow && x.Price <= bandHigh).ToList();
                if (inBand.Count == 0 && candidates.Count > 0)
                {
                    var extra = new List<(int Score, string Name, int Price, int Ingredients)>();
                    CollectNeutralFallback(candidates, hateIds, extra, seenNames);
                    foreach (var item in extra)
                    {
                        if (item.Price <= 0 || item.Price > budget) continue;
                        if (item.Price >= bandLow && item.Price <= bandHigh) inBand.Add(item);
                    }

                    if (inBand.Count > 0) source += "（含放宽标签的微利菜）";
                }

                if (inBand.Count > 0)
                {
                    affordable = inBand;
                }
                else if (budget > RichGuestBudget)
                {
                    var floored = affordable.Where(x => x.Price >= RichGuestMinPrice).ToList();
                    if (floored.Count > 0) affordable = floored;
                }
            }

            var result = Rank(
                affordable,
                GuestTagConfig.GetMaxRecipes(),
                beverage: false,
                downgraded ? UiDowngradeHint : string.Empty);

            Plugin.Logger.LogDebug(
                $"{HarmonyTag} 为稀客 \"{logId}\" 算出推荐（策略 {GuestTagConfig.StrategyDisplayName(GuestTagConfig.GetStrategy())}，" +
                $"候选来自{source}，共 {scanned} 个菜谱）：" +
                (GuestTagConfig.GetStrategy() == RecommendStrategy.FewIngredients
                    ? $"（复杂度 {string.Join(" / ", affordable.OrderBy(x => x.Ingredients).Take(3).Select(x => $"{x.Name}={x.Ingredients}"))}）"
                    : string.Empty) +
                (result.Items.Length > 0 ? string.Join(" / ", result.Items) : "无"));

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 计算推荐菜品失败（面板显示无匹配）: {ex.Message}");
            return RecommendationResult.NoMatch;
        }
    }

    /// <summary>
    /// 把「已通过喜好过滤」的候选按真实售价切成高低两档：
    /// 高价 = 售价从高到低前 N 个；低价 = 售价从低到高前 N 个（已经进了高价档的不再重复）。
    /// 匹配数不足 2N 时自然退化成「能显示多少显示多少」，不会硬凑。
    /// </summary>
    /// <summary>
    /// 严格按售价排序后拆两档（匹配数太少时合并成一行，不硬拆）：
    ///   1) 价格从高到低排；
    ///   2) 合口味的东西 ≤ 2 个 → 合并成一行，不标高/低价（避免「高价里出现 15 块」这种反直觉结果）；
    ///   3) 否则高价 = 最贵的 N 个，低价 = 最便宜 N 个（已经进高价的不重复）。
    /// </summary>
    /// <summary>
    /// 按当前策略排序并截断（不再硬拆高低价）：
    ///   PriceLow        → 价格从低到高（清库存）
    ///   PriceHigh       → 价格从高到低（狠狠赚一笔）
    ///   FewIngredients  → 食材种类少的优先，同档再按价格从低到高（少料易做；酒水没有食材，退化成低价优先）
    /// 同价再按得分、名字稳定排序，避免同一帧两次排序结果抖动。
    /// </summary>
    private static RecommendationResult Rank(
        List<(int Score, string Name, int Price, int Ingredients)> scored,
        int take,
        bool beverage,
        string labelSuffix = "")
    {
        take = Math.Max(1, take);

        var strategy = GuestTagConfig.GetStrategy();
        if (beverage && strategy == RecommendStrategy.FewIngredients)
        {
            // 酒水没有「食材」概念，少料易做下按价格从低到高排，标签也跟着说实话
            strategy = RecommendStrategy.PriceLow;
        }

        List<(int Score, string Name, int Price, int Ingredients)> ordered = strategy switch
        {
            // 高价优先：先挑 100~200 的「豪客价位」，再退到 60 以上，最后才是普通菜；
            // 同一档里价格高的在前（m02908）。
            RecommendStrategy.PriceHigh => scored
                .OrderByDescending(s => s.Price)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList(),

            RecommendStrategy.FewIngredients => scored
                .OrderBy(s => s.Ingredients)
                .ThenBy(s => s.Price)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList(),

            _ => scored
                .OrderBy(s => s.Price)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList(),
        };

        var picked = ordered.Take(take).ToList();
        var label = GuestTagConfig.StrategyDisplayName(strategy) + (beverage ? "酒水" : "推荐") + labelSuffix;

        // m03216：玩家点了「高价优先」就必须看到 [高价优先推荐]，哪怕只剩一个菜。
        // 以前「最高价不到 60 就改写成 [推荐菜品]」的降级逻辑已删除。

        var items = picked.Select(x => FormatItem(x.Name, x.Price)).ToArray();
        return RecommendationResult.With(items, label);
    }

    /// <summary>
    /// 酒水专用排序（m03317）：客人一晚上会点好几杯，所以要给「不同种类」的几瓶，而不是同一瓶重复。
    ///   · 高价优先：第一瓶给最贵的，其余优先从 ≥¥100 档里补不同的酒，不够再按价格降序补；
    ///   · 低价优先 / 少料易做：从最便宜的开始，库存多的优先，同样去重；
    ///   · 任何模式都不重复列同一款酒。
    /// </summary>
    private static RecommendationResult RankBeverages(
        List<(int Score, string Name, int Price, int Ingredients)> scored,
        int take,
        Dictionary<string, int> stock)
    {
        take = Math.Max(1, take);

        var strategy = GuestTagConfig.GetStrategy();
        if (strategy == RecommendStrategy.FewIngredients) strategy = RecommendStrategy.PriceLow;

        int StockOf(string name) => stock.TryGetValue(name, out var count) ? count : 0;

        // m03632：库存多的优先——先看「够不够开」再看价格。
        // 高价优先仍然价格优先，但同价位里库存多的排前面；低价优先则库存多的先出。
        var ordered = strategy == RecommendStrategy.PriceHigh
            ? scored
                .OrderByDescending(x => StockOf(x.Name) > 0)
                .ThenByDescending(x => x.Price)
                .ThenByDescending(x => StockOf(x.Name))
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList()
            : scored
                .OrderByDescending(x => StockOf(x.Name))
                .ThenBy(x => x.Price)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList();

        // 高价优先：先把 ≥¥100 的酒挑满，凑不够再按价格降序补（保证「多种不同的高价酒」）
        IEnumerable<(int Score, string Name, int Price, int Ingredients)> finalOrder = ordered;
        if (strategy == RecommendStrategy.PriceHigh)
        {
            var premium = ordered.Where(x => x.Price >= PremiumBandLow).ToList();
            finalOrder = premium.Concat(ordered.Where(x => x.Price < PremiumBandLow));
        }

        var picked = new List<(int Score, string Name, int Price, int Ingredients)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in finalOrder)
        {
            if (picked.Count >= take) break;
            if (!names.Add(item.Name)) continue;
            picked.Add(item);
        }

        if (picked.Count == 0) return RecommendationResult.NoMatch;

        var label = GuestTagConfig.StrategyDisplayName(strategy) + "酒水";
        var items = picked.Select(x => FormatPricedItem(x.Name, x.Price, StockOf(x.Name))).ToArray();
        return RecommendationResult.With(items, label);
    }

    /// <summary>这道菜需要几种食材（读不到就当很麻烦，排到少料易做的后面去）。</summary>
    private static int ReadComplexity(Recipe recipe)
    {
        var count = 0;
        var seconds = 600;

        try
        {
            var ingredients = recipe.Ingredients;
            if (ingredients != null) count = ingredients.Length;
        }
        catch
        {
            // 食材读不到就靠烹饪时间
        }

        try
        {
            var cookTime = recipe.CookTime;
            if (cookTime > 0f && cookTime < 600f) seconds = (int)Math.Round(cookTime);
        }
        catch
        {
            // 时间也读不到就按 600 秒算
        }

        // 食材种类为主（×1000），烹饪时间为辅；食材为空说明读不到，压到最后但保留时间排序
        if (count <= 0) return 99_000 + Math.Min(seconds, 999);
        return count * 1000 + Math.Min(seconds, 999);
    }

    private static void LogFoodNoMatchOnce(string stringId, string source, int scanned)
    {
        lock (_noMatchGate)
        {
            if (!_foodNoMatchLogged.Add((stringId ?? string.Empty) + "|" + source)) return;
        }

        Plugin.Logger.LogDebug(
            $"{HarmonyTag} 稀客 [{stringId}] 有口味偏好，但{source}（{scanned} 个菜谱）里没有正分匹配");
    }

    /// <summary>
    /// 候选池（m03535 重写）：只从「今日菜单 ∪ 玩家已解锁的菜谱」出发，
    /// 再按背包食材逐道验算「现在到底能不能做出来」——缺任何一样食材立刻剔除。
    /// 绝不再回落全库（DataBaseCore.Recipes），那正是「推了没解锁的神仙菜」的根源。
    /// </summary>
    private static (List<Recipe> Recipes, string Source) CollectCandidates()
    {
        var pool = new List<Recipe>();
        var seen = new HashSet<int>();
        var dailyCount = 0;
        var unlockedCount = 0;

        foreach (var recipe in CollectDailyRecipes())
        {
            try
            {
                if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;
                var key = recipe.FoodID;
                if (key <= 0 || !seen.Add(key)) continue;
                pool.Add(recipe);
                dailyCount++;
            }
            catch
            {
                // 单条失败跳过
            }
        }

        foreach (var recipe in CollectUnlockedRecipes())
        {
            try
            {
                if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;
                var key = recipe.FoodID;
                if (key <= 0 || !seen.Add(key)) continue;
                pool.Add(recipe);
                unlockedCount++;
            }
            catch
            {
                // 单条失败跳过
            }
        }

        // m03688 厨具闸门：台上没有这道菜需要的厨具（Pot/Grill/Fryer/Steamer/CuttingBoard）就整道踢掉。
        // 读不到厨具时 HasAnyCooker == false，本次不按厨具过滤，绝不因此清空整张菜单。
        if (pool.Count > 0 && CookwareFilter.HasAnyCooker)
        {
            var before = pool.Count;
            pool.RemoveAll(recipe =>
            {
                try
                {
                    if (recipe == null || recipe.Pointer == IntPtr.Zero) return true;
                    return !CookwareFilter.IsAvailable(recipe.CookerType);
                }
                catch
                {
                    return false;
                }
            });

            var removed = before - pool.Count;
            if (removed > 0)
            {
                Plugin.Logger.LogDebug(
                    $"{HarmonyTag} 厨具闸门：台上 {CookwareFilter.Describe()}，剔除 {removed} 道需要其它厨具的菜（剩 {pool.Count} 道）");
            }
        }

        if (pool.Count == 0)
        {
            lock (_noMatchGate)
            {
                if (_dailyMenuEmptyLogged.Add("no-pool"))
                {
                    Plugin.Logger.LogWarning(
                        $"{HarmonyTag} 今日菜单与已解锁菜谱都读不到，本次不给推荐（绝不推做不出来的菜）");
                }
            }

            return (new List<Recipe>(), "无（菜单与已解锁菜谱都读不到）");
        }

        var pantry = ReadPantrySnapshot();
        var prepped = ReadPreppedCounts();
        _craftCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // 库存一丁点都读不到（刚读档 / 存档切换）：退化成「菜单/已解锁里的菜 + 备菜栏」，
        // 这是用户认可的兜底方案，但绝不再往全库跑。
        if (pantry.Count == 0 && prepped.Count == 0)
        {
            Plugin.Logger.LogDebug(
                $"{HarmonyTag} 食材库存与备菜栏都读不到，按「今日菜单 {dailyCount} 道 + 已解锁 {unlockedCount} 道」直出");
            return (pool, $"今日菜单 {dailyCount} 道 + 已解锁 {unlockedCount} 道（食材库存暂不可读）");
        }

        var dailyIds = new HashSet<int>();
        foreach (var recipe in CollectDailyRecipes())
        {
            try
            {
                if (recipe != null && recipe.Pointer != IntPtr.Zero && recipe.FoodID > 0) dailyIds.Add(recipe.FoodID);
            }
            catch
            {
                // 单条失败忽略
            }
        }

        var craftable = new List<Recipe>();
        var blocked = 0;
        foreach (var recipe in pool)
        {
            try
            {
                var count = CountCraftable(recipe, pantry, prepped);
                var inMenu = false;
                try { inMenu = recipe.FoodID > 0 && dailyIds.Contains(recipe.FoodID); } catch { }

                if (count <= 0)
                {
                    // m03599：今日菜单是玩家自己挑的，游戏允许从店里现有食材直接做，
                    // 不能因为「背包里差一样调料」就把整行推荐砍成一道。
                    // 菜单里的菜保留（标记 1 份），不在菜单里的已解锁菜才严格剔除。
                    if (!inMenu)
                    {
                        blocked++;
                        continue;
                    }

                    count = 1;
                }

                craftable.Add(recipe);
                var name = ReadName(SafeFood(recipe));
                if (!string.IsNullOrEmpty(name)) _craftCounts![name!] = count;
            }
            catch
            {
                blocked++;
            }
        }

        Plugin.Logger.LogDebug(
            $"{HarmonyTag} 候选池：今日菜单 {dailyCount} 道 + 已解锁 {unlockedCount} 道，" +
            $"按背包食材能做 {craftable.Count} 道（缺食材剔除 {blocked} 道；库存 {pantry.Count} 种，备菜 {prepped.Count} 种）");

        if (craftable.Count == 0)
        {
            lock (_noMatchGate)
            {
                if (_foodNoMatchLogged.Add("craft-none"))
                {
                    Plugin.Logger.LogWarning(
                        $"{HarmonyTag} 背包食材一道菜都凑不出来（菜单/已解锁共 {pool.Count} 道），本次不给推荐");
                }
            }

            return (new List<Recipe>(), "无（背包食材凑不出任何菜）");
        }

        return (craftable, $"今日菜单 {dailyCount} 道 + 已解锁 {unlockedCount} 道 → 现在能做 {craftable.Count} 道");
    }

    /// <summary>玩家已解锁的菜谱：RunTimeStorage.Recipes（菜谱 id）→ DataBaseCore.Recipes。</summary>
    private static List<Recipe> CollectUnlockedRecipes()
    {
        var result = new List<Recipe>();

        try
        {
            var ids = GameData.RunTime.Common.RunTimeStorage.Recipes;
            if (ids == null || ids.Count == 0) return result;

            foreach (var id in ids)
            {
                if (id <= 0) continue;
                if (DataBaseCore.Recipes != null &&
                    DataBaseCore.Recipes.TryGetValue(id, out var recipe) && recipe != null)
                {
                    result.Add(recipe);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_unlockedRecipesFailureLogged)
            {
                _unlockedRecipesFailureLogged = true;
                Plugin.Logger.LogDebug($"{HarmonyTag} 读 RunTimeStorage.Recipes 失败: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>「现在能做出来的菜」的菜名集合：候选池（菜单/已解锁）∩ 食材可做 + 备菜栏。</summary>
    private static HashSet<string> CollectAvailableFoodNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var (pool, _) = CollectCandidates();
            var pantry = ReadPantrySnapshot();
            var prepped = ReadPreppedSnapshot();

            foreach (var name in prepped)
            {
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            // 白名单就是「候选池里的菜」（每晚菜单 / 食材能做的菜），不再二次做食材过滤，
            // 免得食材表读不全时白名单变空、反而放行了名单里的神仙菜。
            _ = pantry;
            foreach (var recipe in pool)
            {
                try
                {
                    if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;
                    var food = recipe.Food;
                    if (food == null || food.Pointer == IntPtr.Zero) continue;
                    var name = ReadName(food);
                    if (!string.IsNullOrEmpty(name)) names.Add(name!);
                }
                catch
                {
                    // 单条失败跳过
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug($"{HarmonyTag} 汇总可做菜名失败: {ex.Message}");
        }

        return names;
    }

    /// <summary>吧台库存里的酒名集合；读不到返回空集合（空表示不做限制）。</summary>
    private static HashSet<string> CollectStockedBeverageNames()
    {
        var stock = ReadBeverageStockSnapshot();
        return stock.Count > 0
            ? new HashSet<string>(stock.Keys, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private static List<Recipe> CollectDailyRecipes()
    {
        var list = new List<Recipe>();
        try
        {
            var configure = IzakayaConfigure.Instance;
            if (configure == null || configure.Pointer == IntPtr.Zero) return list;

            var daily = configure.DailyRecipes;
            if (daily == null) return list;

            foreach (var recipe in daily)
            {
                if (recipe != null) list.Add(recipe);
            }
        }
        catch (Exception ex)
        {
            if (!_dailyMenuFailureLogged)
            {
                _dailyMenuFailureLogged = true;
                Plugin.Logger.LogWarning(
                    $"{HarmonyTag} 读 IzakayaConfigure.Instance.DailyRecipes 失败，改用全菜谱表: {ex.Message}");
            }
        }

        return list;
    }

    private static List<Recipe> CollectAllRecipes()
    {
        var all = new List<Recipe>();
        try
        {
            foreach (var pair in DataBaseCore.Recipes)
            {
                var recipe = pair.Value;
                if (recipe != null) all.Add(recipe);
            }
        }
        catch (Exception ex)
        {
            if (!_allRecipesFailureLogged)
            {
                _allRecipesFailureLogged = true;
                Plugin.Logger.LogWarning($"{HarmonyTag} 读 DataBaseCore.Recipes 失败: {ex.Message}");
            }
        }

        return all;
    }

    /// <summary>按「喜好命中 ×2、讨厌命中 ×3 扣分」给候选菜打分，只把正分菜收进 <paramref name="scored"/>。</summary>
    private static void ScoreCandidates(
        List<Recipe> candidates,
        HashSet<int> likeIds,
        HashSet<int> hateIds,
        List<(int Score, string Name, int Price, int Ingredients)> scored,
        HashSet<string> seenNames)
    {
        foreach (var recipe in candidates)
        {
            if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;

            try
            {
                var food = recipe.Food;
                if (food == null || food.Pointer == IntPtr.Zero) continue;

                var dishTags = ReadTagArray(food.Tags);
                if (dishTags.Count == 0) continue;

                var likeHits = dishTags.Count(tag => likeIds.Contains(tag));
                if (likeHits == 0) continue;

                var hateHits = hateIds.Count == 0 ? 0 : dishTags.Count(tag => hateIds.Contains(tag));

                // 带讨厌标签的菜一律不进池子：稀客吃到厌恶的东西会掉好感，
                // 以前只扣 3 分，两个喜好标签就能把分数顶回正数（m02908 修掉）。
                if (hateHits > 0) continue;

                var score = likeHits * 2 - hateHits * 3;
                if (score <= 0) continue;

                var name = ReadName(food);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seenNames.Add(name!)) continue;

                // m03354：菜品同理——读不到价格就别进列表，避免出现没有 (¥xx) 的裸菜名。
                var foodPrice = ReadPrice(name!, food);
                if (foodPrice <= 0)
                {
                    LogCuratedNoPriceOnce(name!, beverage: false);
                    continue;
                }

                scored.Add((score, name!, foodPrice, ReadComplexity(recipe)));
            }
            catch
            {
                // 单个菜读失败不影响其它菜
            }
        }
    }

    /// <summary>
    /// 放宽标签的兜底池（m02908 的「平替搜索」）：标签完全不匹配也能进，只要不带稀客厌恶的标签。
    /// 高价优先拿它去捞贵菜、低价优先拿它去捞便宜菜，具体价格筛选交给调用方。
    /// </summary>
    /// <summary>
    /// m03248 诊断用：一次性把「当晚菜单」和「整本菜谱」里最贵的几道菜打出来，
    /// 方便确认插件到底读没读到高级菜（价格来自 Sellable.TrueValue，没有写死上限）。
    /// </summary>
    private static void DumpPriceLadderOnce(List<Recipe> daily, List<Recipe> all)
    {
        if (_priceLadderLogged) return;
        _priceLadderLogged = true;

        try
        {
            string Top(IEnumerable<Recipe> recipes)
            {
                var rows = new List<(string Name, int Price)>();
                foreach (var recipe in recipes)
                {
                    var food = SafeFood(recipe);
                    if (food == null) continue;
                    var name = ReadName(food);
                    if (string.IsNullOrEmpty(name)) continue;
                    rows.Add((name, ReadPrice(name, food)));
                }

                return string.Join(" / ", rows
                    .Where(x => x.Price > 0)
                    .OrderByDescending(x => x.Price)
                    .Take(8)
                    .Select(x => $"{x.Name} (¥{x.Price})"));
            }

            Plugin.Logger.LogInfo($"{HarmonyTag} 当晚菜单最贵的 8 道：{Top(daily)}");
            Plugin.Logger.LogInfo($"{HarmonyTag} 整本菜谱最贵的 8 道：{Top(all)}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogDebug($"{HarmonyTag} 价格阶梯诊断失败（忽略）：{ex.Message}");
        }
    }

    private static Sellable? SafeFood(Recipe recipe)
    {
        try
        {
            var food = recipe.Food;
            return food != null && food.Pointer != IntPtr.Zero ? food : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>玩家仓库里的食材：ingredientId → 数量（读不到就返回空字典）。</summary>
    private static Dictionary<int, int> ReadPantrySnapshot()
    {
        var result = new Dictionary<int, int>();
        try
        {
            var source = GameData.RunTime.Common.RunTimeStorage.Ingredients;
            if (source == null) return result;
            foreach (var pair in source)
            {
                if (pair.Key > 0 && pair.Value > 0) result[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            if (!_pantryFailureLogged)
            {
                _pantryFailureLogged = true;
                Plugin.Logger.LogDebug($"{HarmonyTag} 读 RunTimeStorage.Ingredients 失败（跳过食材检查）: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>备菜栏里已经做好、随时能端出去的菜名（读不到返回空集合）。</summary>
    private static HashSet<string> ReadPreppedSnapshot()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var source = GameData.RunTime.Common.RunTimeStorage.Foods;
            if (source == null) return result;
            foreach (var pair in source)
            {
                if (pair.Key <= 0 || pair.Value <= 0) continue;
                if (DataBaseCore.Foods != null && DataBaseCore.Foods.TryGetValue(pair.Key, out var food) && food != null)
                {
                    var name = ReadName(food);
                    if (!string.IsNullOrEmpty(name)) result.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_preppedFailureLogged)
            {
                _preppedFailureLogged = true;
                Plugin.Logger.LogDebug($"{HarmonyTag} 读 RunTimeStorage.Foods 失败（跳过备菜优先）: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>备菜栏里已经做好的菜：菜名 → 份数（读不到返回空字典）。</summary>
    private static Dictionary<string, int> ReadPreppedCounts()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var source = GameData.RunTime.Common.RunTimeStorage.Foods;
            if (source == null) return result;
            foreach (var pair in source)
            {
                if (pair.Key <= 0 || pair.Value <= 0) continue;
                if (DataBaseCore.Foods != null && DataBaseCore.Foods.TryGetValue(pair.Key, out var food) && food != null)
                {
                    var name = ReadName(food);
                    if (string.IsNullOrEmpty(name)) continue;
                    result[name!] = result.TryGetValue(name!, out var have) ? have + pair.Value : pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_preppedFailureLogged)
            {
                _preppedFailureLogged = true;
                Plugin.Logger.LogDebug($"{HarmonyTag} 读 RunTimeStorage.Foods 失败（跳过备菜优先）: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 这道菜现在能做几份（m03535）：备菜栏有就按备的份数，否则按背包食材算
    /// `min(库存 / 每份需求)`；缺任何一样食材返回 0。
    /// </summary>
    private static int CountCraftable(Recipe recipe, Dictionary<int, int> pantry, Dictionary<string, int> prepped)
    {
        try
        {
            var name = ReadName(SafeFood(recipe));
            if (!string.IsNullOrEmpty(name) && prepped.TryGetValue(name!, out var preppedCount) && preppedCount > 0)
            {
                return preppedCount;
            }

            // m03599：游戏自己就有「这道菜现在能做几份」的接口（Recipe.GetCookCountNum 返回可读文本，
            // 比如「×3」「可制作 3 份」）。先信它，解析不出来再按背包手算。
            try
            {
                var text = recipe.GetCookCountNum();
                if (!string.IsNullOrEmpty(text))
                {
                    var digits = new string(text!.Where(char.IsDigit).ToArray());
                    if (digits.Length > 0 && int.TryParse(digits, out var fromGame) && fromGame > 0)
                    {
                        return Math.Min(fromGame, 99);
                    }
                }
            }
            catch
            {
                // 接口不可用时走手算
            }

            var ingredients = recipe.Ingredients;
            if (ingredients == null || ingredients.Length == 0) return 0;

            var need = new Dictionary<int, int>();
            foreach (var ingredientId in ingredients)
            {
                if (ingredientId <= 0) continue;
                need[ingredientId] = need.TryGetValue(ingredientId, out var c) ? c + 1 : 1;
            }

            if (need.Count == 0) return 0;

            var max = int.MaxValue;
            foreach (var pair in need)
            {
                if (!pantry.TryGetValue(pair.Key, out var have) || have < pair.Value) return 0;
                var possible = have / pair.Value;
                if (possible < max) max = possible;
            }

            return max == int.MaxValue ? 0 : Math.Max(0, max);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>这道菜现在做不做得出来：备菜栏已有，或食材库存够。</summary>
    private static bool IsCraftableNow(Recipe recipe, Dictionary<int, int> pantry, HashSet<string> prepped)
    {
        try
        {
            var name = ReadName(SafeFood(recipe));
            if (!string.IsNullOrEmpty(name) && prepped.Contains(name)) return true;

            // m03461：库存读不到时只认「已经备好的菜」，绝不因为「读不到」就当作能做，
            // 否则又会出现「推了 ¥206 的大江户船祭、其实没解锁」这种问题。
            if (pantry.Count == 0) return false;

            var ingredients = recipe.Ingredients;
            if (ingredients == null || ingredients.Length == 0)
            {
                // 食材表读不出来 = 无法确认能做，宁可不推
                return false;
            }

            var need = new Dictionary<int, int>();
            foreach (var ingredientId in ingredients)
            {
                if (ingredientId <= 0) continue;
                need[ingredientId] = need.TryGetValue(ingredientId, out var count) ? count + 1 : 1;
            }

            foreach (var pair in need)
            {
                if (!pantry.TryGetValue(pair.Key, out var have) || have < pair.Value) return false;
            }

            return true;
        }
        catch
        {
            // 读食材失败也不当作能做（安全优先）
            return false;
        }
    }

    /// <summary>吧台库存：酒名 → 瓶数（RunTimeStorage.Beverages → DataBaseCore.Beverages），读不到返回空字典。</summary>
    private static Dictionary<string, int> ReadBeverageStockSnapshot()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var source = GameData.RunTime.Common.RunTimeStorage.Beverages;
            if (source == null) return result;
            foreach (var pair in source)
            {
                if (pair.Key <= 0 || pair.Value <= 0) continue;
                if (DataBaseCore.Beverages != null &&
                    DataBaseCore.Beverages.TryGetValue(pair.Key, out var drink) && drink != null)
                {
                    var name = ReadName(drink);
                    if (!string.IsNullOrEmpty(name)) result[name!] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_beverageStockFailureLogged)
            {
                _beverageStockFailureLogged = true;
                Plugin.Logger.LogDebug($"{HarmonyTag} 读 RunTimeStorage.Beverages 失败（跳过库存过滤）: {ex.Message}");
            }
        }

        return result;
    }

    private static void CollectNeutralFallback(
        List<Recipe> candidates,
        HashSet<int> hateIds,
        List<(int Score, string Name, int Price, int Ingredients)> target,
        HashSet<string> seenNames)
    {
        foreach (var recipe in candidates)
        {
            if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;

            try
            {
                var food = recipe.Food;
                if (food == null || food.Pointer == IntPtr.Zero) continue;

                var dishTags = ReadTagArray(food.Tags);
                if (dishTags.Count > 0 && hateIds.Count > 0 &&
                    dishTags.Any(tag => hateIds.Contains(tag)))
                {
                    continue;
                }

                var name = ReadName(food);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seenNames.Add(name!)) continue;

                var price = ReadPrice(name!, food);
                if (price <= 0) continue;

                target.Add((0, name!, price, ReadComplexity(recipe)));
            }
            catch
            {
                // 单个菜读失败不影响其它菜
            }
        }
    }

    private static HashSet<int> ReadLikeTags(SpecialGuest guest)
    {
        var set = new HashSet<int>();
        try
        {
            foreach (var tag in ReadTagArray(guest.LikeFoodTag))
            {
                set.Add(tag);
            }
        }
        catch
        {
            // 读不到就当没有喜好标签
        }

        return set;
    }

    private static HashSet<int> ReadHateTags(SpecialGuest guest)
    {
        var set = new HashSet<int>();
        try
        {
            // 注意：HateFoodTag 是 Il2CppSystem 的 IEnumerable<int>，其 IEnumerator 没有 MoveNext/Current，
            // 不能用 foreach（会 CS0117/CS0202）；HateFoodTagOriginal 是 Il2CppStructArray<int>，可以直接遍历。
            foreach (var tag in ReadTagArray(guest.HateFoodTagOriginal))
            {
                set.Add(tag);
            }
        }
        catch
        {
            // 讨厌标签读不到就只算喜好
        }

        return set;
    }

    private static List<int> ReadTagArray(Il2CppStructArray<int>? raw)
    {
        var list = new List<int>();
        if (raw == null) return list;

        try
        {
            var length = raw.Length; // 长度本身也可能抛（悬空指针），所以整段包 try
            if (length <= 0 || length > 256) return list;

            foreach (var tag in raw)
            {
                list.Add(tag);
                if (list.Count >= 32) break; // 防御：标签不会这么多
            }
        }
        catch
        {
            // 读到一半失败就返回已有部分，绝不把异常抛出去
        }

        return list;
    }

    /// <summary>按菜名在「当晚菜单 → 全菜谱表」里查真实售价；查不到返回 0。</summary>
    private static int FindFoodPrice(string name)
    {
        var daily = CollectDailyRecipes();
        var price = FindFoodPriceIn(daily, name);
        if (price > 0) return price;

        return FindFoodPriceIn(CollectAllRecipes(), name);
    }

    private static int FindFoodPriceIn(List<Recipe> recipes, string name)
    {
        foreach (var recipe in recipes)
        {
            try
            {
                if (recipe == null || recipe.Pointer == IntPtr.Zero) continue;

                var food = recipe.Food;
                if (food == null || food.Pointer == IntPtr.Zero) continue;

                if (!string.Equals(ReadName(food), name, StringComparison.Ordinal)) continue;

                var price = ReadPrice(name, food);
                if (price > 0) return price;
            }
            catch
            {
                // 单条读失败跳过
            }
        }

        return 0;
    }

    /// <summary>按酒名在「当晚酒水单 → 全酒水表」里查真实售价；查不到返回 0。</summary>
    private static int FindBeveragePrice(string name)
    {
        var price = FindBeveragePriceIn(CollectBeverageCandidates().Drinks, name);
        if (price > 0) return price;

        // m03354：当晚酒水单里没有这瓶（大吟酿 / 水獭祭还没备上台）也要能报出价格，
        // 否则名单里的贵酒就成了一串没价格的裸名字。这里退到整本酒水表。
        var all = new List<Sellable>();
        try
        {
            foreach (var pair in DataBaseCore.Beverages)
            {
                if (pair.Value != null) all.Add(pair.Value);
            }
        }
        catch (Exception ex)
        {
            if (!_allBeveragesFailureLogged)
            {
                _allBeveragesFailureLogged = true;
                Plugin.Logger.LogDebug($"{HarmonyTag} 查价格时读 DataBaseCore.Beverages 失败: {ex.Message}");
            }
        }

        return FindBeveragePriceIn(all, name);
    }

    private static int FindBeveragePriceIn(List<Sellable> candidates, string name)
    {
        foreach (var drink in candidates)
        {
            try
            {
                if (drink == null || drink.Pointer == IntPtr.Zero) continue;
                if (!string.Equals(ReadName(drink), name, StringComparison.Ordinal)) continue;

                var price = ReadPrice(name, drink);
                if (price > 0) return price;
            }
            catch
            {
                // 单条读失败跳过
            }
        }

        return 0;
    }

    // ───────────────────────── 酒水 ─────────────────────────

    /// <summary>
    /// 按酒水喜好标签推荐酒水（最多 1 杯，优先当晚酒水单）。
    /// 喜好标签优先读游戏运行时 SpecialGuest.LikeBevTag；读不到时退回硬编码的 bevTags（按人设推断）；
    /// 两者都没有 = 无明显偏好（<see cref="RecommendationResult.NoPreference"/>）。
    /// </summary>
    internal static RecommendationResult RecommendBeverages(
        SpecialGuest guest,
        string stringId,
        string[]? hardcodedTags,
        int budget)
    {
        var key = "bev:" + (stringId ?? string.Empty) + "@" + budget + "#" + GuestTagConfig.GetStrategy();
        if (TryGetCached(key, out var cached)) return cached;

        var result = ComputeWithCuratedTable(guest, stringId ?? string.Empty, key, budget, beverage: true);
        StoreCache(key, result);
        return result;
    }

    /// <summary>酒水的放宽池：标签不匹配也能进，只要价格够高（高价模式下用来捞贵酒）。</summary>
    private static void CollectNeutralBeverageFallback(
        List<Sellable> candidates,
        List<(int Score, string Name, int Price, int Ingredients)> target,
        HashSet<string> seenNames)
    {
        foreach (var drink in candidates)
        {
            if (drink == null || drink.Pointer == IntPtr.Zero) continue;

            try
            {
                var name = ReadName(drink);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seenNames.Add(name!)) continue;

                var price = ReadPrice(name!, drink);
                if (price <= 0) continue;

                target.Add((0, name!, price, 0));
            }
            catch
            {
                // 单杯读失败不影响其它
            }
        }
    }

    private static RecommendationResult ComputeBeverages(
        SpecialGuest guest,
        string stringId,
        string[]? hardcodedTags,
        int budget)
    {
        try
        {
            var likeNames = new HashSet<string>(StringComparer.Ordinal);

            // 1) 游戏运行时数据优先
            try
            {
                foreach (var tagName in ReadTagNames(guest.LikeBevTag))
                {
                    likeNames.Add(tagName);
                }
            }
            catch
            {
                // LikeBevTag 读不到就往下走硬编码
            }

            // 2) 硬编码兜底
            if (likeNames.Count == 0 && hardcodedTags != null)
            {
                foreach (var tagName in hardcodedTags)
                {
                    if (!string.IsNullOrWhiteSpace(tagName)) likeNames.Add(tagName);
                }
            }

            if (likeNames.Count == 0) return RecommendationResult.NoPreference;

            var (candidates, source) = CollectBeverageCandidates();
            if (candidates.Count == 0) return RecommendationResult.NoMatch;

            // m03632：只在「吧台里真有酒」的里面挑（RunTimeStorage.Beverages）。
            // 库存为 0 的一律不露面——玩家手里没这瓶酒，推荐了也端不出来。
            var stocked = ReadBeverageStockSnapshot();
            if (stocked.Count > 0)
            {
                _beverageStockSnapshot = stocked;
            }

            if (stocked.Count > 0)
            {
                var inStock = new List<Sellable>();
                foreach (var drink in candidates)
                {
                    if (drink == null || drink.Pointer == IntPtr.Zero) continue;
                    var drinkName = ReadName(drink);
                    if (string.IsNullOrEmpty(drinkName)) continue;
                    if (stocked.TryGetValue(drinkName!, out var bottles) && bottles > 0) inStock.Add(drink);
                }

                if (inStock.Count == 0)
                {
                    Plugin.Logger.LogDebug(
                        $"{HarmonyTag} 稀客 [{stringId}] 吧台里没有候选酒水（库存 {stocked.Count} 款），本次不推荐酒水");
                    return RecommendationResult.NoMatch;
                }

                candidates = inStock;
                source += "（吧台库存 >0）";
            }

            var scored = new List<(int Score, string Name, int Price, int Ingredients)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var drink in candidates)
            {
                if (drink == null || drink.Pointer == IntPtr.Zero) continue;

                try
                {
                    var drinkTags = ReadTagNames(drink.Tags);
                    if (drinkTags.Count == 0) continue;

                    var hits = drinkTags.Count(tag => likeNames.Contains(tag));

                    // m03303：高价优先时「不踩厌恶的贵酒」也能推（hits=0 也留下）；
                    // 低价 / 少料易做仍然只推合口味的。
                    if (hits == 0 && GuestTagConfig.GetStrategy() != RecommendStrategy.PriceHigh) continue;

                    var name = ReadName(drink);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!seenNames.Add(name!)) continue;

                    // m03354：读不到价格就别展示，否则面板上会出现没有 (¥xx) 的裸酒名。
                    var drinkPrice = ReadPrice(name!, drink);
                    if (drinkPrice <= 0) continue;

                    scored.Add((hits * 2, name!, drinkPrice, 0));
                }
                catch
                {
                    // 单杯读失败不影响其它
                }
            }

            if (scored.Count == 0)
            {
                lock (_noMatchGate)
                {
                    if (_bevNoMatchLogged.Add(stringId))
                    {
                        Plugin.Logger.LogDebug(
                            $"{HarmonyTag} 稀客 [{stringId}] 有酒水喜好（{string.Join(" / ", likeNames)}），" +
                            $"但{source}里没有匹配的酒水");
                    }
                }

                return RecommendationResult.NoMatch;
            }

            var affordable = budget > 0
                ? scored.Where(s => s.Price <= budget).ToList()
                : scored;

            if (affordable.Count == 0)
            {
                Plugin.Logger.LogDebug(
                    $"{HarmonyTag} 稀客 [{stringId}] 预算 ¥{budget} 买不起任何合口味的酒水（候选 {scored.Count} 款）");
                return RecommendationResult.NoMatch;
            }

            // 高价优先（m03303）：直接在「所有不踩厌恶、单瓶不超预算」的酒里按价格三档挑最贵的，
            // 不再要求命中喜好标签，也不再只看当晚酒水单里的便宜货。
            if (GuestTagConfig.GetStrategy() == RecommendStrategy.PriceHigh)
            {
                // m03565：以前「≥¥150 只有麒麟就只推麒麟」，现在整柜按价格降序排、取满显示条数
                var desiredDrinks = Math.Max(3, GuestTagConfig.GetMaxDrinks());
                var ranked = affordable
                    .OrderByDescending(x => x.Price)
                    .ThenByDescending(x => x.Score)
                    .ThenBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();

                if (ranked.Count > 0)
                {
                    affordable = ranked.Take(desiredDrinks).ToList();
                    var topPrice = affordable[0].Price;
                    source += topPrice >= PremiumBandLow
                        ? $"（高价优先：按价格从高到低取前 {affordable.Count} 款）"
                        : $"（高价优先：吧台没有 ¥{PremiumBandLow} 以上的酒，降级给有货的普通酒，最贵 ¥{topPrice}）";
                }
            }

            if (GuestTagConfig.GetStrategy() == RecommendStrategy.PriceLow && budget > 0)
            {
                var bandLow = budget * LowBandMinRatio;
                var bandHigh = budget * LowBandMaxRatio;
                var inBand = affordable.Where(x => x.Price >= bandLow && x.Price <= bandHigh).ToList();
                if (inBand.Count > 0)
                {
                    affordable = inBand;
                }
                else if (budget > RichGuestBudget)
                {
                    var floored = affordable.Where(x => x.Price >= RichGuestMinPrice).ToList();
                    if (floored.Count > 0) affordable = floored;
                }
            }

            var result = RankBeverages(affordable, GuestTagConfig.GetMaxDrinks(), stocked);

            Plugin.Logger.LogDebug(
                $"{HarmonyTag} 为稀客 [{stringId}] 推荐酒水（策略 {GuestTagConfig.StrategyDisplayName(GuestTagConfig.GetStrategy())}，" +
                $"喜好 {string.Join(" / ", likeNames)}，候选来自{source}，共 {candidates.Count} 款）：" +
                (result.Items.Length > 0 ? string.Join(" / ", result.Items) : "无"));

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 计算推荐酒水失败（面板显示无匹配）: {ex.Message}");
            return RecommendationResult.NoMatch;
        }
    }

    private static (List<Sellable> Drinks, string Source) CollectBeverageCandidates()
    {
        var list = new List<Sellable>();

        try
        {
            var configure = IzakayaConfigure.Instance;
            if (configure != null && configure.Pointer != IntPtr.Zero)
            {
                var daily = configure.DailyBeverages;
                if (daily != null && daily.Count > 0)
                {
                    foreach (var drink in daily)
                    {
                        if (drink != null) list.Add(drink);
                    }

                    return (list, "当晚酒水单");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_beverageMenuFailureLogged)
            {
                _beverageMenuFailureLogged = true;
                Plugin.Logger.LogWarning(
                    $"{HarmonyTag} 读 IzakayaConfigure.Instance.DailyBeverages 失败，改用全酒水表: {ex.Message}");
            }
        }

        try
        {
            foreach (var pair in DataBaseCore.Beverages)
            {
                var drink = pair.Value;
                if (drink != null) list.Add(drink);
            }
        }
        catch (Exception ex)
        {
            if (!_allBeveragesFailureLogged)
            {
                _allBeveragesFailureLogged = true;
                Plugin.Logger.LogWarning($"{HarmonyTag} 读 DataBaseCore.Beverages 失败: {ex.Message}");
            }
        }

        return (list, "全酒水表");
    }

    /// <summary>标签 id → 酒水短标签名（DataBaseLanguage.GetBeverageTag）。</summary>
    private static List<string> ReadTagNames(Il2CppStructArray<int>? ids)
    {
        var names = new List<string>();
        if (ids == null) return names;

        int length;
        try
        {
            length = ids.Length;
        }
        catch
        {
            return names;
        }

        if (length <= 0 || length > 256) return names;

        foreach (var tagId in ids)
        {
            try
            {
                var text = GameData.CoreLanguage.Collections.DataBaseLanguage.GetBeverageTag(tagId);
                if (!string.IsNullOrWhiteSpace(text) && !names.Contains(text))
                {
                    names.Add(text);
                }
            }
            catch
            {
                // 单个标签取不到就跳过
            }

            if (names.Count >= 16) break;
        }

        return names;
    }

    /// <summary>
    /// 读一份菜/酒的售价：
    ///   1) 玩家在 cfg 的 Prices.CustomPrices 里填了同名价格 → 用玩家的（发布版给玩家留的余地）；
    ///   2) 否则用 TrueValue（含等级/修正后的实际售价，跟游戏菜单一致）；
    ///   3) 再不行退 BaseValue；都读不到返回 0，UI 就只显示名字、不显示括号价格。
    /// </summary>
    /// <summary>价格上限：防止读到野指针里的垃圾数字把 UI 撑爆。</summary>
    private const int MaxSanePrice = 1_000_000;

    /// <summary>「高价」的绝对门槛：最高价都不到这个数的池子，没资格叫高价推荐。</summary>
    private static bool _priceLadderLogged;

    /// <summary>最近一次读到的吧台库存（酒名 → 瓶数），只用于面板显示 /xN。</summary>
    private static Dictionary<string, int> _beverageStockSnapshot = new(StringComparer.Ordinal);

    private static int StockOfBeverage(string name) =>
        _beverageStockSnapshot.TryGetValue(name, out var bottles) ? bottles : 0;

    /// <summary>当前这一轮「每道菜能做几份」（菜名 → 份数），只用于面板显示 ×N。</summary>
    private static Dictionary<string, int>? _craftCounts;
    private const string UiDowngradeHint = "（食材不足，已降级）";
    private static bool _pantryFailureLogged;
    private static bool _preppedFailureLogged;
    private static bool _beverageStockFailureLogged;
    private static readonly HashSet<string> NoPriceLogged = new(StringComparer.Ordinal);
    private static bool _unlockedRecipesFailureLogged;
    private static readonly HashSet<string> _dailyMenuEmptyLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _curatedNotInMenuLogged = new(StringComparer.OrdinalIgnoreCase);

    private static void LogCuratedNotInMenuOnce(string name, bool beverage)
    {
        lock (_noMatchGate)
        {
            if (!_curatedNotInMenuLogged.Add((beverage ? "bev:" : "food:") + name)) return;
        }

        Plugin.Logger.LogDebug(
            $"{HarmonyTag} 名单条目「{name}」不在今晚菜单/吧台库存里，已跳过（避免推荐玩家做不出来的菜）");
    }

    /// <summary>名单条目在游戏数据里查不到价格时只提示一次（避免每帧刷日志）。</summary>
    private static void LogCuratedNoPriceOnce(string name, bool beverage)
    {
        lock (_noMatchGate)
        {
            if (!NoPriceLogged.Add((beverage ? "bev:" : "food:") + name)) return;
        }

        Plugin.Logger.LogDebug($"{HarmonyTag} 名单条目「{name}」在游戏数据里查不到价格，已跳过（不显示无价格的条目）");
    }
    private const int HighPriceFloor = 60;
    /// <summary>预算高过这个数的客人（m03142）：低价优先也不能推十几块的菜，否则一晚上白干。</summary>
    private const int RichGuestBudget = 200;
    /// <summary>有钱客人身上能接受的最低价。</summary>
    private const int RichGuestMinPrice = 20;
    /// <summary>低价优先的理想区间：预算的 10%~30%（微利但保底）。</summary>
    private const double LowBandMinRatio = 0.10;
    private const double LowBandMaxRatio = 0.30;

    /// <summary>「低价」的门槛：最便宜的合口味菜都比这个贵，说明真是没便宜的（m02989）。</summary>
    private const int CheapPriceCeiling = 60;

    /// <summary>高价优先时偏爱的「豪客价位」区间：预算够就优先推这一段。</summary>
    private const int PremiumBandLow = 100;
    /// <summary>高价优先的第一档（m03248）：预算内 ≥¥150 的菜优先推。</summary>
    private const int SuperPremiumFloor = 150;

    private const int PremiumBandHigh = 200;

    private static int ReadPrice(string name, Sellable sellable)
    {
        // ① 玩家自定义覆盖（纯托管字典，不碰游戏内存）
        try
        {
            if (TryGetCustomPrice(name, out var custom)) return custom;
        }
        catch
        {
            // 配置读挂了就当没填
        }

        // ② 先确认对象还活着：IL2CPP 侧对象可能已被销毁，Pointer 会变成 0 或悬空；
        //    这一步能挡掉绝大多数「访问已释放的 native 内存」。
        if (!IsAlive(sellable)) return 0;

        // ③ TrueValue / BaseValue 各自独立 try-catch：任何一条读失败都不影响另一条，
        //    也绝不会把异常抛回 Harmony 补丁（抛出去会打断游戏的调用栈）。
        try
        {
            var trueValue = sellable!.TrueValue;
            if (trueValue > 0 && trueValue <= MaxSanePrice) return trueValue;
        }
        catch
        {
            // 读不到就退 BaseValue
        }

        try
        {
            var baseValue = sellable!.BaseValue;
            if (baseValue > 0 && baseValue <= MaxSanePrice) return baseValue;
        }
        catch
        {
            // 都读不到就返回 0（UI 只显示菜名）
        }

        return 0;
    }

    /// <summary>
    /// IL2CPP 对象存活检查：null 或 Pointer == 0 都视为已销毁。
    /// 读任何 native 属性之前都先过一遍，避免访问已释放内存。
    /// </summary>
    private static bool IsAlive(Il2CppObjectBase? obj)
    {
        try
        {
            return obj != null && obj.Pointer != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 Prices.CustomPrices：「菜名 = 价格」，多项用分号（或竖线/逗号）分隔。</summary>
    private static bool TryGetCustomPrice(string name, out int price)
    {
        price = 0;
        var raw = GuestTagConfig.CustomPrices?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        foreach (var part in raw!.Split(';', '|', '\n'))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            var parts = part.Split('=');
            if (parts.Length < 2) continue;

            var key = parts[0].Trim();
            if (!string.Equals(key, name, StringComparison.Ordinal)) continue;

            if (int.TryParse(parts[1].Trim(), out var value) && value > 0)
            {
                price = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>「冷豆腐 (¥20/x3)」；价格读不到就只给名字，份数未知就不带 /x。</summary>
    private static string FormatItem(string name, int price)
    {
        var count = 0;
        try
        {
            if (_craftCounts != null && _craftCounts.TryGetValue(name, out var have)) count = have;
        }
        catch
        {
            // 份数读不到就当没有
        }

        return FormatPricedItem(name, price, count);
    }

    /// <summary>「水獭祭 (¥130/x2)」——价格 + 库存/可做份数（m03632）。</summary>
    private static string FormatPricedItem(string name, int price, int count)
    {
        if (price <= 0) return name;
        return count > 0 ? $"{name} (¥{price}/x{count})" : $"{name} (¥{price})";
    }

    private static string? ReadName(Sellable sellable)
    {
        if (!IsAlive(sellable)) return null;

        try
        {
            var text = sellable!.Text;
            if (!IsAlive(text)) return null;
            return text!.Name;
        }
        catch
        {
            return null;
        }
    }
}
