using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameData.Core.Collections.CharacterUtility;
using GameData.Core.Collections.NightSceneUtility;
using HarmonyLib;
using NightScene.GuestManagementUtility;

namespace MystiaGuestTag;

/// <summary>
/// 稀客识别：Hook 营业场景稀客生成方法，读取 stringId / Id 并登记。
///
/// Hook 点（interop 元数据已核实）：
///   NightScene.GuestManagementUtility.GuestsManager.SpawnSpecialGuestGroup(
///       int id, GuestSpawnType guestSpawnType, Nullable&lt;Vector3&gt; overrideSpawnPosition,
///       Action&lt;GuestGroupController&gt; onGuestLeave, LeaveType leaveType, bool recordIzakaya,
///       int targetDeskCode, bool tryToJumpQueue,
///       Action&lt;AStarInputGeneratorComponent&gt; postProcessCharacterCallback, bool shouldFade)
///     → SpecialGuestsController
///
/// 为什么不是 DayScene.DaySceneMap.RefreshNPCs：白天地图 NPC 刷新（RefreshNPCs(bool)）只维护
/// RunTimeDayScene.trackedNPCs 的白天地图可见性，不产生营业场景的稀客；且 4.4.0e 的 interop 里
/// DayScene.DaySceneMap 没有 GetMapNPCs 方法（全程序集搜索 0 命中），用户提到的该方法在此版本不存在。
/// </summary>
internal static class SpecialGuestTracking
{
    private const string HarmonyTag = "[GuestTag]";

    /// <summary>被过滤掉的路人数量（仅日志用）。</summary>
    private static int _nonSpecialSkipped;

    /// <summary>稀客映射表探测失败是否已记录过日志。</summary>
    private static bool _specialGuestProbeFailureLogged;

    /// <summary>厌恶标签最多取几个（防御：标签太多会把面板撑长）。</summary>
    private const int MaxHateTags = 8;

    /// <summary>一条已识别稀客的展示数据。</summary>
    internal sealed class SeenGuest
    {
        /// <summary>
        /// 是否「已确认入座并进入订单列表」。只有 RefreshPresent() 看到 IsOrdering == true 才会置真；
        /// 生成钩子（SpawnSpecialGuestGroup）只登记、不置真 —— 这样准备阶段刚刷出来的稀客不会凭空上板（m08014）。
        /// </summary>
        public bool Seated { get; set; }

        public SeenGuest(
            string stringId,
            int id,
            string displayName,
            string[]? tags,
            string[] hateTags,
            SetMatchRecommender.RecommendationResult food,
            SetMatchRecommender.RecommendationResult beverage,
            int budget,
            int requestFoodTag,
            int requestBeverageTag,
            bool orderKnown,
            DateTime? firstSeenUtc = null,
            bool hasOrdered = false,
            bool dining = false)
        {
            StringId = stringId;
            Id = id;
            DisplayName = displayName;
            Tags = tags;
            HateTags = hateTags;
            Recommendations = food.Items;
            FoodLabel = food.Label;
            HasFoodPreference = food.HasPreference;
            BeverageRecommendations = beverage.Items;
            BeverageLabel = beverage.Label;
            HasBeveragePreference = beverage.HasPreference;
            Budget = budget;
            RequestFoodTag = requestFoodTag;
            RequestBeverageTag = requestBeverageTag;
            OrderKnown = orderKnown;
            HasOrdered = hasOrdered;
            Dining = dining;
            FirstSeenUtc = firstSeenUtc ?? DateTime.UtcNow;
            LastSeenUtc = FirstSeenUtc;
        }

        public string StringId { get; }
        public int Id { get; }
        public string DisplayName { get; }

        /// <summary>喜好标签；null 表示未收录且运行时也读不到。</summary>
        public string[]? Tags { get; }

        /// <summary>厌恶标签（读 SpecialGuest.HateFoodTagOriginal）；空数组 = 这位稀客没有厌恶标签。</summary>
        public string[] HateTags { get; }

        /// <summary>按当前策略排好的推荐菜品（已经带售价）；空表示有偏好但没匹配上。</summary>
        public string[] Recommendations { get; }

        /// <summary>这一行菜品的前缀，随策略变：低价优先推荐 / 高价优先推荐 / 少料易做推荐。</summary>
        public string FoodLabel { get; }

        /// <summary>false = 这位稀客没有任何口味偏好数据（面板显示「无明显偏好」）。</summary>
        public bool HasFoodPreference { get; }

        /// <summary>按当前策略排好的推荐酒水（已经带售价）；空表示有偏好但没匹配上。</summary>
        public string[] BeverageRecommendations { get; }

        /// <summary>这一行酒水的前缀：低价优先酒水 / 高价优先酒水。</summary>
        public string BeverageLabel { get; }

        /// <summary>false = 这位稀客没有任何酒水偏好数据（面板显示「无明显偏好」）。</summary>
        public bool HasBeveragePreference { get; }

        /// <summary>这一顿能掏出来的钱（GuestGroupController.MaxFundCarry）；0 = 没读到，按不限预算处理。</summary>
        public int Budget { get; }

        /// <summary>这一单点单气泡里的特殊要求标签 id（0 = 没要求）。面板随每次点单换口味靠它。</summary>
        public int RequestFoodTag { get; }

        /// <summary>m06184：点单气泡是否已经弹出（此刻还亮着）。</summary>
        public bool OrderKnown { get; }

        /// <summary>
        /// 这位客人这一顿是不是已经点过单了（点过就一直为真，气泡消失也不回退）。
        /// 用来区分「刚坐下还没点」和「已经点完、正在吃饭」——两者 OrderKnown 都是 false，但含义完全不同。
        /// </summary>
        public bool HasOrdered { get; }

        /// <summary>
        /// 正在用餐：点过单了，而且点单气泡已经消失。
        /// 面板靠它决定「该不该把这位挪到后面」「自动翻页时跳过他」。
        /// </summary>
        public bool Dining { get; }

        /// <summary>这一单要求的酒水标签 id（0 = 没要求）。</summary>
        public int RequestBeverageTag { get; }

        /// <summary>第一次被看到的时间：面板按它升序排，先来的稀客在上面（不会被新客挤走）。</summary>
        public DateTime FirstSeenUtc { get; }

        public DateTime LastSeenUtc { get; set; }
    }

    private static readonly Dictionary<string, SeenGuest> Seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnknownReported = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HateLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 未硬编码稀客的喜好标签缓存：stringId → 从 SpecialGuest.LikeFoodTag + GetFoodTagText 读到的标签。
    /// 缓存是因为 RefreshPresent() 每帧都会对在场稀客调用 Record()，不能每帧都走一遍 IL2CPP 取文本。
    /// null 表示读过但读不到（同样缓存，避免每帧重试）。
    /// </summary>
    private static readonly Dictionary<string, string[]?> LiveTagCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>稀客离开后仍保留显示的宽限时间。</summary>
    internal static readonly TimeSpan VisibleGrace = TimeSpan.FromSeconds(90);

    internal static void Install(Harmony harmony)
    {
        try
        {
            var targetType = typeof(GuestsManager);
            var candidates = targetType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == nameof(GuestsManager.SpawnSpecialGuestGroup))
                .ToList();

            foreach (var candidate in candidates)
            {
                Diag.DbgSb(() => $"{HarmonyTag} SpawnSpecialGuestGroup 候选: {Describe(candidate)}");
            }

            var target = candidates.FirstOrDefault();
            if (target == null)
            {
                Plugin.Logger.LogError(
                    $"{HarmonyTag} 找不到 {targetType.FullName}.SpawnSpecialGuestGroup，稀客识别不生效！");
                return;
            }

            harmony.Patch(
                target,
                postfix: new HarmonyMethod(typeof(SpecialGuestTracking), nameof(SpawnSpecialGuestGroup_Postfix)));

            var info = Harmony.GetPatchInfo(target);
            if (info?.Postfixes == null || info.Postfixes.Count == 0)
            {
                Plugin.Logger.LogError($"{HarmonyTag} 注册 {target.Name} 的 postfix 后回读不到补丁，识别可能不生效！");
                return;
            }

            Plugin.Logger.LogInfo($"{HarmonyTag} 已挂载稀客生成 Hook: {Describe(target)}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"{HarmonyTag} 挂载稀客生成 Hook 失败: {ex}");
        }
    }

    /// <summary>
    /// 营业场景稀客生成 postfix：直接读返回值里的 SpecialGuest。
    /// 只读字段，不修改 __result，对游戏逻辑零影响。
    /// </summary>
    private static void SpawnSpecialGuestGroup_Postfix(SpecialGuestsController __result)
    {
        try
        {
            if (__result == null) return;

            // IL2CPP 对象可能是已回收包装，判 Pointer 更安全
            if (__result.Pointer == IntPtr.Zero) return;

            // m08014：这里**不**登记进 Seen。准备阶段 SpawnSpecialGuestGroup 就会返回稀客对象，
            // 但那时他还没入座、没点单；以前在这里 Record 就会凭空弹出一个「等待稀客点单」的幽灵。
            Diag.DbgSb(() => $"{HarmonyTag} 稀客生成钩子触发（仅观察，不登记）：{SafeString(() => __result.SpecialGuest.stringId)}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 读取稀客生成结果失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 这个角色 id 到底是不是「稀客」——直接问游戏的映射表 DataBaseCharacter.IsSpecialGuestMapped(id)。
    /// 这张表就是游戏自己用来生成日间稀客 / 夜间点单客的同一张表，比维护名字白名单可靠得多；
    /// 普通妖怪（路人）在表里是 false，于是它们在采集阶段就被挡掉：不登记、不算菜、不算预算、不进面板。
    /// 调用失败（IL2CPP 尚未初始化 / DLC 数据没加载完）时保守返回 true——宁可多算一位，也不漏掉稀客。
    /// </summary>
    private static bool IsSpecialGuestId(int id, SpecialGuest? guest = null)
    {
        if (id <= 0) return false;                                                  // id 非法：直接判定不是稀客

        // 证据一：游戏自己的映射表（日间稀客用它生成，最权威）
        try
        {
            if (DataBaseCharacter.IsSpecialGuestMapped(id)) return true;             // 表里明确是稀客
        }
        catch (Exception ex)
        {
            if (!_specialGuestProbeFailureLogged)                                   // 只记一次，避免刷屏
            {
                _specialGuestProbeFailureLogged = true;                             // 标记已记录
                Diag.DbgSb(() => $"{HarmonyTag} 稀客映射表读取失败（后续按其它证据判断）: {ex.Message}");
            }
        }

        // 证据二：本地稀客表（69 位）。映射表只覆盖日间稀客，夜间点单客要靠它兜住——
        //         之前只信映射表，结果把坐到 1 号桌的莉格露当路人挡掉，整个面板直接空了。
        if (JsonDataStore.HasGuestId(id)) return true;                              // 本地表里有他

        // 证据三：他自带喜好标签就一定是稀客（普通妖怪没有喜好数据）。
        try
        {
            if (guest != null && guest.Pointer != IntPtr.Zero && guest.LikeFoodTag is { Length: > 0 })
            {
                return true;                                                        // 有喜好 → 稀有客人
            }
        }
        catch
        {
            // 读不到标签就当普通客人处理，交给下一帧再看
        }

        return false;                                                               // 三条证据都不成立
    }

    /// <summary>被挡住的路人计数（只用于日志，证明「零消耗」这条真的在跑）。</summary>
    private static void CountNonSpecialSkip()
    {
        _nonSpecialSkipped++;                                                       // 累加
        if (_nonSpecialSkipped == 1)                                                // 第一次挡掉时打一条
        {
            Diag.DbgSb(() => $"{HarmonyTag} 已开始过滤普通妖怪：它们不会进入面板，也不参与任何推荐计算");
        }
    }

    /// <summary>把一个稀客控制器登记进展示表（供 Hook 与面板刷新共用）。返回登记到的 stringId。</summary>
    internal static string? Record(SpecialGuestsController controller)
    {
        try
        {
            var guest = controller.SpecialGuest;
            // Pointer == 0 说明 native 对象已经销毁，这时候再读任何属性都是访问野内存
            if (guest == null || guest.Pointer == IntPtr.Zero) return null;

            var id = SafeInt(() => guest.Id);

            // 非稀客（普通妖怪）直接不留痕：不进 Seen、不算菜、不算预算、不进面板。
            if (!IsSpecialGuestId(id, guest))
            {
                CountNonSpecialSkip();                                              // 只记个数
                return null;                                                        // 采集阶段就结束
            }

            var rawStringId = SanitizeName(SafeString(() => guest.stringId));

            // 读不到 stringId 时用 "#id" 当键：以前空串会让多个未识别稀客挤在同一个键上互相覆盖
            var stringId = string.IsNullOrEmpty(rawStringId) ? "#" + id : rawStringId;

            var fallbackName = SafeString(() =>
            {
                var name = controller.OnGetGuestName();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            });

            // m03869: name and tags come straight from the runtime object, zero hardcoded tables.
            var displayName = SanitizeName(fallbackName ?? stringId);

            // like-tags straight from SpecialGuest.LikeFoodTag (real game data)
            var tags = GetOrReadLiveTags(stringId, guest) ?? Array.Empty<string>();

            // 厌恶标签：读 SpecialGuest.HateFoodTagOriginal（Il2CppStructArray<int>）。
            // 注意：HateFoodTag 是 Il2CppSystem 的 IEnumerable<int>，C# 里 foreach 会报 CS0117/CS0202，别用。
            var hateTags = ReadHateTags(guest);
            LogHateOnce(stringId ?? string.Empty, hateTags);

            // 客人的钱包：GuestGroupController.MaxFundCarry 是他这一顿最多能掏出多少钱。
            // 读不到（0/异常）就当作不限预算，推荐逻辑会退化成不过滤。
            var budget = SafeInt(() => controller.MaxFundCarry);
            if (budget <= 0) budget = SafeInt(() => controller.GetFund);

            // 推荐菜品：按喜好标签给当晚菜单（读不到就全菜谱表）打分，再按预算过滤。
            // 失败/没匹配时是 NoMatch，面板显示「无匹配」，不影响稀客识别。
            // m05162：点单气泡里的特殊要求（如「汤羹」「甜」）——含这个标签的菜/酒顶到最前
            var (requestFoodTag, requestBevTag, orderKnown) = ReadPendingRequestTags(controller);

            // 推荐：纯集合比对（SetMatchRecommender）。
            // 先按厌恶标签过滤，再按点单要求过滤，再按预算过滤，最后按喜好命中数排序；
            // 整个过程不出现任何加权 / 加减分 / 相似度，也不为凑数补厌恶菜。
            SetMatchRecommender.Recommend(
                guest,
                stringId ?? string.Empty,
                displayName,
                budget,
                requestFoodTag,
                orderKnown,
                requestBevTag,
                out var food,
                out var beverage);

            // 同一个稀客每次进店都会被重新 Record：# 保留他第一次出现的时间，
            // 这样面板排序稳定（先来的在上），不会每帧抖。
            DateTime? firstSeen = null;
            var alreadyOrdered = false;                                         // 之前有没有点过单
            var wasDining = false;                                              // 之前是不是已经在吃了
            lock (Gate)
            {
                if (Seen.TryGetValue(stringId ?? string.Empty, out var previous))
                {
                    firstSeen = previous.FirstSeenUtc;
                    alreadyOrdered = previous.HasOrdered;                       // 继承「点过单」这个事实
                    wasDining = previous.Dining;                                // 继承「在用餐」这个事实
                }
            }

            // 「点过单」是一次性的：只要这一顿点过一次，气泡消失后也要记得他点过了，
            // 否则会退化成「刚坐下还没点」——面板会一直显示等待点单，还会被自动翻页当成待服务对象。
            var hasOrdered = orderKnown || alreadyOrdered;
            var dining = hasOrdered && !orderKnown;                             // 点完了、气泡没了 = 正在用餐

            // m08050 / m08067：这里不再产生「自动翻页」信号。
            // 上菜、进店、离店都只影响名单，页码完全由玩家的鼠标滚轮控制。
            Diag.DbgSb(() => $"{HarmonyTag} 稀客 [{stringId}] 点单状态：已下单={hasOrdered}，用餐中={dining}");
            // m05959：点单变化时打一行日志，方便确认「每点一次菜，口味要求就重新算」
            int previousFoodRequest = 0;
            int previousBevRequest = 0;
            var hadPrevious = false;
            lock (Gate)
            {
                if (Seen.TryGetValue(stringId ?? string.Empty, out var previousSeen))
                {
                    previousFoodRequest = previousSeen.RequestFoodTag;
                    previousBevRequest = previousSeen.RequestBeverageTag;
                    hadPrevious = true;
                }
            }

            if (hadPrevious && (previousFoodRequest != requestFoodTag || previousBevRequest != requestBevTag))
            {
                Diag.Dbg(
                    $"{HarmonyTag} 稀客 [{stringId}] 点单变化：菜要求 {previousFoodRequest} → {requestFoodTag}，" +
                    $"酒要求 {previousBevRequest} → {requestBevTag}（推荐会按新要求重算）");
            }

            var seen = new SeenGuest(
                stringId ?? string.Empty,
                id,
                displayName,
                tags,
                hateTags,
                food,
                beverage,
                budget,
                requestFoodTag,
                requestBevTag,
                orderKnown,
                firstSeen,
                hasOrdered,
                dining);

            lock (Gate)
            {
                Seen[seen.StringId] = seen;
            }

            if (tags == null || tags.Length == 0)
            {
                ReportUnknown(stringId, id, fallbackName);
            }

            return seen.StringId;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 登记稀客失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 未硬编码的稀客：从游戏运行时数据读喜好标签（只读，不改游戏状态）。
    /// 结果按 stringId 缓存，读不到也缓存 null，避免每帧重试。
    /// </summary>
    private static string[]? GetOrReadLiveTags(string? stringId, SpecialGuest guest)
    {
        var key = stringId ?? string.Empty;
        lock (Gate)
        {
            if (LiveTagCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var tags = ReadLiveTags(guest);

        lock (Gate)
        {
            LiveTagCache[key] = tags;
        }

        if (tags is { Length: > 0 })
        {
            Diag.Dbg(
                $"{HarmonyTag} 从运行时读到稀客 \"{key}\" 的喜好标签：{string.Join(" / ", tags)}");
        }

        return tags;
    }

    /// <summary>
    /// 读 SpecialGuest.LikeFoodTag（int[]，标签 id）并用 GetFoodTagText(id) 转成当前语言的文本。
    /// 任何一步失败都返回 null（面板会退化成「喜好未收录」）。
    /// </summary>
    private static string[]? ReadLiveTags(SpecialGuest guest)
    {
        try
        {
            var ids = new List<int>();
            var raw = guest.LikeFoodTag;
            if (raw == null) return null;

            foreach (var tagId in raw)
            {
                ids.Add(tagId);
                if (ids.Count >= 12) break; // 防御：别把面板撑爆
            }

            if (ids.Count == 0) return null;

            var texts = new List<string>(ids.Count);
            foreach (var tagId in ids)
            {
                try
                {
                    // 用 DataBaseLanguage.GetFoodTag(id) 拿短标签名（「猎奇」「清淡」这种）。
                    // 别用 SpecialGuest.GetFoodTagText(id)：它返回的是标签长描述（整段话），
                    // 直接显示会把面板撑爆 —— 2026-11-01 实机踩过。
                    var text = GameData.CoreLanguage.Collections.DataBaseLanguage.GetFoodTag(tagId);
                    if (!string.IsNullOrWhiteSpace(text) && !texts.Contains(text))
                    {
                        texts.Add(text);
                    }
                }
                catch
                {
                    // 单个标签取文本失败不影响其它标签
                }
            }

            return texts.Count > 0 ? texts.ToArray() : null;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 读取运行时喜好标签失败（改用未收录提示）: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读稀客的厌恶标签：SpecialGuest.HateFoodTagOriginal（Il2CppStructArray&lt;int&gt;）→ GetFoodTag 短名。
    /// 读不到就返回空数组（面板不画「厌恶」行），任何异常都不影响稀客识别本身。
    /// </summary>
    /// <summary>
    /// 读客人当前这张单的特殊要求标签（m05162）。
    /// 数据源：GuestGroupController.AllOrdersData（Il2CppSystem Stack&lt;OrderBase&gt;）→ Peek()，
    /// 若这一单是 SpecialOrder，取它的 RequestFoodTag / RequestBeverageTag（都是 int 标签 id）。
    /// 普通单（NormalOrder）是「指定某道菜」，不走这个分支，返回 (0, 0)。
    /// </summary>
    private static (int Food, int Beverage, bool HasOrder) ReadPendingRequestTags(GuestGroupController? controller)
    {
        if (controller == null || controller.Pointer == IntPtr.Zero) return (0, 0, false);

        try
        {
            var stack = controller.AllOrdersData;
            if (stack == null || stack.Count == 0) return (0, 0, false);

            var order = stack.Peek();
            if (order == null || order.Pointer == IntPtr.Zero) return (0, 0, false);

            // 只要栈顶是特殊订单，就算「点单已经出现」，哪怕他没提口味要求
            var special = order.TryCast<GuestsManager.SpecialOrder>();
            if (special == null) return (0, 0, false);

            var foodTag = special.RequestFoodTag;
            var bevTag = special.RequestBeverageTag;
            if (foodTag < 0) foodTag = 0;
            if (bevTag < 0) bevTag = 0;
            return (foodTag, bevTag, true);
        }
        catch (Exception ex)
        {
            Diag.DbgSb(() => $"{HarmonyTag} 读点单要求失败（本次不做优先）：{ex.GetType().Name}: {ex.Message}");
            return (0, 0, false);
        }
    }

    private static string[] ReadHateTags(SpecialGuest guest)
    {
        try
        {
            var raw = guest.HateFoodTagOriginal;
            if (raw == null) return Array.Empty<string>();

            var texts = new List<string>(raw.Length);
            foreach (var tagId in raw)
            {
                if (texts.Count >= MaxHateTags) break;

                try
                {
                    var text = GameData.CoreLanguage.Collections.DataBaseLanguage.GetFoodTag(tagId);
                    if (!string.IsNullOrWhiteSpace(text) && !texts.Contains(text))
                    {
                        texts.Add(text);
                    }
                }
                catch
                {
                    // 单个标签取文本失败不影响其它标签
                }
            }

            return texts.Count > 0 ? texts.ToArray() : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 读取厌恶标签失败（面板不显示厌恶行）: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>厌恶标签只在第一次读到某个稀客时打一行日志，方便核对（别每帧刷屏）。</summary>
    private static void LogHateOnce(string stringId, string[] hateTags)
    {
        if (hateTags.Length == 0) return;

        lock (Gate)
        {
            if (!HateLogged.Add(stringId)) return;
        }

        Diag.DbgSb(() => $"{HarmonyTag} 稀客 [{stringId}] 的厌恶标签：{string.Join(" / ", hateTags)}");
    }

    private static void ReportUnknown(string? stringId, int id, string? fallbackName)    {
        var key = stringId ?? $"#{id}";
        lock (Gate)
        {
            if (!UnknownReported.Add(key)) return;
        }

        Plugin.Logger.LogWarning(
            $"{HarmonyTag} 发现未收录稀客：stringId=\"{stringId}\" Id={id} 本地化名=\"{fallbackName ?? "?"}\" " +
            "（运行时没读到喜好标签，面板会显示「数据未收录」）");
    }

    /// <summary>取当前应显示的稀客（调用方负责处理锁内拷贝）。</summary>
    /// <summary>把某位稀客标记为「已确认在等单」（由 RefreshPresent 在 IsOrdering 为真时调用）。</summary>
    internal static void MarkSeated(string stringId)                                     // 入参：内部键
    {
        if (string.IsNullOrEmpty(stringId)) return;                                      // 空键忽略
        lock (Gate)                                                                      // 与 Record 共用锁
        {
            if (Seen.TryGetValue(stringId, out var seen)) seen.Seated = true;            // 置真
        }
    }

    internal static List<SeenGuest> SnapshotVisible()
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            foreach (var stale in Seen.Where(pair => now - pair.Value.LastSeenUtc > VisibleGrace)
                         .Select(pair => pair.Key).ToList())
            {
                Seen.Remove(stale);
            }

            return Seen.Values
                .Where(g => g.Seated)                                                    // m08014：没确认在等单的一律不上板
                .OrderBy(g => g.FirstSeenUtc)
                .ThenBy(g => g.DisplayName, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// 把「已经离店」的稀客从展示表里剔掉。
    /// 判定依据是 GuestsManager.AllPresentedGuestGroupController 里还有没有他，
    /// 而不是时间窗——否则稀客走了面板还会挂着他 90 秒（2026-11-01 实测的 bug）。
    /// 刚进店 2 秒内的先留着，避免生成 Hook 已登记、名单还没刷新的空档把他闪掉。
    /// </summary>
    private static string _lastPresentLog = string.Empty;

    /// <summary>m06052：把「这一轮场上到底有几位、分别是谁」只在变化时打一行，排查只显示一位。</summary>
    private static void LogPresentOnce()
    {
        try
        {
            string line;
            int count;
            lock (Gate)
            {
                count = Seen.Count;
                line = string.Join(", ", Seen.Values
                    .OrderBy(g => g.FirstSeenUtc)
                    .Select(g => g.DisplayName + "(" + g.StringId + ")"));
            }

            var text = "场上 " + count + " 位：" + line;
            if (string.Equals(text, _lastPresentLog, StringComparison.Ordinal)) return;

            _lastPresentLog = text;
            Diag.DbgSb(() => $"{HarmonyTag} {text}（面板显示上限 {GuestTagConfig.GetMaxGuests()} 位）");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 营业结束 / 回到白天时调用：把在场名单、自动翻页信号全部清空。
    /// 不清的话，第二天开门会拿昨天的旧数据先画一帧（甚至沿用昨天的页码）。
    /// </summary>
    internal static void ClearAll()
    {
        lock (Gate)                                                          // 与 RefreshPresent 共用同一把锁
        {
            Seen.Clear();                                                    // 清空在场名单
            _lastPresentLog = string.Empty;                                  // 让下一天的「场上 N 位」日志能重新打
        }
    }

    private static void PruneAbsent(HashSet<string> alive)
    {
        var now = DateTime.UtcNow;
        List<string> gone;
        lock (Gate)
        {
            gone = Seen.Keys
                .Where(key => !alive.Contains(key) && now - Seen[key].LastSeenUtc > PresentGrace)
                .ToList();

            foreach (var key in gone)
            {
                Seen.Remove(key);
                LiveTagCache.Remove(key);
            }
        }

        if (gone.Count == 0) return;

        foreach (var key in gone)
        {
            Diag.DbgSb(() => $"{HarmonyTag} 稀客已离店，移出面板：{key}");
        }
    }

    /// <summary>
    /// 刚进店的宽限期：这段时间内即使没出现在 GuetsManager 的在场名单里也先留着。
    /// 稀客是「先走进来、坐下之后才进 AllPresentedGuestGroupController」，2 秒会让刚进店的人被踢掉、
    /// 面板憋到点单时才出现（玩家反馈的「抢不到单」根因），所以放宽到 30 秒。
    /// </summary>
    private static readonly TimeSpan PresentGrace = TimeSpan.FromSeconds(30);

    /// <summary>标记一次「仍然在场」，延长显示时间。</summary>
    internal static void Touch(string stringId)
    {
        lock (Gate)
        {
            if (Seen.TryGetValue(stringId, out var seen))
            {
                seen.LastSeenUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>两次「在场名单」扫描的最小间隔（秒）。OnGUI 每帧会被调好几次，不节流就是白白扫内存。</summary>
    private const float RefreshIntervalSeconds = 1.5f; // 既不每帧遍历，又能在客人进店的 1.5 秒内上屏

    private static float _lastRefreshTime = -999f;

    /// <summary>
    /// 节流版刷新：默认 1.5 秒最多扫一次。进入/离开营业场景立刻会重扫（阶段变了）。
    /// </summary>
    /// <summary>
    /// 刷新节流：每 1.5 秒最多刷新一次「在场稀客」。
    /// 注意：这里只刷新名单，绝不碰 _windowOffset（页码由玩家自己控制），所以不会出现“幽灵翻页”。
    /// </summary>
    internal static void RefreshPresentIfDue()
    {
        var now = UnityEngine.Time.unscaledTime;                            // 当前游戏时间（不受暂停影响）
        if (now - _lastRefreshTime < RefreshIntervalSeconds) return;        // 距上次不足 1.5 秒：直接返回，不做任何事

        _lastRefreshTime = now;                                             // 记下本次刷新时间
        RefreshPresent();                                                   // 真正去读 GuestsManager 的在场名单
    }

    /// <summary>
    /// 刷新「在场稀客」：营业中读 GuestsManager.AllPresentedGuestGroupController，
    /// 覆盖插件载入前就已进店（读档/场景重建）的情况。
    /// </summary>
    internal static void RefreshPresent()
    {
        try
        {
            var manager = GuestsManager.Instance;
            if (manager == null || manager.Pointer == IntPtr.Zero) return;

            if (!PhaseGate.IsNightWorkPhase()) return;

            var presented = manager.AllPresentedGuestGroupController;
            if (presented == null) return;

            // m05960：每轮刷新重建“本轮剩余食材”账本，加料按库存分配

            // 这一轮里还活着的稀客；扫完拿它反向剔除已经离店的
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var scannedAny = false;

            foreach (var controller in presented)
            {
                if (controller == null || controller.Pointer == IntPtr.Zero) continue;

                var special = controller.TryCast<SpecialGuestsController>();
                if (special == null || special.Pointer == IntPtr.Zero) continue;

                // 最便宜的一道闸：确认这一组真的是稀客再往下走。
                // 普通妖怪在这里就被跳过，连 Record() 里的标签读取与推荐计算都不会发生——这就是「零消耗」。
                var peek = special.SpecialGuest;                                    // 组上的稀客对象
                if (peek == null || peek.Pointer == IntPtr.Zero) continue;           // 空指针：不是稀客组
                if (!IsSpecialGuestId(SafeInt(() => peek.Id), peek))                      // 映射表判定
                {
                    CountNonSpecialSkip();                                          // 只记个数
                    continue;                                                       // 后面一概不算
                }

                // 【订单闸门】面板成员 = 游戏左下角「待上菜订单列表」的成员。
                // IsOrdering 为 true 表示这一桌桌上还有没端上去的订单，正是会出现在订单列表里的那些；
                // 菜端上去之后游戏把订单移出列表、IsOrdering 变 false，这里直接 continue，
                // Record() 不再被调用、PruneAbsent() 把这一条从面板里剔掉 —— 面板与订单列表同进同出。
                if (!SafeBool(() => controller.IsOrdering))                              // 没有待上菜的订单（已上菜，或还在看菜单）
                {
                    // m08050：这里**不再**发「自动翻页」信号。
                    // 客人上菜/离店只是把名单刷新一遍，页码永远由玩家自己控制（滚轮 / ↑↓）。
                    continue;                                                          // 本帧不再 Record，PruneAbsent 会把他剔掉
                }

                scannedAny = true;
                var id = Record(special);                                                // 采集（可能返回 null）
                if (id != null) MarkSeated(id);                                          // m08014：确认在等单，才允许上板
                if (!string.IsNullOrEmpty(id)) alive.Add(id);
            }

            if (!scannedAny) alive.Clear();

            PruneAbsent(alive);

            LogPresentOnce();
        }
        catch (Exception ex)
        {
            // 每次 OnGUI 都会调用，只记一次避免刷屏
            if (!_refreshFailureLogged)
            {
                _refreshFailureLogged = true;
                Plugin.Logger.LogWarning($"{HarmonyTag} 刷新在场稀客失败（后续静默）: {ex.Message}");
            }
        }
    }

    private static bool _refreshFailureLogged;

    /// <summary>
    /// 抹掉名字里的脏字符：游戏本地化文本偶尔夹带换行、回车、单引号、零宽字符，
    /// 面板上就会凭空多出一个单引号（2026-11-01「橙 Chen '」就是这个）。
    /// </summary>
    internal static string SanitizeName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new System.Text.StringBuilder(raw!.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch)) continue;
            if (ch == (char)0x200B || ch == (char)0x200C || ch == (char)0x200D || ch == (char)0xFEFF) continue;
            sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim();

        // 首尾多出来的引号/反引号/星号也去掉（游戏里有些名字带引号做注音标记）
        var junk = new[] { (char)0x27, '"', '`', (char)0x00B4, (char)0x2018, (char)0x2019, (char)0x201C, (char)0x201D, '*' };
        cleaned = cleaned.Trim(junk).Trim();

        // 名字尾巴上孤零零的句号/逗号/顿号（面板里会单独占一行，看着像脏字符）
        var punctuation = new[]
        {
            (char)0x3002, (char)0xFF0C, (char)0x3001, (char)0xFF1A, (char)0xFF1B, (char)0xFF1F, (char)0xFF01,
            '.', ',', ';', ':', '!', '?', '-', '_',
        };
        cleaned = cleaned.Trim(punctuation).Trim();

        while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");

        return cleaned;
    }

    private static string? SafeString(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static int SafeInt(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>安全读 bool：IL2CPP 对象可能已销毁，任何异常都当作「没有」。</summary>
    private static bool SafeBool(Func<bool> getter)                             // 入参：取值委托
    {
        try { return getter(); }                                                // 正常取值
        catch (Exception ex)                                                    // 对象已销毁 / 类型不对
        {
            Diag.DbgSb(() => $"{HarmonyTag} 读 bool 失败（按 false 处理）：{ex.GetType().Name}"); // 只记调试日志
            return false;                                                       // 当作没有
        }
    }

    private static string Describe(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.ParameterType.Name} {p.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }
}
