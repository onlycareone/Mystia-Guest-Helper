using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    /// <summary>厌恶标签最多取几个（防御：标签太多会把面板撑长）。</summary>
    private const int MaxHateTags = 8;

    /// <summary>一条已识别稀客的展示数据。</summary>
    internal sealed class SeenGuest
    {
        public SeenGuest(
            string stringId,
            int id,
            string displayName,
            string[]? tags,
            string[] hateTags,
            RecipeRecommender.RecommendationResult food,
            RecipeRecommender.RecommendationResult beverage,
            int budget,
            DateTime? firstSeenUtc = null)
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
                Plugin.Logger.LogDebug($"{HarmonyTag} SpawnSpecialGuestGroup 候选: {Describe(candidate)}");
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

            Record(__result);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 读取稀客生成结果失败: {ex.Message}");
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
            var food = RecipeRecommender.Recommend(guest, stringId ?? string.Empty, budget);

            // 推荐酒水：喜好标签优先读 SpecialGuest.LikeBevTag，读不到用硬编码表里那份（按人设推的）。
            // 两者都没有 = NoPreference，面板显示「无明显偏好」。
            // m05033：把「推荐出来的菜」的标签传给酒水，做「甜口菜配甜口酒」的口味配对。
            var pairingTags = RecipeRecommender.ReadDishTagNames(
                RecipeRecommender.ExtractItemNames(food.Items));
            var beverage = RecipeRecommender.RecommendBeverages(
                guest,
                stringId ?? string.Empty,
                null,
                budget,
                pairingTags);

            // 同一个稀客每次进店都会被重新 Record：# 保留他第一次出现的时间，
            // 这样面板排序稳定（先来的在上），不会每帧抖。
            DateTime? firstSeen = null;
            lock (Gate)
            {
                if (Seen.TryGetValue(stringId ?? string.Empty, out var previous))
                {
                    firstSeen = previous.FirstSeenUtc;
                }
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
                firstSeen);

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
            Plugin.Logger.LogDebug(
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

        Plugin.Logger.LogDebug($"{HarmonyTag} 稀客 [{stringId}] 的厌恶标签：{string.Join(" / ", hateTags)}");
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
            Plugin.Logger.LogDebug($"{HarmonyTag} 稀客已离店，移出面板：{key}");
        }
    }

    /// <summary>刚进店的宽限期：这段时间内即使没出现在在场名单里也先留着。</summary>
    private static readonly TimeSpan PresentGrace = TimeSpan.FromSeconds(2);

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
    private const float RefreshIntervalSeconds = 2f; // m03065：2 秒一次，绝不每帧遍历游戏数据

    private static float _lastRefreshTime = -999f;

    /// <summary>
    /// 节流版刷新：默认 0.5 秒最多扫一次。进入/离开营业场景立刻会重扫（阶段变了）。
    /// </summary>
    internal static void RefreshPresentIfDue()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now - _lastRefreshTime < RefreshIntervalSeconds) return;

        _lastRefreshTime = now;
        RefreshPresent();
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

            // 这一轮里还活着的稀客；扫完拿它反向剔除已经离店的
            var alive = new HashSet<string>(StringComparer.Ordinal);
            var scannedAny = false;

            foreach (var controller in presented)
            {
                if (controller == null || controller.Pointer == IntPtr.Zero) continue;

                var special = controller.TryCast<SpecialGuestsController>();
                if (special == null || special.Pointer == IntPtr.Zero) continue;

                scannedAny = true;
                var id = Record(special);
                if (!string.IsNullOrEmpty(id)) alive.Add(id);
            }

            if (!scannedAny) alive.Clear();

            PruneAbsent(alive);
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

    private static string Describe(MethodBase method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.ParameterType.Name} {p.Name}"));
        return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }
}
