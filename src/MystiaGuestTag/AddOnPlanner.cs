using System;                             // 基础类型（Math / StringComparer）
using System.Collections.Generic;         // HashSet / Dictionary / List
using System.Text;                        // StringBuilder

namespace MystiaGuestTag                  // 插件自己的命名空间
{
    /// <summary>
    /// 完美加料规划器。输入：一道菜 + 稀客喜好集合 + 稀客厌恶集合；输出：主料文本 / 加料文本 / 是否完美。
    /// 全程只有集合运算（交集、计数），没有任何加权、打分、概率。
    /// </summary>
    internal static class AddOnPlanner     // 静态类：只有静态缓存，没有实例
    {
        /// <summary>规划结果。</summary>
        internal readonly struct Plan      // readonly struct：值类型、字段不可改
        {
            public readonly string MainText;   // "主料: 猪肉x1+海苔x1"，读不到就是空串
            public readonly string ExtraText;  // "鸡蛋、可可豆"，没有可加的就是空串
            public readonly bool Perfect;      // 是否够“完美”（规则见 ComputeCore 末尾）

            public Plan(string mainText, string extraText, bool perfect) // 构造函数
            {
                MainText = mainText ?? string.Empty;    // null → 空串
                ExtraText = extraText ?? string.Empty;  // null → 空串
                Perfect = perfect;                      // 直接赋值
            }                                           // 构造函数结束

            public bool HasMain => !string.IsNullOrEmpty(MainText);    // UI 是否要画主料行
            public bool HasExtra => !string.IsNullOrEmpty(ExtraText);  // UI 是否要画加料行
        }                                                            // Plan 结束

        private static readonly Dictionary<string, Plan> Cache = new(StringComparer.Ordinal);
        private const int CacheLimit = 512;                           // 缓存上限：超过就整体清空（简单、无碎片） // 结果缓存：键 = 菜名+喜好+厌恶

        /// <summary>清空缓存。</summary>
        internal static void ClearCache()   // 外部可调
        {
            Cache.Clear();                  // 清空
        }                                   // ClearCache 结束

        /// <summary>对外入口：先查缓存，未命中再算一次并写回。</summary>
        internal static Plan Compute(JsonDataStore.RecipeDef recipe, HashSet<string> likes, HashSet<string> hates, bool orderDish = false) // 菜 + 喜好 + 厌恶 + 是否点单菜
        {
            if (recipe == null) return new Plan(string.Empty, string.Empty, false); // 空菜对象：返回空计划

            var key = recipe.Name + "|" + Join(likes) + "|" + Join(hates) + (orderDish ? "|ORDER" : "|NORMAL"); // 缓存键（含点单标记）
            if (Cache.TryGetValue(key, out var cached)) return cached;      // 命中：直接返回

            Plan plan;                                    // 结果变量
            try                                           // 计算异常不能让面板崩
            {
                plan = ComputeCore(recipe, likes, hates, orderDish); // 真正算法
            }                                             // try 结束
            catch                                         // 任何异常
            {
                plan = new Plan(MainText(recipe), string.Empty, false); // 退化为“只显示主料”
            }                                             // catch 结束

            if (Cache.Count >= CacheLimit) Cache.Clear();  // 超上限：整表清掉，重新算（内存优先）
            Cache[key] = plan;                            // 写缓存
            return plan;                                  // 返回
        }                                                 // Compute 结束

        /// <summary>
        /// 背包里这个食材还有没有货。
        /// 返回 true = 有货或库存读不到（不拦）；false = 明确没货。
        /// </summary>
        private static bool InStock(int ingredientId)
        {
            try
            {
                var stock = GameData.RunTime.Common.RunTimeStorage.Ingredients;   // 游戏静态字典：食材 id → 数量
                if (stock == null || stock.Count == 0) return true;                // 读不到库存：不拦
                return stock.TryGetValue(ingredientId, out var have) && have > 0;  // 有货才算可用
            }
            catch
            {
                return true;                                                       // 异常：不拦
            }
        }

        /// 互斥词条（游戏底层「词条抵消」机制，官方交流群管理员确认）：
        /// 同一道菜里这两个词条不会共存，后加的会被抵消（面板显示半透明白色 ×）。
        /// 官方给出的完整对照（六组，已全部确认互斥）：
        ///   昂贵 ↔ 实惠、饱腹 ↔ 下酒、肉 ↔ 素、灼热 ↔ 凉爽、大份 ↔ 小巧、重油 ↔ 清淡。
        /// </summary>
        private static readonly (string A, string B)[] ConflictPairs =           // 互斥词条对
        {
            ("昂贵", "实惠"),                                                      // 价格词条互斥
            ("饱腹", "下酒"),                                                      // 饱腹 / 下酒 互斥（官方举例）
            ("肉", "素"),                                                          // 荤 / 素 互斥（加肉类菜不能加素料）
            ("灼热", "凉爽"),                                                      // 温度词条互斥
            ("大份", "小巧"),                                                      // 分量词条互斥
            ("重油", "清淡"),                                                      // 油腻 / 清淡 互斥
        };

        /// <summary>
        /// 食材词条与主菜现有词条是否互斥：命中任一对（A 在一边、B 在另一边）就算冲突。
        /// </summary>
        private static bool HasConflict(IEnumerable<string>? ingredientTags, HashSet<string> dishTags)
        {
            if (ingredientTags == null || dishTags.Count == 0) return false;   // 没词条就不冲突
            var tags = new HashSet<string>(StringComparer.Ordinal);            // 食材词条临时集合
            foreach (var tag in ingredientTags)                                // 逐个食材标签
            {
                if (string.IsNullOrEmpty(tag)) continue;                       // 空标签跳过
                tags.Add(tag);                                                 // 收集
            }
            if (tags.Count == 0) return false;                                 // 没词条不冲突

            foreach (var pair in ConflictPairs)                                // 逐对互斥词条
            {
                if (tags.Contains(pair.A) && dishTags.Contains(pair.B)) return true; // 食材带 A、主菜带 B
                if (tags.Contains(pair.B) && dishTags.Contains(pair.A)) return true; // 食材带 B、主菜带 A
            }

            return false;                                                      // 都不冲突
        }

        // 说明：这里**没有**「危险食材黑名单」了（毒 / 猎奇 / 奇怪 / 昂贵 都不再无条件禁止）。
        // 官方群确认：游戏里不存在绝对的“危险食材”，只要稀客不讨厌、且不触发词条重叠/互斥，
        // 带这些标签的食材照样能加。唯一会变成黑暗物质的原因就是下面两条：标签重叠、互斥词条。

        /// <summary>常规食材类型 id（IngredientData.json 的 type 字段）：0=肉、1=水产、2=蔬菜。</summary>
        private static readonly HashSet<int> CommonTypeIds = new()                          // 常规类型 id 集合
        {
            0, 1, 2,                                                                            // 肉 / 水产 / 蔬菜；-1（特殊货）不算常规
        };                                                                                      // 集合结束

        /// <summary>是不是常规食材：类型是肉/水产/蔬菜，或标签里含家常标签。</summary>
        private static bool IsCommonIngredient(JsonDataStore.IngredientDef ingredient)      // 入参：食材
        {
            if (ingredient == null) return false;                                           // 空对象：不是
            if (CommonTypeIds.Contains(ingredient.Type)) return true;                        // 类型是肉/水产/蔬菜 → 常规
            return IsNormalIngredient(ingredient.Tags);                                     // 特殊类型：只有带家常标签才算常规
        }                                                                                   // IsCommonIngredient 结束

        /// <summary>
        /// 核心：在「主料绝对优先」的前提下挑加料。
        /// 官方规则（唯一两个会出黑暗物质的条件）：**标签重叠** 与 **互斥词条**。
        /// 规则顺序：
        ///   ① 主料用到的食材（同名或同 id）绝不能当加料，避免占用主料槽位；
        ///   ② 标签重叠：加料标签与主菜已有词条有任何重合 → 丢弃（重叠即抵消，直接变黑暗物质）；
        ///   ③ 主菜「黑色词条」（RecipeData 的 negativeTags，带 × 的那个）命中 → 丢弃；
        ///   ④ 互斥词条（六组，见 ConflictPairs）命中 → 丢弃；
        ///   ⑤ 稀客厌恶标签命中 → 丢弃；
        ///   ⑥ 必须能补至少 1 条稀客喜好，补不了就不加（绝不为了凑数硬塞）；
        ///   ⑦ 同分时：常见食材优先 → 便宜优先 → 等级低优先；
        ///   ⑧ 完美判定：只要选中的料补到了喜好、且全程没碰上述任何一条 → 完美。
        /// 注意：**没有**危险食材黑名单——毒 / 猎奇 / 奇怪 只会在稀客讨厌它们或触发重叠时被拦。
        /// </summary>
        private static Plan ComputeCore(JsonDataStore.RecipeDef recipe, HashSet<string> likes, HashSet<string> hates, bool orderDish) // 算法体
        {
            var mainText = MainText(recipe);                                  // 主料文本（只读，绝不改）
            var slots = recipe.MaxExtraIngredients;                           // 这道菜最多能加几种料（厨具槽位）
            if (slots <= 0 || JsonDataStore.IngredientCatalog.Count == 0)     // 槽位为 0 / 食材表没加载
            {
                return new Plan(mainText, string.Empty, false);                // 返回：只有主料，不加料
            }                                                                  // if 结束

            // ① 主料保护：把主料的 id 和名字都记下来，后面一个都不许再选
            var mainIds = new HashSet<int>();                                 // 主料食材 id 集合
            var mainNames = new HashSet<string>(StringComparer.Ordinal);      // 主料/配料名字集合
            if (recipe.MainIngredients != null)                               // 有主料表才处理
            {
                foreach (var item in recipe.MainIngredients)                  // 逐样主料
                {
                    if (item == null) continue;                               // 空条目跳过
                    if (item.Id != 0) mainIds.Add(item.Id);                   // 记 id
                    if (!string.IsNullOrEmpty(item.Name)) mainNames.Add(item.Name); // 记名字
                }                                                             // foreach 结束
            }                                                                 // 主料表处理结束

            if (recipe.Ingredients != null)                                   // 菜本身列出的配料也要保护
            {
                foreach (var name in recipe.Ingredients)                      // 逐个配料名
                {
                    if (!string.IsNullOrEmpty(name)) mainNames.Add(name);      // 加进保护集合
                }                                                             // foreach 结束
            }                                                                 // 配料名处理结束

            var covered = new HashSet<string>(StringComparer.Ordinal);         // 已覆盖的喜好标签
            if (recipe.Tags != null)                                          // 菜自带标签
            {
                foreach (var tag in recipe.Tags) covered.Add(tag);             // 一个个记入
            }                                                                 // foreach 结束

            // 主菜当前拥有的全部词条（含后面加进去的料带来的词条），用于「同属性 / 互斥」判定。
            var dishTags = new HashSet<string>(StringComparer.Ordinal);        // 主菜词条集合
            if (recipe.Tags != null)                                          // 主菜自带 tags
            {
                foreach (var tag in recipe.Tags)                              // 逐个标签
                {
                    if (!string.IsNullOrEmpty(tag)) dishTags.Add(tag);         // 记入词条集合
                }                                                              // foreach 结束
            }                                                                  // if 结束

            var chosen = new List<JsonDataStore.IngredientDef>();              // 已选食材（保序）
            var chosenNames = new HashSet<string>(StringComparer.Ordinal);     // 已选名字（去重）

            for (var round = 0; round < slots; round++)                        // 每轮加一种，最多 slots 轮
            {
                JsonDataStore.IngredientDef? best = null;                      // 本轮最优食材
                var bestGain = 0;                                              // 本轮最优能补几条新喜好

                foreach (var ingredient in JsonDataStore.IngredientCatalog)    // 枚举全部食材
                {
                    if (ingredient == null || string.IsNullOrEmpty(ingredient.Name)) continue; // 脏数据跳过

                    // ① 主料绝对优先：同 id 或同名的一律不许当加料
                    if (mainIds.Contains(ingredient.Id) || mainNames.Contains(ingredient.Name)) continue; // 是主料：跳过
                    if (chosenNames.Contains(ingredient.Name)) continue;                                   // 已经选过：跳过

                    // ② 标签重叠（黑暗物质的头号原因）：加料标签与主菜已有词条有任何重合 → 丢弃。
                    //    例：菜里已经带「肉」，配料也带「肉」→ 重叠即抵消，直接变黑暗物质。
                    if (HitsAny(ingredient.Tags, dishTags)) continue;           // 与主菜词条重叠：跳过

                    // ③ 主菜的「黑色词条」（RecipeData 的 negativeTags，面板上带 × 的那个）命中 → 丢弃。
                    //    例：烤八目鳗 negativeTags = 肉 / 素 → 加肉、加素料都会变黑暗物质。
                    if (HitsAny(ingredient.Tags, recipe.NegativeTags)) continue;

                    // ④ 互斥词条（六组，见 ConflictPairs）：肉↔素、大份↔小巧、昂贵↔实惠、饱腹↔下酒、重油↔清淡、灼热↔凉爽。
                    if (HasConflict(ingredient.Tags, dishTags)) continue;

                    // ⑤ 稀客厌恶的食材出局——但**点单菜（符卡料理）豁免**：
                    //    只要客人点的是这道菜，为了做成完美品质，即使加了她讨厌的料也可以加（官方规则）。
                    if (!orderDish && HitsAny(ingredient.Tags, hates)) continue; // 非点单菜才查厌恶

                    // ⑥ 库存门槛（m07784）：背包里一个都没有的食材不许当加料——面板上写着「和牛」，玩家却拿不出来。
                    //    只在库存读得到时生效；读不到就不瞎拦（否则整块加料全空）。
                    if (!InStock(ingredient.Id)) continue;                        // 明确没货：跳过

                    // ⑤ 必须能补至少 1 条新喜好
                    var gain = 0;                                              // 能补的新喜好数
                    if (ingredient.Tags != null)                               // 有标签才算
                    {
                        foreach (var tag in ingredient.Tags)                   // 逐个标签
                        {
                            if (tag != null && likes.Contains(tag) && !covered.Contains(tag)) gain++; // 是喜好且未覆盖 → +1
                        }                                                      // foreach 结束
                    }                                                          // 标签计算结束

                    if (gain <= 0) continue;                                   // 补不了喜好：不加

                    // ⑥ 同分比较：收益高优先 → 常见食材优先 → 便宜优先 → 等级低优先（全是“更安全”方向）
                    var common = IsCommonIngredient(ingredient);                     // 是否常见（type 0/1/2 或家常标签）
                    var bestCommon = best != null && IsCommonIngredient(best);       // 当前最优是否常见
                    var better = best == null                                                        // 还没有候选
                                 || gain > bestGain                                                   // 收益更高
                                 || (gain == bestGain && common && !bestCommon)                       // 收益相同：常见料优先
                                 || (gain == bestGain && common == bestCommon && ingredient.Price < best.Price) // 再相同：更便宜
                                 || (gain == bestGain && common == bestCommon && ingredient.Price == best.Price && ingredient.Level < best.Level); // 再相同：等级更低
                    if (better)                                                // 更优就替换
                    {
                        best = ingredient;                                     // 记下最优
                        bestGain = gain;                                       // 记下收益
                    }                                                          // if 结束
                }                                                              // 食材遍历结束

                if (best == null) break;                                       // 一个安全的都挑不出：结束（宁可不加）

                chosen.Add(best);                                              // 收进结果
                chosenNames.Add(best.Name);                                    // 收进去重集合
                if (best.Tags != null)                                         // 更新覆盖集合
                {
                    foreach (var tag in best.Tags)                             // 逐标签
                    {
                        if (tag == null) continue;                             // 空标签跳过
                        if (likes.Contains(tag)) covered.Add(tag);             // 只记喜好标签
                        dishTags.Add(tag);                                     // 主菜词条也要更新：后续料得跟它比对
                    }                                                          // foreach 结束
                }                                                              // if 结束
            }                                                                  // 槽位循环结束

            if (chosen.Count == 0) return new Plan(mainText, string.Empty, false); // 一个都没加：只显示主料

            var builder = new StringBuilder();                                 // 拼加料文本
            for (var i = 0; i < chosen.Count; i++)                             // 按选中顺序拼
            {
                if (i > 0) builder.Append('、');                               // 顿号分隔
                builder.Append(chosen[i].Name);                                // 食材名
            }                                                                  // for 结束

            // ⑦ 完美判定（放宽版）：只要加进去的料提供了至少 1 条稀客喜好、且全程没碰厌恶标签，就算完美加料。
            //    不再强制“必须凑够 2 条喜好”——那条规则会逼着往锅里塞多余食材，正是黑暗料理的来源。
            var perfect = chosen.Count > 0;                                    // 有料入选即完美（入选前提：补≥1喜好 + 无危险 + 无厌恶 + 非主料）

            return new Plan(mainText, builder.ToString(), perfect);            // 返回最终结果
        }                                                                      // ComputeCore 结束

        /// <summary>主料文本。</summary>
        private static string MainText(JsonDataStore.RecipeDef recipe)      // 只读主料字段
        {
            if (recipe.MainIngredients == null || recipe.MainIngredients.Length == 0) return string.Empty; // 无主料 → 空串

            var builder = new StringBuilder();                              // 拼串
            foreach (var item in recipe.MainIngredients)                    // 遍历主料
            {
                if (item == null || string.IsNullOrEmpty(item.Name)) continue; // 脏数据跳过
                if (builder.Length > 0) builder.Append('+');                // '+' 连接
                builder.Append(item.Name);                                  // 名字
                builder.Append('x');                                        // 数量分隔
                builder.Append(item.Count <= 0 ? 1 : item.Count);           // 数量（非法值按 1）
            }                                                               // foreach 结束

            return builder.Length == 0 ? string.Empty : "主料: " + builder; // 加前缀
        }                                                                   // MainText 结束

        private static readonly HashSet<string> NormalTags = new(StringComparer.Ordinal) // 家常料标签集合
        {
            "肉", "水产", "素", "家常", "鲜", "饱腹", "山珍", "海味", "甜", "咸", "酸", "辣", "和风", "中华", "西式", // 命中其一即家常
        };                                                                                 // 集合结束

        /// <summary>家常食材判定（只用于同分排序）。</summary>
        private static bool IsNormalIngredient(IEnumerable<string>? tags)                  // 输入标签
        {
            if (tags == null) return false;                                                // 无标签：非家常
            foreach (var tag in tags)                                                      // 遍历
            {
                if (tag != null && NormalTags.Contains(tag)) return true;                   // 命中即家常
            }                                                                              // foreach 结束

            return false;                                                                  // 都不是
        }                                                                                  // 结束

        /// <summary>集合是否有交集。</summary>
        private static bool HitsAny(IEnumerable<string>? tags, HashSet<string> set)         // 厌恶拦截用
        {
            if (tags == null || set.Count == 0) return false;                               // 空输入：不命中
            foreach (var tag in tags)                                                       // 遍历
            {
                if (tag != null && set.Contains(tag)) return true;                           // 命中即 true
            }                                                                               // foreach 结束

            return false;                                                                   // 不命中
        }                                                                                   // 结束

        /// <summary>同上一版，但对照集合是数组（用于 recipe.NegativeTags 这种 string[]）。</summary>
        private static bool HitsAny(IEnumerable<string>? tags, string[]? against)           // 数组版
        {
            if (tags == null || against == null || against.Length == 0) return false;       // 空输入：不命中
            foreach (var tag in tags)                                                       // 遍历食材标签
            {
                if (string.IsNullOrEmpty(tag)) continue;                                     // 空标签跳过
                foreach (var candidate in against)                                           // 遍历对照数组
                {
                    if (string.Equals(tag, candidate, StringComparison.Ordinal)) return true; // 命中即 true
                }                                                                            // foreach 结束
            }                                                                                // foreach 结束

            return false;                                                                    // 不命中
        }                                                                                    // 结束

        /// <summary>集合 → 稳定字符串（排序后拼接）。</summary>
        private static string Join(HashSet<string> set)                                     // 缓存键用
        {
            var list = new List<string>(set);                                               // 复制
            list.Sort(StringComparer.Ordinal);                                              // 排序
            return string.Join(",", list);                                                  // 拼接
        }                                                                                   // 结束
    }                                                                                       // AddOnPlanner 结束
}                                                                                           // namespace 结束
