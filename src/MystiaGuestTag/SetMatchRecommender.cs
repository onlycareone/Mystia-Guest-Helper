using System;
using System.Collections.Generic;
using GameData.Core.Collections;
using GameData.Core.Collections.NightSceneUtility;
using GameData.CoreLanguage.Collections;
using GameData.RunTime.Common;
using GameData.RunTime.NightSceneUtility;

namespace MystiaGuestTag;

/// <summary>
/// 纯集合比对推荐器（v1.4.0 起唯一的推荐实现，旧 RecipeRecommender / JsonRecommender 已删除）。
///
/// 设计红线（用户定的，代码里不许违背）：
///   1. 不发明公式：没有任何加权、加减分、相似度、概率模型。唯一的“数”是「命中集合的元素个数」，
///      以及「价格」这两个数据字段本身。
///   2. 先过滤再排序：含厌恶标签的菜 / 酒在进入排序池之前就被剔除，压根没有参评资格。
///   3. 绝不兜底推厌恶：过滤后没有符合条件的目标就返回「无匹配」，不使用“最接近/最便宜”之类凑数。
///   4. 状态隔离：每次算一位稀客都从全新的局部集合开始，没有任何跨客人共享的候选缓存，
///      入口处还会 Reset() 清掉上一位留下的诊断状态，防止数据串号。
///   5. 数据只来自字典：稀客喜好取 GuestPreferenceData.json（likeTags / hateTags / beverageTags），
///      菜取 RecipeData.json（tags / negativeTags / price），酒取 DrinkData.json（tags / price）。
///
/// 比对一律用「标签名字符串」而不是 id：游戏里菜品标签和酒水标签是两套 id 命名空间，
/// 同一个 id 在两边名字不同，用 id 比会把结果弄脏。
/// </summary>
internal static class SetMatchRecommender
{
    /// <summary>面板上「点单还没出来」时显示的文字。</summary>
    internal const string WaitingText = "等待稀客点单…";

    /// <summary>推荐结果标签：集合比对命中。</summary>
    private const string LabelSetMatch = "喜好命中";

    /// <summary>兜底档标签：只保证“带点单口味 + 不含厌恶”，排序提示用琥珀色。</summary>

    /// <summary>推荐结果标签：过滤完什么都不剩。</summary>
    private const string LabelNoMatch = "无匹配";

    /// <summary>诊断日志去重：同一位稀客、同样的输入只打一次。</summary>
    private static string _lastSignature = string.Empty;

    /// <summary>上一次打集合匹配日志的时间（unscaled）。配合签名一起做节流。</summary>
    private static float _lastLogTime = -999f;

    /// <summary>
    /// 清空跨次调用的诊断状态（用户要求：每次算新客人前重置，避免上一轮数据串号）。
    /// 本类不缓存任何候选列表，这里清的是日志去重签名。
    /// </summary>
    internal static void Reset()
    {
        _lastSignature = string.Empty;
    }

    /// <summary>兼容旧调用点（面板切换策略 / 设置面板会用 ClearCache 触发重算）。</summary>
    internal static void ClearCache()
    {
        Reset();                                   // 清日志去重签名（O(1)）
        _gameRecipeTags = null;                    // m08103：只断开引用，交给 GC 自己回收——不在切场景那一帧遍历 200 道菜做清理
    }

    // ---------------------------------------------------------------------
    // 结果结构（原来是 RecipeRecommender 内的嵌套类型，现在搬到这里）
    // ---------------------------------------------------------------------

    /// <summary>一条推荐结果：Items 是显示用文本，Label 说明这条结果是怎么来的。</summary>
    internal readonly struct RecommendationResult
    {
        private RecommendationResult(string[] items, bool hasPreference, string label)
        {
            Items = items ?? Array.Empty<string>();
            HasPreference = hasPreference;
            Label = label ?? string.Empty;
        }

        public string[] Items { get; }

        /// <summary>这位稀客在数据里到底有没有该类喜好（没有 = 显示「无明显偏好」）。</summary>
        public bool HasPreference { get; }

        public string Label { get; }

        public static RecommendationResult NoPreference => new(Array.Empty<string>(), false, string.Empty);

        public static RecommendationResult NoMatch => new(Array.Empty<string>(), true, LabelNoMatch);

        public static RecommendationResult With(string[] items, string label) => new(items, true, label);
    }

    // ---------------------------------------------------------------------
    // 内部工作类型
    // ---------------------------------------------------------------------

    /// <summary>一位稀客这次比对用的偏好集合（全部是标签名）。</summary>
    private sealed class Preferences
    {
        public string Name = string.Empty;
        public HashSet<string> Likes = new(StringComparer.Ordinal);
        public HashSet<string> Hates = new(StringComparer.Ordinal);
        public HashSet<string> BeverageLikes = new(StringComparer.Ordinal);
    }

    /// <summary>排序池里的一个候选（菜或酒）。</summary>
    private sealed class Candidate
    {
        public string Name = string.Empty;
        public int Price;
        public int HitCount;

        /// <summary>在 JSON 文件里的原始下标，用来做最后一级稳定排序。</summary>
        public int SourceIndex;

        public List<string> HitTags = new();

        /// <summary>这道菜今晚在菜单上（只用于同分时靠前，不参与淘汰）。</summary>
        public bool InMenu;

        /// <summary>完全满足客人今天点单口味（符卡料理候选）：排序第一关键字，且豁免厌恶拦截。</summary>
        public bool IsOrderDish;
        public int[] CookerTypes = Array.Empty<int>();                       // 厨具 id 列表（显示 [厨具: 煮锅]）

        /// <summary>完美加料的规划结果（主料文本 / 加料文本 / 是否完美）。</summary>
        public AddOnPlanner.Plan AddOns;
    }

    /// <summary>过滤统计，只用于日志，不参与任何决策。</summary>
    private sealed class FilterStats
    {
        public int Total;
        public int SkippedByHate;
        public int OrderDish;
        public int SkippedByBudget;
        public int SkippedByZeroHit;
        public int SkippedByStock;
        public int SkippedByCooker;
        public int SkippedByLiveHate;
        public int Kept;

        public override string ToString()
        {
            return $"候选 {Total}，点单菜 {OrderDish}，厌恶剔除 {SkippedByHate}，" +
                   $"超预算 {SkippedByBudget}，零命中 {SkippedByZeroHit}，缺主料 {SkippedByStock}，" +
                   $"缺厨具 {SkippedByCooker}，游戏侧厌恶 {SkippedByLiveHate}，保留 {Kept}";
        }
    }

    // ---------------------------------------------------------------------
    // 对外入口
    // ---------------------------------------------------------------------

    /// <summary>
    /// 算一位稀客的推荐菜 + 推荐酒水。
    /// 任何异常 / 数据缺失都会走兜底文本，绝不抛出去把面板打崩。
    /// </summary>
    internal static void Recommend(
        SpecialGuest? guest,
        string stringId,
        string displayName,
        int budget,
        int requestFoodTag,
        bool orderKnown,
        int requestBeverageTag,
        out RecommendationResult food,
        out RecommendationResult beverage)
    {
        // 每次进来先清掉上一轮的诊断状态，再从空集合开始，保证不同客人之间不共享任何东西。
        Reset();
        food = RecommendationResult.NoMatch;
        beverage = RecommendationResult.NoMatch;

        // 第 0 步：点单气泡还没出现时，什么都不猜。
        if (!orderKnown)
        {
            food = RecommendationResult.With(new[] { WaitingText }, "等待点单");
            beverage = RecommendationResult.With(new[] { WaitingText }, "等待点单");
            return;
        }

        // 第 1 步：数据没加载就直说，不拿别的算法顶替。
        if (!JsonDataStore.Ready)
        {
            food = RecommendationResult.With(new[] { "本地数据未加载，暂不推荐" }, "数据缺失");
            beverage = RecommendationResult.With(new[] { "本地数据未加载，暂不推荐" }, "数据缺失");
            return;
        }

        // 第 2 步：定位这位稀客在字典里的偏好；找不到就明说，不猜。
        var pref = ResolvePreferences(guest, stringId, displayName);
        if (pref == null)
        {
            food = RecommendationResult.With(new[] { "数据未收录（" + displayName + "）" }, "未收录");
            beverage = RecommendationResult.With(new[] { "数据未收录（" + displayName + "）" }, "未收录");
            return;
        }

        // 第 3 步：准备输入。全部是局部变量，计算完即丢。
        // 说明：本插件只为稀客（SpecialGuest）服务——面板里的每一条都来自 SpecialGuestsController，
        // 普通妖怪根本不会进到这里，所以不存在「普通客人没有喜好数据」的分支要处理。
        // 推荐池：只推「已解锁菜谱 ∪ 今晚菜单」；两者都读不到就本轮不推荐（绝不退回全量 200 道，m07784）。
        var pool = ResolveRecipePool();
        var orderFoodName = requestFoodTag > 0 ? JsonDataStore.TagName(requestFoodTag) : string.Empty;
        var orderBeverageName = requestBeverageTag > 0 ? JsonDataStore.BeverageTagName(requestBeverageTag) : string.Empty;
        var foodLimit = Math.Max(1, GuestTagConfig.GetMaxRecipes());
        var drinkLimit = Math.Max(1, GuestTagConfig.GetMaxDrinks());

        // 第 4 步：菜——先硬过滤，再按命中数排序。
        var foodStats = new FilterStats();
        var liveHates = ReadLiveHateNames(guest);
        var foods = RankFoods(pref, pool, orderFoodName, budget, foodLimit, foodStats, liveHates);

        // 没有「放宽档」：菜必须至少命中一条喜好标签，否则不推。
        // （2026-11-03 用户投诉「土豆可乐饼/冬甩这种标签完全不沾边的菜也被推出来，还硬加完美」，
        //   根因就是这里原来的零命中兜底档。宁可显示无匹配，也不推做出来不讨喜的菜。）
        if (foods.Count == 0)
        {
            Diag.DbgSb(() => $"[GuestTag] 无可推菜：{foodStats}");
        }

        // 第 5 步：酒——同一套流程，独立过滤、独立排序。
        var drinkStats = new FilterStats();
        var drinks = RankDrinks(pref, orderBeverageName, budget, drinkLimit, drinkStats);

        // 第 6 步：把酒按位配到菜上，只做「菜价 + 酒价 ≤ 预算」这一条硬校验。
        var pairedDrinks = PairDrinks(foods, drinks, budget);

        // 第 7 步：组装显示文本。
        if (foods.Count == 0)
        {
            // 没有不踩雷的菜：绝不拿厌恶菜凑数。
            var lonelyDrinks = new string[drinks.Count];
            for (var i = 0; i < drinks.Count; i++)
            {
                lonelyDrinks[i] = FormatItem(drinks[i]);
            }

            food = RecommendationResult.With(
                new[] { "无匹配（没有既合口味又不踩雷的菜）" }, LabelNoMatch);
            beverage = lonelyDrinks.Length == 0
                ? RecommendationResult.NoMatch
                : RecommendationResult.With(lonelyDrinks, LabelSetMatch);
        }
        else
        {
            var foodItems = new string[foods.Count];
            for (var i = 0; i < foods.Count; i++)
            {
                foodItems[i] = FormatItem(foods[i]);
            }

            food = RecommendationResult.With(foodItems, LabelSetMatch);
            beverage = RecommendationResult.With(pairedDrinks, LabelSetMatch);
        }

        // 第 8 步：诊断日志（只在内容变化时打一次）。
        LogOnce(pref.Name, budget, orderFoodName, orderBeverageName, foodStats, drinkStats, foods, drinks);
    }

    // ---------------------------------------------------------------------
    // 偏好解析
    // ---------------------------------------------------------------------

    private static Preferences? ResolvePreferences(SpecialGuest? guest, string stringId, string displayName)
    {
        try
        {
            var gameId = 0;
            try
            {
                if (guest != null) gameId = guest.Id;
            }
            catch
            {
                gameId = 0;
            }

            var def = JsonDataStore.FindGuest(gameId, displayName);
            if (def == null)
            {
                Diag.Dbg(
                    $"[GuestTag] 集合匹配：字典里没有「{displayName}」（stringId={stringId}，id={gameId}），本次不推荐。");
                return null;
            }

            var pref = new Preferences { Name = def.Name };
            foreach (var tag in def.LikeTags)
            {
                if (!string.IsNullOrEmpty(tag)) pref.Likes.Add(tag);
            }

            foreach (var tag in def.HateTags)
            {
                if (!string.IsNullOrEmpty(tag)) pref.Hates.Add(tag);
            }

            foreach (var tag in def.BeverageTags)
            {
                if (!string.IsNullOrEmpty(tag)) pref.BeverageLikes.Add(tag);
            }

            return pref;
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"[GuestTag] 集合匹配：读稀客偏好失败 {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // 菜：过滤 + 排序
    // ---------------------------------------------------------------------

    /// <summary>
    /// 第 4 步的完整实现：
    ///   a) 点单菜识别：菜自带客人今天点单的口味 → 标记为符卡料理候选（豁免厌恶拦截）；
    ///   b) 厌恶拦截（菜自身 tags ∪ negativeTags 与稀客 hateTags 有交集 → 剔除），只对非点单菜生效；
    ///   c) 游戏侧厌恶二次校验（DataBaseCore 实时标签），同样只对非点单菜生效；
    ///   d) 菜价不得超过预算（点单菜也照样要买得起）；
    ///   e) 与稀客 likeTags 的交集为空 → 剔除，但点单菜不受限（不为凑数推荐零命中的日常菜）；
    ///   f) 留下的按「点单菜 → 完美加料 → 命中数 → 今晚菜单 → 价格 → JSON 原序」排序，取前 limit 道。
    /// </summary>
    private static List<Candidate> RankFoods(
        Preferences pref,
        HashSet<string>? pool,
        string orderFoodName,
        int budget,
        int limit,
        FilterStats stats,
        HashSet<string>? liveHates)
    {
        var kept = new List<Candidate>();
        var menuNames = ReadTonightMenuNames();
        _stockKnown = StockKnown;                                             // 本轮库存是否可读（只算一次）

        var recipes = JsonDataStore.Recipes;
        for (var index = 0; index < recipes.Count; index++)
        {
            var recipe = recipes[index];                                        // 当前这道菜
            if (recipe == null || string.IsNullOrEmpty(recipe.Name)) continue;  // 空条目 / 没名字：跳过

            // ① 池子门槛：必须是玩家**已解锁**的菜谱（m08324：不并今晚菜单、不退回全量）；pool 为 null 仅限配置 All
            if (pool != null && !pool.Contains(recipe.Name)) continue;          // 不在池子里：剔除

            // ② 库存门槛：主料一样都不能缺。
            //    读得到背包 → 严格按数量比（缺一样就剔除）；
            //    读不到背包 → 整轮不推（m08324：不准用今日菜单兜底，也绝不把全量菜谱放过去，
            //    否则就会出现 m07784 那种「和牛 / 鹿肉」虚空画饼）。
            if (GuestTagConfig.RequireIngredients.Value)                        // 需要库存校验
            {
                // 背包读不到时不再退回「今晚菜单」那套兜底（用户 m08324：不准用今日菜单兜底），
                // 直接整轮不推——宁可少推，也不推任何没把握做出来的菜。
                if (!StockKnown)                                                 // 背包读不到
                {
                    stats.SkippedByStock++;                                     // 统计：库存未知
                    if (!_stockUnknownLogged)                                    // 只记一次日志
                    {
                        _stockUnknownLogged = true;                              // 标记已记
                        Plugin.Logger.LogInfo($"[GuestTag] 读不到食材库存 → 本轮不推荐（不退回今日菜单）"); // 说明原因
                    }                                                            // 日志结束
                    continue;                                                    // 剔除
                }                                                                // 库存未知分支结束

                if (!HasMainIngredients(recipe))                                 // 主料缺任何一样
                {
                    stats.SkippedByStock++;                                     // 统计：缺主料
                    continue;                                                   // 剔除
                }                                                               // 缺料结束
            }                                                                   // 库存门槛结束

            // ②' 厨具门槛（m08215）：这道菜要求的厨具，今晚厨房里必须至少摆着一种。
            //    读不到厨具时 IsAnyAvailable 会返回 true（放行），绝不因为读失败清空整张菜单。
            if (recipe.CookerTypes.Length > 0 && !CookwareFilter.IsAnyAvailable(recipe.CookerTypes))
            {
                stats.SkippedByCooker++;                                        // 统计：缺厨具
                continue;                                                       // 剔除：这台厨具不在场
            }

            stats.Total++;                                                      // 统计：进入过滤的候选数

            // ③ 最高优先级：这道菜是不是客人今天点名要的口味？
            //    是的话它就是「符卡料理」候选——**跳过一切厌恶拦截**，直接放行（官方规则：点单要求高于个人好恶）。
            var isOrderDish = orderFoodName.Length > 0 && Contains(recipe.Tags, orderFoodName); // 满足点单要求

            // ④ 喜好命中：只做交集、只数个数，不做任何加权（排序关键字，两支都要用）
            var hitTags = Intersect(recipe.Tags, pref.Likes);                   // 菜的标签 ∩ 稀客喜好

            // ⑤ 分流：点单菜 / 备用菜。
            //    游戏机制（用户 m07539 核实）：稀客坐下必定带点单气泡，所以「点单要求」永远存在，
            //    而且优先级最高——命中了就是她今天想吃的，哪怕菜里有她平时的厌恶标签也照样端上去；
            //    没命中点单要求的菜只是「备用菜」，这时才去查她讨厌什么。
            if (!isOrderDish)                                                   // 备用菜：严格查厌恶
            {
                // ⑤-a 备用菜 · JSON 侧厌恶：菜自身 tags ∪ 黑色词条 negativeTags 命中厌恶就淘汰
                if (HitsAny(recipe.Tags, pref.Hates) || HitsAny(recipe.NegativeTags, pref.Hates))
                {
                    stats.SkippedByHate++;                                      // 统计：JSON 侧厌恶
                    continue;                                                   // 剔除
                }

                // ⑤-b 备用菜 · 游戏侧实时标签二次校验，防本地 JSON 过期
                if (liveHates != null && liveHates.Count > 0 &&                 // 有游戏侧厌恶数据
                    GameRecipeTags(recipe.Name) is { Count: > 0 } gameTags &&   // 这道菜在游戏里查得到标签
                    gameTags.Overlaps(liveHates))                               // 与游戏侧厌恶有交集
                {
                    stats.SkippedByLiveHate++;                                  // 统计：游戏侧厌恶
                    continue;                                                   // 剔除
                }

                // ⑤-c 备用菜 · 一个喜好都没命中就别推（不为凑数硬推）
                if (hitTags.Count == 0)
                {
                    stats.SkippedByZeroHit++;                                   // 统计：零命中
                    continue;                                                   // 剔除
                }
            }
            // 点单菜走到这里：厌恶不查、喜好命中数不设门槛，只在下面查预算——她今天点名要的，先满足她。
            if (isOrderDish) stats.OrderDish++;                                 // 统计：点单菜直通的数量

            // ⑥ 预算：单价超过钱包的直接出局（budget <= 0 视为不限）——点单菜也要买得起
            if (budget > 0 && recipe.Price > budget)                            // 超预算
            {
                stats.SkippedByBudget++;                                        // 统计：超预算
                continue;                                                       // 剔除
            }                                                                   // 预算门槛结束

            // ⑨ 完美加料：点单菜允许加她讨厌的料（只要不产生黑暗物质），一切为了做成符卡料理
            var addOns = AddOnPlanner.Compute(recipe, pref.Likes, pref.Hates, isOrderDish); // 主料 / 加料 / 是否完美

            kept.Add(new Candidate                                              // 入选
            {
                Name = recipe.Name,                                             // 菜名
                Price = recipe.Price,                                           // 价格
                HitCount = hitTags.Count,                                       // 命中数（排序第二关键字）
                SourceIndex = index,                                            // JSON 原序（最后兜底）
                HitTags = hitTags,                                              // 命中的标签（日志用）
                InMenu = menuNames != null && menuNames.Contains(recipe.Name),  // 是否在今晚菜单（排序加分）
                AddOns = addOns,                                                // 加料规划
                IsOrderDish = isOrderDish,                                      // 是否点单菜（排序第一关键字）
                CookerTypes = recipe.CookerTypes,                               // 这道菜要用哪些厨具（显示用）
            });                                                                 // 入选结束
            stats.Kept++;                                                       // 统计：保留数
        }                                                                       // 遍历结束

        // f) 排序：完美加料优先 → 命中数降序 → 今晚菜单靠前 → 价格升序 → JSON 原顺序。
        kept.Sort(CompareCandidates);
        return TakeFront(kept, limit);
    }

    // ---------------------------------------------------------------------
    // 酒：过滤 + 排序
    // ---------------------------------------------------------------------

    /// <summary>
    /// 第 5 步：酒跟菜用同一套规则，只是「喜好集合」换成 beverageTags、点单要求换成酒水标签。
    /// 注意：稀客的 hateTags 里可能有只属于菜的标签（如“重油”），酒水里没有同名标签，
    /// 交集自然是空，不会误杀。
    /// </summary>
    private static List<Candidate> RankDrinks(
        Preferences pref,
        string orderBeverageName,
        int budget,
        int limit,
        FilterStats stats)
    {
        var kept = new List<Candidate>();

        var drinks = JsonDataStore.Drinks;
        for (var index = 0; index < drinks.Count; index++)
        {
            var drink = drinks[index];
            if (drink == null || string.IsNullOrEmpty(drink.Name)) continue;

            stats.Total++;

            if (HitsAny(drink.Tags, pref.Hates))
            {
                stats.SkippedByHate++;
                continue;
            }

            if (orderBeverageName.Length > 0 && !Contains(drink.Tags, orderBeverageName))
            {
                continue;
            }

            if (budget > 0 && drink.Price > budget)
            {
                stats.SkippedByBudget++;
                continue;
            }

            var hitTags = Intersect(drink.Tags, pref.BeverageLikes);
            if (hitTags.Count == 0)
            {
                stats.SkippedByZeroHit++;
                continue;
            }

            kept.Add(new Candidate
            {
                Name = drink.Name,
                Price = drink.Price,
                HitCount = hitTags.Count,
                SourceIndex = index,
                HitTags = hitTags,
            });
            stats.Kept++;
        }

        kept.Sort(CompareCandidates);
        return TakeFront(kept, limit);
    }

    // ---------------------------------------------------------------------
    // 菜 + 酒配对
    // ---------------------------------------------------------------------

    /// <summary>
    /// 第 6 步：给每道推荐菜配一杯酒，硬条件只有「菜价 + 酒价 ≤ 预算」。
    /// 配不上就留空字符串（面板那一行只显示菜），不用别的酒硬顶。
    /// </summary>
    private static string[] PairDrinks(List<Candidate> foods, List<Candidate> drinks, int budget)
    {
        var result = new string[foods.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = string.Empty;
        }

        if (foods.Count == 0 || drinks.Count == 0) return result;

        var used = new bool[drinks.Count];
        for (var i = 0; i < foods.Count; i++)
        {
            var dish = foods[i];
            for (var j = 0; j < drinks.Count; j++)
            {
                if (used[j]) continue;

                var drink = drinks[j];
                var total = dish.Price + drink.Price;
                if (budget > 0 && total > budget) continue;

                used[j] = true;
                result[i] = FormatItem(drink);
                break;
            }
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // 小工具（全部按名字字符串比对）
    // ---------------------------------------------------------------------

    private static bool Contains(string[]? tags, string name)
    {
        if (tags == null) return false;
        foreach (var tag in tags)
        {
            if (string.Equals(tag, name, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool HitsAny(string[]? tags, HashSet<string> set)
    {
        if (tags == null || set.Count == 0) return false;
        foreach (var tag in tags)
        {
            if (tag != null && set.Contains(tag)) return true;
        }

        return false;
    }

    private static List<string> Intersect(string[]? tags, HashSet<string> set)
    {
        var hit = new List<string>();
        if (tags == null || set.Count == 0) return hit;

        foreach (var tag in tags)
        {
            if (tag == null) continue;
            if (!set.Contains(tag)) continue;
            if (hit.Contains(tag)) continue;
            hit.Add(tag);
        }

        return hit;
    }

    private static int CompareCandidates(Candidate a, Candidate b)
    {
        // 0) 最高优先级：今天的点单菜（符卡料理）永远排最前面——它才是客人这局真正想要的。
        var byOrder = b.IsOrderDish.CompareTo(a.IsOrderDish);
        if (byOrder != 0) return byOrder;

        // 1) 能做出完美加料的菜一律排在最前面（用户要求：完美料理优先）。
        var byPerfect = b.AddOns.Perfect.CompareTo(a.AddOns.Perfect);
        if (byPerfect != 0) return byPerfect;

        // 2) 喜好命中数降序。
        var byHit = b.HitCount.CompareTo(a.HitCount);
        if (byHit != 0) return byHit;

        // 3) 今晚菜单上的菜靠前（只影响同分顺序，不淘汰任何菜）。
        var byMenu = b.InMenu.CompareTo(a.InMenu);
        if (byMenu != 0) return byMenu;

        // 4) 价格升序，最后按 JSON 原顺序稳定排序。
        var byPrice = a.Price.CompareTo(b.Price);
        if (byPrice != 0) return byPrice;

        return a.SourceIndex.CompareTo(b.SourceIndex);
    }

    private static List<Candidate> TakeFront(List<Candidate> source, int limit)
    {
        if (source.Count <= limit) return source;

        var trimmed = new List<Candidate>(limit);
        for (var i = 0; i < limit; i++)
        {
            trimmed.Add(source[i]);
        }

        return trimmed;
    }

    private static string FormatItem(Candidate candidate)
    {
        var head = candidate.Price > 0
            ? candidate.Name + " (¥" + candidate.Price + ")"
            : candidate.Name;

        if (!candidate.AddOns.HasMain && !candidate.AddOns.HasExtra) return head;

        var builder = new System.Text.StringBuilder(head);

        // 厨具：紧跟价格后面，用最简的中文短名，多个厨具用 / 连起来，例如 [厨具: 煮锅]
        if (candidate.CookerTypes != null && candidate.CookerTypes.Length > 0)
        {
            var names = new List<string>();                                   // 收集能翻译出来的厨具名
            foreach (var id in candidate.CookerTypes)                         // 逐个 id
            {
                var name = CookerName(id);                                    // 翻成中文
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name); // 去重后收下
            }
            if (names.Count > 0) builder.Append(" [厨具: ").Append(string.Join("/", names)).Append(']');
        }

        if (candidate.AddOns.HasMain) builder.Append(" [").Append(candidate.AddOns.MainText).Append(']');
        if (candidate.AddOns.HasExtra)
        {
            builder.Append(" [加料: ").Append(candidate.AddOns.ExtraText);
            if (candidate.AddOns.Perfect) builder.Append(" (完美)");
            builder.Append(']');
        }

        return builder.ToString();
    }

    /// <summary>背包库存这轮是否读得到（读不到就只能信今晚菜单）。</summary>
    private static bool _stockKnown;
    private static bool _stockUnknownLogged; // 库存读不到时只提示一次（m08324）

    private static bool StockKnown
    {
        get
        {
            try
            {
                var stock = RunTimeStorage.Ingredients;                      // 游戏静态字典：食材 id → 数量
                return stock != null && stock.Count > 0;                     // 有内容才算「读得到」
            }
            catch
            {
                return false;                                                // 任何异常都当读不到
            }
        }
    }

    /// <summary>把厨具 id 翻成中文短名（游戏 CookerType：1=煮锅 2=烧烤架 3=油锅 4=蒸锅 5=料理台）。</summary>
    private static string CookerName(int id) => id switch
    {
        1 => "煮锅",
        2 => "烧烤架",
        3 => "油锅",
        4 => "蒸锅",
        5 => "料理台",
        _ => string.Empty,
    };

    private static Dictionary<string, HashSet<string>>? _gameRecipeTags;

    /// <summary>
    /// 读某一页菜名对应的游戏原始标签（走 DataBaseCore.Recipes → Recipe.Food.Tags → GetFoodTag）。
    /// 只建一次表，读失败返回空集合（调用方会跳过二次校验，不影响主流程）。
    /// </summary>
    private static HashSet<string> GameRecipeTags(string recipeName)
    {
        try
        {
            if (_gameRecipeTags == null)
            {
                var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var recipes = DataBaseCore.Recipes;
                if (recipes != null)
                {
                    foreach (var kv in recipes)
                    {
                        var recipe = kv.Value;
                        if (recipe == null) continue;
                        string? name = null;
                        try
                        {
                            name = recipe.Food?.Text?.Name;
                        }
                        catch
                        {
                            name = null;
                        }

                        if (string.IsNullOrEmpty(name)) continue;

                        var tags = new HashSet<string>(StringComparer.Ordinal);
                        try
                        {
                            var ids = recipe.Food?.Tags;
                            if (ids != null)
                            {
                                foreach (var id in ids)
                                {
                                    var tagName = DataBaseLanguage.GetFoodTag(id);
                                    if (!string.IsNullOrEmpty(tagName)) tags.Add(tagName);
                                }
                            }
                        }
                        catch
                        {
                            // 标签读不出来就留空集合，后面会跳过而不是误杀。
                        }

                        map[name] = tags;
                    }
                }

                _gameRecipeTags = map;
                Diag.DbgSb(() => $"[GuestTag] 游戏侧菜谱标签表：{map.Count} 道");
            }

            return _gameRecipeTags.TryGetValue(recipeName, out var t) ? t : new HashSet<string>();
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"[GuestTag] 读游戏侧菜谱标签失败：{ex.GetType().Name}: {ex.Message}");
            _gameRecipeTags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            return new HashSet<string>();
        }
    }

    /// <summary>读稀客的实时厌恶标签名（HateFoodTagOriginal → GetFoodTag）。读不到返回空集合。</summary>
    private static HashSet<string>? ReadLiveHateNames(SpecialGuest? guest)
    {
        try
        {
            if (guest == null) return null;
            var ids = guest.HateFoodTagOriginal;
            if (ids == null || ids.Length == 0) return null;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var name = DataBaseLanguage.GetFoodTag(id);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            return names.Count > 0 ? names : null;
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"[GuestTag] 读实时厌恶标签失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 推荐池解析。顺序：Auto = 已解锁菜谱（RunTimeStorage.Recipes）→ 今晚菜单 → 返回 null（全量）。
    /// 池子为 null 时调用方才会退回全量菜谱。
    /// </summary>
    /// <summary>
    /// 决定「哪些菜有资格进推荐池」。顺序：
    ///   All     → 全量（明确允许推没解锁的菜，默认不用）；
    ///   Tonight → 只今晚菜单；
    ///   Auto    → 已解锁菜谱（RunTimeStorage.Recipes）∪ 今晚菜单；读不到已解锁表就退今晚菜单；再读不到才退全量。
    /// </summary>
    private static HashSet<string>? ResolveRecipePool()
    {
        var mode = GuestTagConfig.RecipePoolMode.Value?.Trim() ?? "Auto";   // 读配置：Auto / Tonight / All

        if (string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase))  // 配置成 All：不限制（仅调试用）
        {
            LogPoolOnce("All（全菜谱，会推出没解锁的菜）");                   // 只记一次日志
            return null;                                                    // null = 不做池子过滤
        }                                                                   // All 分支结束

        var tonight = ReadTonightMenuNames();                               // 今晚菜单（玩家自己备好的菜，必然做得出）

        if (string.Equals(mode, "Tonight", StringComparison.OrdinalIgnoreCase)) // 配置成 Tonight：只今晚菜单
        {
            if (tonight != null)                                            // 读到了
            {
                LogPoolOnce($"Tonight（今晚菜单 {tonight.Count} 道）");        // 报数量
                return tonight;                                             // 只在这个池子里挑
            }                                                               // 读到结束

            LogPoolOnce("Tonight 但今晚菜单读不到 → 本轮不推荐（不再退回全量）"); // 绝不放全量
            return new HashSet<string>(StringComparer.Ordinal);             // 空池子：一道都不推
        }                                                                   // Tonight 分支结束

        // Auto 模式（用户 m08324 明确要求）：只认游戏底层「已解锁菜谱」，
        // 不再把今晚菜单并进来兜底——宁可少推，也不推任何没解锁的菜。
        var unlocked = ReadUnlockedRecipeNames();                           // 读游戏底层「已解锁菜谱」
        if (unlocked == null) unlocked = new HashSet<string>(StringComparer.Ordinal); // 读不到就当空集合

        if (unlocked.Count > 0)                                             // 有池子
        {
            LogPoolOnce($"Auto（已解锁 {unlocked.Count} 道，仅已解锁，不用今晚菜单兜底）"); // 记录池子大小
            return unlocked;                                                // 用它
        }                                                                   // 有池子结束

        // 已解锁表读不到：按用户要求本轮不推荐，绝不退回今晚菜单、也绝不退回全量。
        LogPoolOnce("读不到已解锁菜谱表 → 本轮不推荐（不退回今晚菜单、不退回全量）");
        return new HashSet<string>(StringComparer.Ordinal);                  // 空池子
    }

    private static string _lastPoolLog = string.Empty;

    private static void LogPoolOnce(string text)
    {
        if (string.Equals(_lastPoolLog, text, StringComparison.Ordinal)) return;
        _lastPoolLog = text;
        Plugin.Logger.LogInfo($"[GuestTag] 推荐池：{text}");
    }

    /// <summary>读玩家已解锁的菜谱名集合（RunTimeStorage.Recipes 里存的就是菜谱 id）。</summary>
    private static HashSet<string>? ReadUnlockedRecipeNames()
    {
        try                                                                 // 读游戏底层失败也不能崩
        {
            var ids = RunTimeStorage.Recipes;                               // 游戏静态字段：已解锁菜谱 id 列表
            if (ids == null || ids.Count == 0) return null;                 // 读不到 / 空表：返回 null 让调用方兜底

            var idSet = new HashSet<int>();                                 // id 去重集合
            foreach (var id in ids) idSet.Add(id);                          // 逐个加入
            if (idSet.Count == 0) return null;                              // 一条都没转进来：当读不到

            var names = new HashSet<string>(StringComparer.Ordinal);        // 菜名集合（大小写敏感，和 JSON 一致）
            foreach (var recipe in JsonDataStore.Recipes)                   // 遍历本地菜谱表
            {
                if (recipe != null && idSet.Contains(recipe.Id)) names.Add(recipe.Name); // id 命中就用 JSON 里的中文名
            }                                                               // 遍历结束

            return names.Count > 0 ? names : null;                          // 一个名都没映射出来：当读不到
        }                                                                   // try 结束
        catch (Exception ex)                                                // native 调用异常
        {
            Diag.DbgSb(() => $"[GuestTag] 读已解锁菜谱失败：{ex.GetType().Name}: {ex.Message}"); // 只记 debug
            return null;                                                    // 让调用方退回今晚菜单/全量
        }                                                                   // catch 结束
    }

    /// <summary>
    /// 主料是否齐全：按 RunTimeStorage.Ingredients（背包食材 id→数量）逐项核对。
    /// 库存读不到（字典空 / 抛异常）时一律返回 true，绝不因为读不到库存就不推。
    /// </summary>
    private static bool HasMainIngredients(JsonDataStore.RecipeDef recipe)
    {
        try                                                                 // native 读取异常保护
        {
            var stock = RunTimeStorage.Ingredients;                         // 游戏静态字段：背包食材 id→数量
            if (stock == null || stock.Count == 0) return true;             // 读不到库存：放行
            if (recipe.MainIngredients == null || recipe.MainIngredients.Length == 0) return true; // 这道菜没主料：放行

            foreach (var item in recipe.MainIngredients)                    // 逐样主料核对
            {
                if (item == null) continue;                                 // 空条目跳过
                var need = item.Count <= 0 ? 1 : item.Count;                // 需求数量，<=0 当 1
                if (!stock.TryGetValue(item.Id, out var have) || have < need) return false; // 没有或不够：缺主料
            }                                                               // 核对结束

            return true;                                                    // 全齐
        }                                                                   // try 结束
        catch                                                               // 任何异常
        {
            return true;                                                    // 读不到就当齐全，宁多推也不误杀
        }                                                                   // catch 结束
    }

    /// <summary>读今晚菜单上的菜名集合；读不到返回 null（调用方会退回全菜谱表）。</summary>
    private static HashSet<string>? ReadTonightMenuNames()
    {
        try
        {
            var configure = IzakayaConfigure.Instance;
            var daily = configure?.DailyRecipes;
            if (daily == null || daily.Count == 0) return null;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in daily)
            {
                if (recipe == null) continue;
                try
                {
                    var name = recipe.Food?.Text?.Name;
                    if (!string.IsNullOrEmpty(name)) names.Add(name!);
                }
                catch
                {
                    // 单道菜读失败就跳过，不影响其它菜
                }
            }

            return names.Count > 0 ? names : null;
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"[GuestTag] 读当晚菜单失败，改用全菜谱表：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>诊断日志：同签名只打一次，避免每帧刷屏。</summary>
    private static void LogOnce(
        string guestName,
        int budget,
        string orderFoodName,
        string orderBeverageName,
        FilterStats foodStats,
        FilterStats drinkStats,
        List<Candidate> foods,
        List<Candidate> drinks)
    {
        try
        {
            // 预算是会随着他花钱一直变小的小数，直接进签名会变成每买一次就刷一条日志；
            // 这里按 50 块一档取整，并加 10 秒冷却，保证一个晚上每位稀客最多几行。
            var signature = guestName + "|b" + (budget / 50) + "|" + orderFoodName + "|" + orderBeverageName + "|" +
                            foods.Count + "|" + drinks.Count;
            var now = UnityEngine.Time.unscaledTime;
            if (string.Equals(signature, _lastSignature, StringComparison.Ordinal) &&
                now - _lastLogTime < 10f) return;
            _lastSignature = signature;
            _lastLogTime = now;

            var pickedFoods = new List<string>();
            foreach (var item in foods) pickedFoods.Add(item.Name + "×" + item.HitCount);

            var pickedDrinks = new List<string>();
            foreach (var item in drinks) pickedDrinks.Add(item.Name + "×" + item.HitCount);

            Diag.Dbg(
                $"[GuestTag] 集合匹配 [{guestName}] 预算 {budget}，点单菜={orderFoodName}，点单酒={orderBeverageName}；" +
                $"菜：{foodStats}；酒：{drinkStats}；" +
                $"入选菜 {(pickedFoods.Count == 0 ? "无" : string.Join("、", pickedFoods))}；" +
                $"入选酒 {(pickedDrinks.Count == 0 ? "无" : string.Join("、", pickedDrinks))}");
        }
        catch
        {
            // 日志失败绝不影响推荐
        }
    }
}
