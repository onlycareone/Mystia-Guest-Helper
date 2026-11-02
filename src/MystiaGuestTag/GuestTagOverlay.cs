using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MystiaGuestTag;

/// <summary>
/// 半透明面板：显示当前（含最近 90 秒内）进店稀客的名称、喜好标签、推荐菜品与推荐酒水。
///
/// 绘制点选 NightScene.SceneManager.OnGUI()（营业场景常驻的私有 OnGUI，interop 已核实）：
/// 不注入自定义 MonoBehaviour —— Il2CppInterop 注入类的 Update/OnGUI 不触发是已知问题，
/// 挂到游戏自己的 OnGUI 上更稳，也不会在白天/标题界面误画。
///
/// 排版（2026-11-01 第 3 次重做，按 5 稀客实测反馈）：
/// - 默认停靠屏幕中左侧、竖向居中：碰不到右边 COMBO 区，也压不到底部金钱栏/按键提示；
/// - 每位稀客最多 2 个推荐菜 + 1 杯推荐酒水（由 RecipeRecommender 限制），块高因此可控；
/// - 人多了先缩字号（base → base-6），还放不下就把多余的淡客折叠成「…另有 N 位」；
/// - 也可以把 UI.Layout 改成 TwoColumns 走两列并排；
/// - 新的稀客排在上面（按最后出现时间倒序）。
/// 字号/宽度/位置/列数都在 BepInEx\\config\\cc.mystia.guesttag.cfg 里调（改完重开游戏生效）。
/// </summary>
internal static class GuestTagOverlay
{
    private const string HarmonyTag = "[GuestTag]";

    private const float Margin = 18f;
    private const float ColumnGap = 12f;

    /// <summary>本帧内容需要的宽度（由 MeasureDesiredWidth 算出），给 ResolveColumnWidth 自适应用。</summary>
    /// <summary>
    /// 行文本缓存（m03065）：IMGUI 一秒要 Repaint 几十次，每帧都 new 富文本字符串会被 GC 拖死。
    /// 键 = stringId + 行种类，值里存数组引用，SeenGuest 每 2 秒重建数组时才重算一次。
    /// </summary>
    private static readonly Dictionary<string, (string[] Arr, string Text)> LineCache =
        new(StringComparer.Ordinal);

    private static string CachedLine(string id, string kind, string[]? arr, Func<string[], string> build)
    {
        var key = (id ?? string.Empty) + "|" + kind;
        var values = arr ?? Array.Empty<string>();
        if (LineCache.TryGetValue(key, out var hit) && ReferenceEquals(hit.Arr, values)) return hit.Text;

        string text;
        try
        {
            text = build(values);
        }
        catch
        {
            text = string.Empty;
        }

        LineCache[key] = (values, text);
        return text;
    }

    private static float _desiredWidth;

    /// <summary>名字色板：日式暖色食堂风的明黄，醒目又衬暖底。</summary>
    private static readonly string[] NameColors =
    {
        "#FFE066", // 明黄
    };

    /// <summary>Top* 停靠时的额外上边距：避开顶部的招财猫/金钱条。</summary>
    private const float TopInset = 96f;

    /// <summary>标题栏高度：这一条是拖拽把手，鼠标按在上面才能挪面板。</summary>
    private const float TitleBarHeight = 30f;

    /// <summary>标题栏文字。</summary>
    private const string TitleBarText = "稀客助手";

    /// <summary>本帧场上的稀客总数（不受「显示数量」截断影响，m03657）。</summary>
    private static int _totalGuests;

    /// <summary>面板高度最多容纳几个稀客，多的靠滚动条看。</summary>
    private const int MaxVisibleGuests = 4;

    /// <summary>滚动条宽度（IMGUI 默认竖直滚动条约 16px）。</summary>
    private const float ScrollbarWidth = 16f;

    /// <summary>「喜好」一行最多显示几个标签，多出来的用 … 省略。</summary>
    private const int MaxVisibleTags = 5;

    /// <summary>一套按字号生成的样式（缩字号时重建，按字号缓存）。</summary>
    private sealed class Styles
    {
        public GUIStyle Title = null!;
        public GUIStyle Name = null!;
        public GUIStyle Line = null!;
    }

    /// <summary>行的种类：正文 / 稀客名字 / 稀客之间的分隔线。</summary>
    private enum RowKind
    {
        Text,
        Name,
        Separator,
        NameRule,
        Spacer,
    }

    /// <summary>一行要画的东西。</summary>
    private readonly struct Row
    {
        public Row(RowKind kind, string text, float height, bool spacerBefore)
        {
            Kind = kind;
            Text = text;
            Height = height;
            SpacerBefore = spacerBefore;
        }

        public RowKind Kind { get; }
        public string Text { get; }
        public float Height { get; }
        public bool SpacerBefore { get; }
    }

    /// <summary>某个字号下的行高/间距。</summary>
    private readonly struct Metrics
    {
        public Metrics(int size)
        {
            Size = size;
            // 字号放大后行高一起放大，行与行之间别再贴死（m02908）
            NameHeight = size * 1.62f + 8f;
            LineHeight = size * 1.62f + 6f;
            // 稀客区块之间的留白：分割线上下各一半
            Spacing = Mathf.Max(18f, size * 1.15f);
            Padding = Mathf.Max(12f, size * 0.85f);
        }

        public int Size { get; }
        public float NameHeight { get; }
        public float LineHeight { get; }
        public float Spacing { get; }
        public float Padding { get; }
    }

    /// <summary>一个稀客的整块内容（名字 + 喜好 + 推荐菜 + 推荐酒水）。</summary>
    private sealed class Block
    {
        public List<Row> Rows { get; } = new();
        public float Height { get; set; }
    }

    /// <summary>算好的版面：列数、每行的内容、整体尺寸。</summary>
    private sealed class Layout
    {
        public Styles Styles = null!;
        public Metrics Metrics;
        public float Padding;
        public float Spacing;
        public float TitleHeight;
        public float ColumnWidth;
        public float Width;
        public float Height;
        public float ContentHeight;
        public float ViewHeight;
        public bool NeedsScroll;
        public string Title = string.Empty;
        public List<List<Row>> ColumnRows { get; } = new();
        public List<float> ColumnHeights { get; } = new();
    }

    private static readonly Dictionary<int, Styles> StyleCache = new();

    // 拖拽状态：_panelPosition 为 null 时按配置/默认角落算位置
    private static Vector2 _scroll;
    private static string? _lastLayoutKey;

    /// <summary>上一次算出来的面板矩形：非 Repaint 事件用它做拖拽命中测试，不重复排版。</summary>
    private static Rect? _lastPanelRect;

    /// <summary>F10 隐藏整个面板（做菜时想清屏）。</summary>
    private static bool _hidden;

    /// <summary>标题栏齿轮按钮的位置（拖拽要避开它）。</summary>
    private const float StepperHeight = 26f;

    /// <summary>步进条下面那条「[策略: 低价优先]」按钮的高度。</summary>
    private const float StrategyHeight = 24f;

    private static GUIStyle? _stepLabelStyle;

    /// <summary>上一次 Repaint 算出来的版面：非 Repaint 事件要拿它补画步进条、给 GUI.Button 派发点击。</summary>
    private static Layout _lastLayout = null!;
    private static bool _hasLayout;


    /// <summary>分页窗口的起始下标（滚轮翻页）。</summary>
    private static int _windowOffset;

    private static int _maxWindowOffset;

    /// <summary>F9 设置面板 / F10 隐藏显示，都只在 Layout 事件里轮询（一帧一次）。</summary>
    private static void HandleGlobalInput()
    {
        try
        {
            var currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.Layout) return;

            if (Input.GetKeyDown(KeyCode.F10))
            {
                _hidden = !_hidden;
                _lastPanelRect = null;
                Plugin.Logger.LogInfo(_hidden
                    ? $"{HarmonyTag} 面板已隐藏（再按 F10 显示）"
                    : $"{HarmonyTag} 面板已显示");
            }
        }
        catch
        {
            // 按键检测失败不影响绘制
        }
    }

    /// <summary>鼠标在面板上滚轮翻页：一格一位稀客。</summary>
    private static void HandleWheel(Rect panel, Event? currentEvent)
    {
        if (currentEvent == null || currentEvent.type != EventType.ScrollWheel) return;
        if (_maxWindowOffset <= 0) return;
        if (!panel.Contains(currentEvent.mousePosition)) return;

        // 一格滚轮 = 一位稀客；到底再滚回顶部、到顶再滚到底（循环）。
        // 有的鼠标/触控板一次事件 delta.y 会给到 ±2 以上，这里只认方向、步长恒为 1，
        // 所以无论滚得多猛，翻页速度都一样，不会「唰」一下滑过去。
        var step = currentEvent.delta.y > 0f ? 1 : -1;
        var span = _maxWindowOffset + 1;
        _windowOffset = ((_windowOffset + step) % span + span) % span;
        currentEvent.Use();
    }
    private static Vector2? _panelPosition;
    private static bool _dragging;
    private static int _dragControlId;
    private static bool _cursorStateCaptured;
    private static bool _cursorVisibleBefore;
    private static bool _cursorTextureCleared;
    private static bool _ownHotControl;
    private static Font? _font;
    private static bool _styleFailureLogged;
    private static bool _drawFailureLogged;
    private static bool _trimLogged;

    internal static void Install(Harmony harmony)
    {
        try
        {
            var targetType = typeof(NightScene.SceneManager);
            var target = AccessTools.Method(targetType, "OnGUI", Type.EmptyTypes);
            if (target == null)
            {
                Plugin.Logger.LogError($"{HarmonyTag} 找不到 {targetType.FullName}.OnGUI，面板不会显示！");
                return;
            }

            harmony.Patch(
                target,
                postfix: new HarmonyMethod(typeof(GuestTagOverlay), nameof(OnGUI_Postfix)));

            var info = Harmony.GetPatchInfo(target);
            if (info?.Postfixes == null || info.Postfixes.Count == 0)
            {
                Plugin.Logger.LogError($"{HarmonyTag} 注册 {targetType.Name}.OnGUI 的 postfix 后回读不到补丁！");
                return;
            }

            // 一行就够：启动日志只留「挂了哪里 + 面板参数」，细节走 Debug
            Plugin.Logger.LogInfo(
                $"{HarmonyTag} 已挂载 {targetType.Name}.OnGUI 绘制点");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"{HarmonyTag} 挂载面板绘制点失败: {ex}");
        }
    }

    private static void OnGUI_Postfix()
    {
        HandleGlobalInput();

        // 设置面板要独立于稀客面板：它得在 Layout/Repaint/鼠标事件里都能跑
        SettingsPanel.HandleInput();

        try
        {
            Draw();
        }
        catch (Exception ex)
        {
            if (!_drawFailureLogged)
            {
                _drawFailureLogged = true;
                Plugin.Logger.LogError($"{HarmonyTag} 绘制面板异常（后续静默）: {ex}");
            }
        }

        SettingsPanel.Draw();
    }

    private static void Draw()
    {
        // IMGUI 一帧会进好几次 OnGUI（Layout/Repaint/各种鼠标事件），
        // 只有 Repaint 才真正需要画东西；其它事件直接返回，省掉整帧的排版和 native 读取。
        var currentEvent = Event.current;
        var isRepaint = currentEvent == null || currentEvent.type == EventType.Repaint;

        SpecialGuestTracking.RefreshPresentIfDue();

        // 非 Repaint 事件（Layout / 各种鼠标事件）不重排版，但拖拽和光标必须照常处理，
        // 否则鼠标一动面板就不跟手了。命中测试用上一次算好的矩形。
        if (!isRepaint)
        {
            if (_lastPanelRect.HasValue)
            {
                var rect = _lastPanelRect.Value;
                HandleStepperClick(rect, currentEvent);
                HandleDrag(rect, ResolveTitleBar(rect));
                HandleWheel(rect, currentEvent);

                // 步进条必须在这条路径上也画一遍：GUI.Button 只有吃到 MouseDown/MouseUp 才会返回 true，
                // 只在 Repaint 里画的话按钮永远是死键（2026-11-01「显示数量点不动」就是这个原因）。
                if (_hasLayout)
                {
                    DrawStepperBar(
                        new Rect(rect.x + 2f, rect.y + TitleBarHeight, rect.width - 4f, StepperHeight),
                        _lastLayout);
                    DrawStrategyBar(
                        new Rect(rect.x + 2f, rect.y + TitleBarHeight + StepperHeight, rect.width - 4f, StrategyHeight));
                }
            }

            return;
        }

        if (_hidden)
        {
            _lastPanelRect = null;
            return;
        }

        // 营业结束（结算/白天）就不再显示：否则上一晚的稀客会在结果画面上挂到 90 秒到期
        if (!PhaseGate.IsNightWorkPhase())
        {
            _lastPanelRect = null;
            return;
        }

        var guests = SpecialGuestTracking.SnapshotVisible();
        if (guests.Count == 0)
        {
            _lastPanelRect = null;
            _windowOffset = 0;
            return;
        }

        // 新客在上：最后出现的排最前
        // 先来的稀客排在上面（m01794 要求）：新客插到末尾，不会把老面孔顶下去。
        guests.Sort((a, b) =>
        {
            var byTime = a.FirstSeenUtc.CompareTo(b.FirstSeenUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.DisplayName, b.DisplayName);
        });

        var baseSize = Mathf.Clamp(GuestTagConfig.FontSize.Value, 10, 32);
        var limit = GuestTagConfig.GetMaxGuests(); // 玩家能在 cfg 里限死显示几位

        // 超出 limit 的稀客不丢弃，改成「分页窗口」：滚轮上下翻，标题显示当前窗口范围
        var total = guests.Count;
        _totalGuests = total; // m03657：标题要报「场上总共几位」，不是被截断后的位数
        var window = Mathf.Min(limit, total);
        _maxWindowOffset = Mathf.Max(0, total - window);
        _windowOffset = _maxWindowOffset <= 0 ? 0 : Mathf.Clamp(_windowOffset, 0, _maxWindowOffset);
        if (window < total)
        {
            guests = guests.GetRange(_windowOffset, window);
        }

        var available = AvailableHeight();
        var columns = GuestTagConfig.UseTwoColumns() ? 2 : 1;

        Layout? layout = null;
        var minSize = Mathf.Max(10, baseSize - 6);

        // ① 原字号放得下就直接用；② 否则逐档缩字号；③ 最小字号仍放不下就裁掉末尾稀客
        for (var size = baseSize; size >= minSize && layout == null; size -= 2)
        {
            var styles = EnsureStyles(size);
            if (styles == null) return;

            _desiredWidth = MeasureDesiredWidth(guests, styles);
            layout = TryBuildLayout(guests, new Metrics(size), styles, columns, available, limit, _totalGuests);
        }

        if (layout == null)
        {
            var m = new Metrics(minSize);
            var styles = EnsureStyles(minSize);
            if (styles == null) return;

            _desiredWidth = MeasureDesiredWidth(guests, styles);
            for (var keep = Mathf.Min(limit, guests.Count) - 1; keep >= 1 && layout == null; keep--)
            {
                layout = TryBuildLayout(guests, m, styles, columns, available, keep, _totalGuests);
                if (layout != null && !_trimLogged)
                {
                    _trimLogged = true;
                    Plugin.Logger.LogDebug(
                        $"{HarmonyTag} 同时在场稀客较多（{guests.Count}），面板只显示最近 {keep} 位，其余折叠成一行");
                }
            }

            layout ??= TryBuildLayout(guests, m, styles, columns, available, 1, _totalGuests);
        }

        if (layout == null) return;

        LogLayoutOnce(layout, guests.Count);

        DrawLayout(layout);
    }

    /// <summary>
    /// 量一下「这一屏最好给多宽」：把每行富文本的标签剥掉，用当前字号的 Line 样式算像素宽度。
    /// 字号调大之后面板要跟着变宽，否则中文会折成两行把面板撑高（m02908）。
    /// </summary>
    private static float MeasureDesiredWidth(IReadOnlyList<SpecialGuestTracking.SeenGuest> guests, Styles styles)
    {
        var widest = 0f;

        foreach (var guest in guests)
        {
            var nameText = $"{guest.DisplayName}  {guest.StringId}" +
                           (guest.Budget > 0 ? $"　预算 ¥{guest.Budget}" : string.Empty);

            widest = Mathf.Max(widest, MeasureOne(styles.Name, nameText));

            widest = Mathf.Max(widest, MeasureOne(styles.Line, StripRichText(FormatTags(guest))));

            var hateText = FormatHateTags(guest);
            if (hateText.Length > 0)
            {
                widest = Mathf.Max(widest, MeasureOne(styles.Line, StripRichText(hateText)));
            }

            widest = Mathf.Max(widest, MeasureOne(styles.Line, StripRichText(FormatRecommendations(guest))));

            if (guest.BeverageRecommendations.Length > 0)
            {
                widest = Mathf.Max(widest, MeasureOne(styles.Line, StripRichText(FormatBeverages(guest))));
            }
        }

        return widest;

        float MeasureOne(GUIStyle style, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            try
            {
                return style.CalcSize(new GUIContent(text)).x;
            }
            catch
            {
                return 0f;
            }
        }
    }

    /// <summary>把 &lt;color&gt; / &lt;b&gt; 这类富文本标签剥掉，只留可见字符（测量宽度用）。</summary>
    private static string StripRichText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        var inTag = false;

        foreach (var ch in text)
        {
            if (ch == '<')
            {
                inTag = true;
                continue;
            }

            if (ch == '>')
            {
                inTag = false;
                continue;
            }

            if (!inTag) builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>版面尺寸只在变化时记一行，方便排查「面板高度和内容对不上」这类问题。</summary>
    private static void LogLayoutOnce(Layout layout, int total)
    {
        var key = $"{layout.Title}|{layout.Height:F0}|{layout.ContentHeight:F0}|{layout.ViewHeight:F0}|{layout.NeedsScroll}";
        if (string.Equals(key, _lastLayoutKey, StringComparison.Ordinal)) return;

        _lastLayoutKey = key;
        Plugin.Logger.LogDebug(
            $"{HarmonyTag} 版面：{layout.Title}｜面板高 {layout.Height:F0}｜内容高 {layout.ContentHeight:F0}｜" +
            $"可视高 {layout.ViewHeight:F0}｜滚动 {layout.NeedsScroll}｜字号 {layout.Styles.Line.fontSize}");
    }

    private static void DrawLayout(Layout layout)
    {
        var panel = ResolvePanelRect(layout.Width, layout.Height);
        _lastPanelRect = panel;
        _lastLayout = layout;
        _hasLayout = true;

        // 先用上一帧的矩形判命中，拖拽后本帧立刻按新位置重算
        HandleStepperClick(panel, Event.current);
        HandleDrag(panel, ResolveTitleBar(panel));
        panel = ResolvePanelRect(layout.Width, layout.Height);

        ApplyGameCursor(panel);

        DrawPanelBackground(panel);
        DrawTitleBar(ResolveTitleBar(panel), layout);
        DrawStepperBar(
            new Rect(panel.x + 2f, panel.y + TitleBarHeight, panel.width - 4f, StepperHeight),
            layout);
        DrawStrategyBar(
            new Rect(panel.x + 2f, panel.y + TitleBarHeight + StepperHeight, panel.width - 4f, StrategyHeight));

        var padding = layout.Padding;
        var top = panel.y + TitleBarHeight + StepperHeight + StrategyHeight + layout.Spacing;

        // 人多了就切滚动视图：面板高度只够 4 个稀客，其余靠滚轮/滚动条
        if (layout.NeedsScroll)
        {
            var viewRect = new Rect(panel.x + padding, top, layout.ColumnWidth + ScrollbarWidth, layout.ViewHeight);
            var contentRect = new Rect(0f, 0f, layout.ColumnWidth, layout.ContentHeight);

            _scroll = GUI.BeginScrollView(viewRect, _scroll, contentRect);
            try
            {
                DrawRows(layout.ColumnRows[0], layout, 0f, 0f, layout.ColumnWidth);
            }
            finally
            {
                GUI.EndScrollView();
            }

            DrawOwnCursor(panel);
            return;
        }

        for (var c = 0; c < layout.ColumnRows.Count; c++)
        {
            var x = panel.x + padding + c * (layout.ColumnWidth + ColumnGap);
            DrawRows(layout.ColumnRows[c], layout, x, top, layout.ColumnWidth);
        }

        DrawOwnCursor(panel);
    }

    private static void DrawRows(List<Row> rows, Layout layout, float x, float y, float width)
    {
        foreach (var row in rows)
        {
            if (row.SpacerBefore) y += layout.Spacing;

            if (row.Kind == RowKind.Spacer)
            {
                // 纯空隙行，只占高度
            }
            else if (row.Kind == RowKind.Separator)
            {
                // 稀客之间的暗棕分隔线，画在整行中线上
                var lineY = y + Mathf.Round(row.Height * 0.5f);
                DrawLine(new Rect(x, lineY, width, 1f), new Color(0.45f, 0.36f, 0.22f, 0.85f));
            }
            else if (row.Kind == RowKind.NameRule)
            {
                // 名字下方的暗金横线
                DrawLine(new Rect(x, y + row.Height - 1f, width, 1f), new Color(0.72f, 0.57f, 0.26f, 0.9f));
            }
            else
            {
                var style = row.Kind == RowKind.Name ? layout.Styles.Name : layout.Styles.Line;
                GUI.Label(new Rect(x, y, width, row.Height), row.Text, style);
            }

            y += row.Height;
        }
    }

    /// <summary>
    /// 按给定字号/列数排一次版面；放不下就改成滚动，别把内容截到屏幕外面。
    /// </summary>
    private static Layout? TryBuildLayout(
        IReadOnlyList<SpecialGuestTracking.SeenGuest> guests,
        in Metrics m,
        Styles styles,
        int maxColumns,
        float availableHeight,
        int limit,
        int totalGuests)
    {
        var shown = Mathf.Min(limit, guests.Count);
        if (shown <= 0) return null;

        // 先按「不滚动」排，排不下再切滚动（内容绝不截到屏幕外）。
        var total = Mathf.Max(totalGuests, guests.Count);
        var layout = BuildLayout(guests, m, styles, maxColumns, availableHeight, shown, total, false);
        if (layout == null) return null;

        if (layout.Height > availableHeight)
        {
            layout = BuildLayout(guests, m, styles, maxColumns, availableHeight, shown, total, true) ?? layout;
        }

        if (layout.Width > Screen.width - Margin * 2f) return null;

        return layout;
    }

    /// <summary>真正排版：按 <paramref name="scroll"/> 决定是否单列 + 滚动条。</summary>
    private static Layout? BuildLayout(
        IReadOnlyList<SpecialGuestTracking.SeenGuest> guests,
        in Metrics m,
        Styles styles,
        int maxColumns,
        float availableHeight,
        int shown,
        int total,
        bool scroll)
    {
        var columns = scroll ? 1 : Mathf.Max(1, Mathf.Min(maxColumns, shown));
        var perColumn = scroll ? shown : (shown + columns - 1) / columns;
        if (!scroll) columns = (shown + perColumn - 1) / perColumn;

        var columnWidth = ResolveColumnWidth(columns);
        if (scroll) columnWidth -= ScrollbarWidth; // 给滚动条留出位置
        if (columnWidth < 160f) return null;

        var innerWidth = columnWidth;

        var layout = new Layout
        {
            Styles = styles,
            Metrics = m,
            Padding = m.Padding,
            Spacing = m.Spacing,
            ColumnWidth = columnWidth,
            TitleHeight = TitleBarHeight,
            // 分页时标题直接给出「当前窗口范围」，玩家滚轮翻页能立刻看到自己在第几页
            Title = shown < total
                ? $"{TitleBarText}　·　已显示 {_windowOffset + 1}-{_windowOffset + shown}/{total} 位 · 滚轮翻页"
                : $"{TitleBarText}　·　{total} 位稀客",
        };

        var blocks = new List<Block>(shown);
        for (var i = 0; i < shown; i++)
        {
            blocks.Add(BuildBlock(guests[i], m, styles, innerWidth));
        }

        var more = guests.Count - shown;
        var moreText = more > 0 ? $"<color=#8B949E>…另有 {more} 位</color>" : null;
        var moreHeight = moreText != null ? Measure(styles.Line, moreText, m.LineHeight, innerWidth) : 0f;

        var maxColumnHeight = 0f;

        for (var c = 0; c < columns; c++)
        {
            var rows = new List<Row>();
            var start = c * perColumn;
            var end = Mathf.Min(shown, start + perColumn);

            for (var i = start; i < end; i++)
            {
                if (rows.Count > 0)
                {
                    // 稀客之间：上半个间距 + 分隔线 + 下半个间距，上下留白一致
                    var half = Mathf.Max(6f, m.Spacing * 0.5f);
                    rows.Add(new Row(RowKind.Spacer, string.Empty, half, false));
                    rows.Add(new Row(RowKind.Separator, string.Empty, 1f, false));
                    rows.Add(new Row(RowKind.Spacer, string.Empty, half, false));
                }

                rows.AddRange(blocks[i].Rows);
            }

            // 折叠提示只挂在最后一列末尾
            if (c == columns - 1 && moreText != null)
            {
                var half = Mathf.Max(6f, m.Spacing * 0.5f);
                rows.Add(new Row(RowKind.Spacer, string.Empty, half, false));
                rows.Add(new Row(RowKind.Text, moreText, moreHeight, false));
            }

            // 精确高度：逐行累加，SpacerBefore 的额外间距也要算进去
            var height = 0f;
            foreach (var row in rows)
            {
                height += row.Height + (row.SpacerBefore ? m.Spacing : 0f);
            }

            layout.ColumnRows.Add(rows);
            layout.ColumnHeights.Add(height);
            maxColumnHeight = Mathf.Max(maxColumnHeight, height);
        }

        // 标题栏 + 标题下间距 + 底部内边距，是固定的外框开销
        var chrome = TitleBarHeight + StepperHeight + StrategyHeight + m.Spacing + Mathf.Max(15f, m.Padding);

        var viewHeight = maxColumnHeight;
        if (scroll)
        {
            // 可视区最多占满可用高度，剩下的交给滚动条
            viewHeight = Mathf.Min(maxColumnHeight, Mathf.Max(140f, availableHeight - chrome));
        }

        var needsScroll = maxColumnHeight > viewHeight + 0.5f;

        layout.Width = columns * columnWidth + (columns - 1) * ColumnGap + m.Padding * 2f
                       + (needsScroll ? ScrollbarWidth : 0f);
        // 底部至少留 15px，别让最后一行贴着边框
        layout.Height = chrome + viewHeight;
        layout.ContentHeight = maxColumnHeight;
        layout.ViewHeight = viewHeight;
        layout.NeedsScroll = needsScroll;

        return layout;
    }

    /// <summary>一列画多宽：单列吃满上限；两列时每列窄一点，避免铺满屏幕。</summary>
    private static float ResolveColumnWidth(int columns)
    {
        var configured = Mathf.Clamp(GuestTagConfig.PanelWidth.Value, 220f, 900f);
        var maxWidth = Mathf.Clamp(GuestTagConfig.MaxWidth.Value, 200f, 1200f);
        // 字号放大后按实际内容加宽（m02908），上限仍然是 MaxWidth
        var contentWidth = _desiredWidth > 0f ? _desiredWidth + 32f : 0f;
        var width = Mathf.Min(Mathf.Max(configured, contentWidth), maxWidth);
        width = Mathf.Min(width, Screen.width * 0.45f);

        if (columns > 1)
        {
            var twoColumnBudget = Screen.width * 0.62f - ColumnGap - Margin * 2f;
            width = Mathf.Min(width, Mathf.Max(220f, twoColumnBudget / columns));
        }

        return Mathf.Max(160f, width);
    }

    private static Block BuildBlock(
        SpecialGuestTracking.SeenGuest guest,
        in Metrics m,
        Styles styles,
        float innerWidth)
    {
        var block = new Block();
        var color = ColorFor(guest.StringId);

        // 名字后面挂上这一顿的预算：上错价位他会当场走人，让玩家一眼看到能卖多贵
        var budgetText = guest.Budget > 0
            ? $"<color=#BFAF8E>　预算</color> <color=#FFD98A>¥{guest.Budget}</color>"
            : string.Empty;

        var nameText =
            $"<b><color=#{color}>{Escape(guest.DisplayName)}</color></b>" +
            $"<color=#8B949E>  {Escape(guest.StringId)}</color>" +
            budgetText;

        AddRow(block, nameText, true, m.NameHeight, false, styles, innerWidth);

        // 名字下方的暗金横线（留 4px 高度的行，线画在行底）
        block.Rows.Add(new Row(RowKind.NameRule, string.Empty, 4f, false));
        block.Height += 4f;

        AddRow(block, FormatTags(guest), false, m.LineHeight, false, styles, innerWidth);

        // 厌恶标签：没有就不占行（避免空白行把面板撑高）
        var hateText = FormatHateTags(guest);
        if (hateText.Length > 0)
        {
            AddRow(block, hateText, false, m.LineHeight, false, styles, innerWidth);
        }

        // 策略化之后只有一行：按当前策略排好序、带售价，前缀跟着策略变
        AddRow(block, FormatRecommendations(guest), false, m.LineHeight, false, styles, innerWidth);

        // 没有匹配的酒水就干脆不占这一行（省高度，也不显示「无匹配」占位）
        if (guest.BeverageRecommendations.Length > 0)
        {
            AddRow(block, FormatBeverages(guest), false, m.LineHeight, false, styles, innerWidth);
        }

        return block;
    }

    private static void AddRow(
        Block block,
        string text,
        bool isName,
        float minHeight,
        bool spacerBefore,
        Styles styles,
        float innerWidth)
    {
        var kind = isName ? RowKind.Name : RowKind.Text;
        var height = Measure(isName ? styles.Name : styles.Line, text, minHeight, innerWidth);
        block.Rows.Add(new Row(kind, text, height, spacerBefore));
        block.Height += height;
    }

    /// <summary>策略条整行就是一颗按钮：[策略: 低价优先]，点一下循环切换。</summary>
    private static Rect ResolveStrategyRect(Rect panel) =>
        new(panel.x + 2f, panel.y + TitleBarHeight + StepperHeight, panel.width - 4f, StrategyHeight);

    private static Rect ResolveTitleBar(Rect panel) =>
        new(panel.x + 2f, panel.y + 2f, panel.width - 4f, TitleBarHeight - 2f);

    /// <summary>标题栏：比底板略深的暖棕条 + 底部一条金线 + 标题文字。</summary>
    private static void DrawTitleBar(Rect bar, Layout layout)
    {
        DrawLine(bar, new Color(0.24f, 0.16f, 0.10f, 0.92f));
        DrawLine(
            new Rect(bar.x, bar.yMax - 1f, bar.width, 1f),
            new Color(0.78f, 0.62f, 0.30f, 0.85f));

        GUI.Label(
            new Rect(bar.x + layout.Padding, bar.y, bar.width - layout.Padding * 2f, bar.height),
            layout.Title,
            layout.Styles.Title);
    }

    /// <summary>
    /// 标题栏下方那一条：`显示 3 [-] [+]` / `菜品 2 [-] [+]` / `酒水 2 [-] [+]`。
    /// 点一下立刻改 cfg 并清缓存，面板下一帧就按新数值重排，不用重启游戏。
    /// </summary>
    private static void DrawStepperBar(Rect bar, Layout layout)
    {
        DrawLine(bar, new Color(0.20f, 0.13f, 0.08f, 0.92f));
        DrawLine(
            new Rect(bar.x, bar.yMax - 1f, bar.width, 1f),
            new Color(0.66f, 0.51f, 0.26f, 0.8f));

        EnsureStepperStyles(layout);

        var groups = new (string Id, string Label, int Value)[]
        {
            ("guests", "显示", GuestTagConfig.GetMaxGuests()),
            ("food", "菜品", GuestTagConfig.GetMaxRecipes()),
            ("bev", "酒水", GuestTagConfig.GetMaxDrinks()),
        };

        StepperGeometry(bar.width, out var labelWidth, out var btn, out var valueWidth, out var gap, out var cellWidth);

        for (var i = 0; i < groups.Length; i++)
        {
            var cellX = bar.x + 4f + cellWidth * i;
            var midY = bar.y + (bar.height - btn) * 0.5f;

            GUI.Label(
                new Rect(cellX, bar.y, labelWidth, bar.height),
                groups[i].Label,
                _stepLabelStyle);

            var minusRect = new Rect(cellX + labelWidth, midY, btn, btn);
            var valueRect = new Rect(minusRect.xMax + gap, bar.y, valueWidth, bar.height);
            var plusRect = new Rect(valueRect.xMax + gap, midY, btn, btn);

            // 自绘按钮 + 手写点击：GUI.Button 在 OnGUI 被多次派发时会重复返回 true（实测一下跳三格）
            DrawStepButton(minusRect, "\u2212");
            GUI.Label(valueRect, groups[i].Value.ToString(), _stepLabelStyle);
            DrawStepButton(plusRect, "+");
        }
    }

    /// <summary>策略条：[策略: 低价优先] —— 整行可点，循环切换三种策略。</summary>
    private static void DrawStrategyBar(Rect bar)
    {
        if (!_hasLayout) return;
        EnsureStepperStyles(_lastLayout);

        var hovered = bar.Contains(Event.current?.mousePosition ?? new Vector2(-1f, -1f));
        DrawLine(bar, hovered
            ? new Color(0.40f, 0.28f, 0.15f, 0.98f)
            : new Color(0.26f, 0.18f, 0.11f, 0.96f));
        DrawLine(
            new Rect(bar.x, bar.yMax - 1f, bar.width, 1f),
            new Color(0.72f, 0.57f, 0.28f, hovered ? 0.95f : 0.55f));

        if (_stepLabelStyle == null) return;

        var strategy = GuestTagConfig.GetStrategy();
        var text = $"策略: {GuestTagConfig.StrategyDisplayName(strategy)}　▸ 点击切换";
        GUI.Label(new Rect(bar.x + 8f, bar.y, bar.width - 16f, bar.height), text, _stepLabelStyle);
    }

    private static void DrawStepButton(Rect rect, string text)
    {
        var hovered = rect.Contains(Event.current?.mousePosition ?? new Vector2(-1f, -1f));
        DrawLine(rect, hovered
            ? new Color(0.46f, 0.33f, 0.18f, 0.98f)
            : new Color(0.30f, 0.21f, 0.13f, 0.96f));
        DrawLine(
            new Rect(rect.x, rect.yMax - 1f, rect.width, 1f),
            new Color(0.80f, 0.64f, 0.32f, hovered ? 0.95f : 0.65f));

        if (_stepButtonStyle != null)
        {
            GUI.Label(rect, text, _stepButtonStyle);
        }
    }

    private static Texture2D? _stepButtonTexture;
    private static Texture2D? _stepButtonTextureHover;
    private static GUIStyle? _stepButtonStyle;

    private static Texture2D MakeSolidTexture(Color color)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }

    private static void EnsureStepperStyles(Layout layout)
    {
        if (_stepLabelStyle != null && _stepButtonStyle != null) return;

        _stepLabelStyle = new GUIStyle(GUI.skin.label)
        {
            font = _font,
            fontSize = Mathf.Max(10, layout.Styles.Line.fontSize - 3),
            alignment = TextAnchor.MiddleLeft,
            richText = false,
        };
        _stepLabelStyle.normal.textColor = new Color(0.95f, 0.88f, 0.70f);
        _stepLabelStyle.padding.left = 0;
        _stepLabelStyle.padding.right = 0;

        _stepButtonTexture ??= MakeSolidTexture(new Color(0.30f, 0.21f, 0.13f, 0.96f));
        _stepButtonTextureHover ??= MakeSolidTexture(new Color(0.46f, 0.33f, 0.18f, 0.98f));

        _stepButtonStyle = new GUIStyle(GUI.skin.button)
        {
            font = _font,
            fontSize = Mathf.Max(10, layout.Styles.Line.fontSize - 1),
            alignment = TextAnchor.MiddleCenter,
            richText = false,
            wordWrap = false,
            border = new RectOffset(),
        };
        _stepButtonStyle.padding.left = 0;
        _stepButtonStyle.padding.right = 0;
        _stepButtonStyle.padding.top = 0;
        _stepButtonStyle.padding.bottom = 0;
        _stepButtonStyle.margin.left = 0;
        _stepButtonStyle.margin.right = 0;
        _stepButtonStyle.margin.top = 0;
        _stepButtonStyle.margin.bottom = 0;

        _stepButtonStyle.normal.background = _stepButtonTexture;
        _stepButtonStyle.hover.background = _stepButtonTextureHover;
        _stepButtonStyle.active.background = _stepButtonTextureHover;
        _stepButtonStyle.focused.background = _stepButtonTexture;
        _stepButtonStyle.onNormal.background = _stepButtonTexture;

        _stepButtonStyle.normal.textColor = new Color(0.96f, 0.86f, 0.60f);
        _stepButtonStyle.hover.textColor = new Color(1f, 0.94f, 0.62f);
        _stepButtonStyle.active.textColor = new Color(1f, 0.98f, 0.80f);
    }

    // SceneManager.OnGUI 一帧里会被派发多次（Layout/Repaint/各个鼠标事件），
    // 所以点击做成状态机：MouseDown 只记「想点哪个」，MouseUp 才落账，落账后立刻清掉。
    private static string? _pendingStepper;
    private static float _pendingStepperTime = -999f;
    private static float _lastAppliedTime = -999f;

    private static void HandleStepperClick(Rect panel, Event e)
    {
        var bar = new Rect(panel.x + 2f, panel.y + TitleBarHeight, panel.width - 4f, StepperHeight);
        var inStrategy = ResolveStrategyRect(panel).Contains(e.mousePosition);
        if (!bar.Contains(e.mousePosition) && !inStrategy && _pendingStepper == null) return;

        switch (e.type)
        {
            case EventType.MouseDown when e.button == 0:
                _pendingStepper = HitStepper(bar, e.mousePosition, ResolveStrategyRect(panel));
                _pendingStepperTime = UnityEngine.Time.unscaledTime;
                break;

            case EventType.MouseUp when e.button == 0:
                var id = _pendingStepper;
                _pendingStepper = null;
                if (id == null) return;

                // 按下到松开太久（>0.6 秒）或者松手时已经不在原按钮上，就当取消
                if (UnityEngine.Time.unscaledTime - _pendingStepperTime > 0.6f) return;
                if (HitStepper(bar, e.mousePosition, ResolveStrategyRect(panel)) != id) return;

                ApplyStepper(id);
                break;
        }
    }

    private static readonly string[] StepperIds = { "guests", "food", "bev" };

    /// <summary>
    /// 步进条几何：三格平分，格内 [标签][−][数值][+]。
    /// 面板窄时按钮跟着缩，保证一格的控件不压到下一格（以前固定 30+18+22+18+间距≈94px，
    /// PanelWidth 下限 220 时一格只有 ~71px，+ 号会落到下一格的标签上，点错按钮）。
    /// </summary>
    private static void StepperGeometry(
        float barWidth,
        out float labelWidth,
        out float btn,
        out float valueWidth,
        out float gap,
        out float cellWidth)
    {
        gap = 3f;
        valueWidth = 22f;
        btn = 18f;
        labelWidth = 30f;
        cellWidth = (barWidth - 8f) / 3f;

        var needed = labelWidth + btn + gap + valueWidth + gap + btn;
        if (needed <= cellWidth - 2f) return;

        // 压缩顺序：标签先让到 20，再按剩余空间平均压两个按钮
        labelWidth = Mathf.Clamp(cellWidth * 0.28f, 18f, 30f);
        btn = Mathf.Clamp((cellWidth - labelWidth - valueWidth - gap * 2f - 2f) * 0.5f, 10f, 18f);
    }

    /// <summary>命中测试：把步进条切成三格，每格 [−][数值][+]，返回如 "guests+"。</summary>
    private static string? HitStepper(Rect bar, Vector2 mouse, Rect strategyBar)
    {
        if (strategyBar.Contains(mouse)) return "strategy";

        StepperGeometry(bar.width, out var labelWidth, out var btn, out var valueWidth, out var gap, out var cellWidth);

        for (var i = 0; i < StepperIds.Length; i++)
        {
            var cellX = bar.x + 4f + cellWidth * i;
            var midY = bar.y + (bar.height - btn) * 0.5f;

            var minusRect = new Rect(cellX + labelWidth, midY, btn, btn);
            var valueRect = new Rect(minusRect.xMax + gap, bar.y, valueWidth, bar.height);
            var plusRect = new Rect(valueRect.xMax + gap, midY, btn, btn);

            if (minusRect.Contains(mouse)) return StepperIds[i] + "-";
            if (plusRect.Contains(mouse)) return StepperIds[i] + "+";
        }

        return null;
    }

    private static void ApplyStepper(string id)
    {
        // 双保险：同一次点击万一被派发两遍，0.15 秒内的第二次直接丢掉
        if (UnityEngine.Time.unscaledTime - _lastAppliedTime < 0.15f) return;
        _lastAppliedTime = UnityEngine.Time.unscaledTime;

        var guests = GuestTagConfig.GetMaxGuests();
        var food = GuestTagConfig.GetMaxRecipes();
        var drinks = GuestTagConfig.GetMaxDrinks();

        if (id == "strategy")
        {
            var next = GuestTagConfig.ApplyStrategy(GuestTagConfig.NextStrategy(GuestTagConfig.GetStrategy()));
            RecipeRecommender.ClearCache();
            Plugin.Logger.LogInfo(
                $"[GuestTag] 推荐策略已切换为「{GuestTagConfig.StrategyDisplayName(next)}」（写入 cfg），推荐会立即按新策略重排");
            return;
        }

        switch (id)
        {
            case "guests-": guests = Mathf.Max(1, guests - 1); break;
            case "guests+": guests = Mathf.Min(10, guests + 1); break;
            case "food-": food = Mathf.Max(1, food - 1); break;
            case "food+": food = Mathf.Min(10, food + 1); break;
            case "bev-": drinks = Mathf.Max(1, drinks - 1); break;
            case "bev+": drinks = Mathf.Min(10, drinks + 1); break;
            default: return;
        }

        GuestTagConfig.ApplyDisplaySettings(guests, food, drinks);
        RecipeRecommender.ClearCache();
        _windowOffset = 0; // 人数变了，翻页窗口重头开始，免得停在空页

        Plugin.Logger.LogInfo(
            $"[GuestTag] 面板设置已更新（写入 cfg）：显示 {guests} 位 / 每人菜品 {food} 条 / 每人酒水 {drinks} 条");
    }

    /// <summary>
    /// 鼠标按住标题栏拖动面板；松开时把坐标写回 cfg。
    /// 用 Event.current.delta 累加（不是绝对坐标），所以鼠标和面板不会脱节、不会闪。
    /// </summary>
    private static void HandleDrag(Rect panel, Rect titleBar)
    {
        var e = Event.current;
        if (e == null) return;

        var overTitle = titleBar.Contains(e.mousePosition);
        var overPanel = panel.Contains(e.mousePosition);

        // 步进条自己处理点击，拖拽必须让道，否则点一下 [-] 面板就跟着跑了
        var overStepper = new Rect(panel.x + 2f, panel.y + TitleBarHeight, panel.width - 4f, StepperHeight)
            .Contains(e.mousePosition);

        switch (e.type)
        {
            case EventType.MouseDown when e.button == 0 && overTitle && !overStepper:
                _dragging = true;
                _dragControlId = GUIUtility.GetControlID(FocusType.Passive);
                GUIUtility.hotControl = _dragControlId; // 拖动期间独占 IMGUI 事件
                _ownHotControl = true;
                CaptureCursorState();                   // 记住游戏原本的光标状态，松手后还回去
                e.Use();
                break;

            case EventType.MouseDrag when _dragging:
                // 增量累加：鼠标动多少、面板就动多少
                _panelPosition = ClampPosition((_panelPosition ?? panel.position) + e.delta, panel.size);
                e.Use();
                break;

            case EventType.MouseUp when _dragging:
                _dragging = false;
                if (_ownHotControl)
                {
                    GUIUtility.hotControl = 0;
                    _ownHotControl = false;
                }

                RestoreCursorState();

                if (_panelPosition.HasValue)
                {
                    GuestTagConfig.SavePosition(_panelPosition.Value.x, _panelPosition.Value.y);
                }

                e.Use();
                break;

            // 面板正文不再吞事件：以前这里 e.Use() 会把游戏底层的点击/光标更新一起掐掉，
            // 表现就是「鼠标一上面板，游戏的像素光标消失、冒出 Windows 白箭头」（2026-11-01 踩过）。
            // 现在只有标题栏吃拖拽，其余键鼠事件全部穿透给游戏，光标状态只在离开时收尾。
            case EventType.MouseDown:
            case EventType.MouseUp:
            case EventType.MouseDrag:
                if (!overPanel) RestoreCursorState();
                break;
        }
    }

    /// <summary>
    /// 光标状态守卫：只「记住 + 还回去」，不主动改 Cursor.visible。
    /// 游戏营业时用的是自己画的像素光标（并把系统光标藏了），插件强行 Cursor.visible = true
    /// 会冒出 Windows 白箭头 —— 2026-11-01 实机踩过。这里只在鼠标离开面板/松手时复原。
    /// </summary>
    private static void CaptureCursorState()
    {
        if (_cursorStateCaptured) return;
        _cursorVisibleBefore = Cursor.visible;
        _cursorStateCaptured = true;
    }

    private static void RestoreCursorState()
    {
        if (!_cursorStateCaptured) return;
        _cursorStateCaptured = false;
        if (Cursor.visible != _cursorVisibleBefore)
        {
            Cursor.visible = _cursorVisibleBefore;
        }
    }

    /// <summary>
    /// 保持游戏原生光标。营业时游戏自己画像素光标、并把硬件光标藏起来（Cursor.visible = false）；
    /// IMGUI 控件一和鼠标打交道，Unity 会把硬件光标亮出来，于是面板上就冒出 Windows 白箭头。
    /// 这里在鼠标压到面板上时，把硬件光标按游戏原本的状态压回去，并清掉任何 Cursor.SetCursor 贴图，
    /// 这样露出来的就是游戏自己画的那只光标。鼠标离开面板不做任何修改，交还给游戏。
    /// </summary>
    private static void ApplyGameCursor(Rect panel)
    {
        var current = Event.current;
        if (current == null) return;

        if (!panel.Contains(current.mousePosition)) return;

        // 第一次在面板上看到光标时记下游戏的状态（营业中通常是 false）
        CaptureCursorState();

        if (_cursorVisibleBefore) return; // 游戏本来就显示系统光标，别管

        if (Cursor.visible)
        {
            Cursor.visible = false;
        }

        if (!_cursorTextureCleared)
        {
            _cursorTextureCleared = true;
            try
            {
                Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
            catch
            {
                // 清不掉就算了，不影响功能
            }
        }
    }

    /// <summary>
    /// 面板自己画一个光标点：游戏营业时会把系统光标藏起来、自己画像素光标，
    /// 万一底层的光标没画出来，至少玩家还能看见鼠标在哪（暗边 + 白点 + 暖色芯）。
    /// </summary>
    private static void DrawOwnCursor(Rect panel)
    {
        var current = Event.current;
        if (current == null || !panel.Contains(current.mousePosition)) return;

        var p = current.mousePosition;
        DrawLine(new Rect(p.x - 3f, p.y - 3f, 7f, 7f), new Color(0f, 0f, 0f, 0.65f));
        DrawLine(new Rect(p.x - 2f, p.y - 2f, 5f, 5f), new Color(1f, 1f, 1f, 0.9f));
        DrawLine(new Rect(p.x - 1f, p.y - 1f, 3f, 3f), new Color(0.95f, 0.85f, 0.55f, 1f));
    }

    private static Vector2 ClampPosition(Vector2 position, Vector2 size) =>
        new(
            Mathf.Clamp(position.x, 0f, Mathf.Max(0f, Screen.width - size.x)),
            Mathf.Clamp(position.y, 0f, Mathf.Max(0f, Screen.height - size.y)));

    /// <summary>画一条实心横线/矩形（用白色贴图染成指定颜色）。</summary>
    private static void DrawLine(Rect rect, Color color)
    {
        var previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previousColor;
    }

    /// <summary>暖色木质底板：深棕半透明 + 一圈米金色细边（照着游戏里的食堂菜单配色）。</summary>
    private static void DrawPanelBackground(Rect panel)
    {
        // BackgroundAlpha 以 0.7 为基准，映射到用户给的 RGBA(60,40,30,200)
        var alpha = Mathf.Clamp01(GuestTagConfig.BackgroundAlpha.Value / 0.7f * (200f / 255f));
        const float border = 2f;

        var previousColor = GUI.color;
        try
        {
            GUI.color = new Color(0.87f, 0.77f, 0.52f, Mathf.Clamp01(alpha + 0.12f)); // 米金外框
            GUI.DrawTexture(panel, Texture2D.whiteTexture);

            GUI.color = new Color(60f / 255f, 40f / 255f, 30f / 255f, alpha); // 暖棕底
            GUI.DrawTexture(
                new Rect(panel.x + border, panel.y + border, panel.width - border * 2f, panel.height - border * 2f),
                Texture2D.whiteTexture);
        }
        finally
        {
            GUI.color = previousColor;
        }
    }

    private static string FormatTagsRaw(SpecialGuestTracking.SeenGuest guest)
    {
        if (guest.Tags == null || guest.Tags.Length == 0)
        {
            return guest.StringId.Length > 0
                ? "<color=#BFAF8E>喜好　未收录</color>"
                : "<color=#BFAF8E>喜好　未知</color>";
        }

        var tags = guest.Tags;
        var joined = tags.Length > MaxVisibleTags
            ? string.Join(" · ", tags.Take(MaxVisibleTags)) + " …"
            : string.Join(" · ", tags);

        return "<color=#F2E6C9>喜好　" + Escape(joined) + "</color>";
    }

    /// <summary>厌恶标签行（浅红）；没有厌恶标签返回空串，调用方就不加这一行。</summary>
    private static string FormatHateTagsRaw(SpecialGuestTracking.SeenGuest guest)
    {
        var hates = guest.HateTags;
        if (hates.Length == 0)
        {
            return string.Empty;
        }

        var joined = hates.Length > MaxVisibleTags
            ? string.Join(" · ", hates.Take(MaxVisibleTags)) + " …"
            : string.Join(" · ", hates);

        // 暗粉 / 浅红，跟喜好的米白、推荐的绿、酒水的橙都区分开
        return "<color=#FF9E9E>厌恶　" + Escape(joined) + "</color>";
    }

    /// <summary>推荐菜品：按当前策略排好序的一行，前缀跟着策略变（低价优先 / 高价优先 / 少料易做）。</summary>
    private static string FormatRecommendationsRaw(SpecialGuestTracking.SeenGuest guest)
    {
        if (!guest.HasFoodPreference)
        {
            // m03688：新 DLC 的稀客如果连运行时标签都读不到，明确告诉玩家「数据未收录」，不要静默摆烂
            return "<color=#FFB86B>[新稀客] 数据未收录</color>";
        }

        if (guest.Recommendations.Length == 0)
        {
            return "<color=#BFAF8E>[推荐菜品] 无匹配</color>";
        }

        var label = string.IsNullOrEmpty(guest.FoodLabel) ? "推荐菜品" : guest.FoodLabel;
        return "<color=#9BE86B>[" + Escape(label) + "] " +
               Escape(string.Join("、", guest.Recommendations)) + "</color>";
    }

    /// <summary>推荐酒水：同样按策略排序，没有匹配时整行不画。</summary>
    private static string FormatBeveragesRaw(SpecialGuestTracking.SeenGuest guest)
    {
        var label = string.IsNullOrEmpty(guest.BeverageLabel) ? "推荐酒水" : guest.BeverageLabel;
        return "<color=#7EC8E3>[" + Escape(label) + "] " +
               Escape(string.Join("、", guest.BeverageRecommendations)) + "</color>";
    }


    // ── 带缓存的行格式化（m03065：只在新数据到来时重建字符串） ──

    private static string FormatTags(SpecialGuestTracking.SeenGuest guest)
        => CachedLine(guest.StringId, "tags", guest.Tags, _ => FormatTagsRaw(guest));

    private static string FormatHateTags(SpecialGuestTracking.SeenGuest guest)
        => CachedLine(guest.StringId, "hate", guest.HateTags, _ => FormatHateTagsRaw(guest));

    private static string FormatRecommendations(SpecialGuestTracking.SeenGuest guest)
        => CachedLine(guest.StringId, "food|" + (guest.FoodLabel ?? string.Empty), guest.Recommendations,
            _ => FormatRecommendationsRaw(guest));

    private static string FormatBeverages(SpecialGuestTracking.SeenGuest guest)
        => CachedLine(guest.StringId, "bev|" + (guest.BeverageLabel ?? string.Empty),
            guest.BeverageRecommendations, _ => FormatBeveragesRaw(guest));

    /// <summary>用当前字号的 <see cref="GUIStyle"/> 量这段富文本要占多高（自动换行）。</summary>
    private static float Measure(GUIStyle style, string? text, float minHeight, float innerWidth)
    {
        if (string.IsNullOrEmpty(text)) return minHeight;

        try
        {
            var width = Mathf.Max(60f, innerWidth);
            return Mathf.Max(minHeight, style.CalcHeight(new GUIContent(text), width) + 2f);
        }
        catch
        {
            return minHeight;
        }
    }

    private static Styles? EnsureStyles(int size)
    {
        if (StyleCache.TryGetValue(size, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            var font = LoadCjkFont(size);
            var styles = new Styles
            {
                Title = BuildStyle(font, size, new Color(0.95f, 0.84f, 0.58f), bold: true),
                Name = BuildStyle(font, size, new Color(1f, 0.88f, 0.4f), bold: false),
                Line = BuildStyle(font, size, new Color(0.95f, 0.9f, 0.79f), bold: false),
            };

            StyleCache[size] = styles;
            return styles;
        }
        catch (Exception ex)
        {
            if (!_styleFailureLogged)
            {
                _styleFailureLogged = true;
                Plugin.Logger.LogWarning($"{HarmonyTag} 创建 GUIStyle 失败，面板不会显示: {ex.Message}");
            }

            return null;
        }
    }

    private static GUIStyle BuildStyle(Font? font, int size, Color color, bool bold)
    {
        var style = new GUIStyle(GUI.skin != null ? GUI.skin.label : new GUIStyle())
        {
            fontSize = size,
            richText = true,
            wordWrap = true,
            alignment = TextAnchor.UpperLeft,
            fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
            normal = { textColor = color },
        };

        style.padding.left = 0;
        style.padding.right = 0;
        style.padding.top = 0;
        style.padding.bottom = 0;
        style.margin.left = 0;
        style.margin.right = 0;
        style.margin.top = 0;
        style.margin.bottom = 0;

        if (font != null)
        {
            style.font = font;
        }

        return style;
    }

    private static Font? LoadCjkFont(int size)
    {
        if (_font != null) return _font;

        try
        {
            // 默认 GUI 字体（Arial）不带中文字形，会全变成方块，所以显式挂系统的微软雅黑等字体。
            var candidates = new[]
            {
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "SimHei",
                "SimSun",
                "Segoe UI",
            };

            _font = Font.CreateDynamicFontFromOSFont(candidates, size);
            if (_font != null)
            {
                Plugin.Logger.LogDebug($"{HarmonyTag} 面板字体已加载: {_font.name}");
            }
            else
            {
                Plugin.Logger.LogWarning($"{HarmonyTag} 没找到可用的中文字体，面板里的中文可能显示为方块");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{HarmonyTag} 加载中文字体失败（用默认字体）: {ex.Message}");
        }

        return _font;
    }

    /// <summary>面板矩形：Middle* 竖向居中；Bottom* 底边再往上让出 BottomMargin。</summary>
    private static Rect ResolvePanelRect(float width, float height)
    {
        // 只压宽度；高度由 TryBuildLayout 保证放得下，这里再压会把内容顶出底边
        width = Mathf.Min(width, Screen.width - Margin * 2f);

        var corner = GuestTagConfig.ResolveCorner();
        var bottomInset = Mathf.Clamp(GuestTagConfig.BottomMargin.Value, 0, 600);

        float x;
        float y;

        switch (corner)
        {
            case OverlayCorner.TopLeft:
                x = Margin;
                y = Margin + TopInset;
                break;
            case OverlayCorner.TopRight:
                x = Screen.width - width - Margin;
                y = Margin + TopInset;
                break;
            case OverlayCorner.BottomLeft:
                x = Margin;
                y = Screen.height - bottomInset - Margin - height;
                break;
            case OverlayCorner.BottomRight:
                x = Screen.width - width - Margin;
                y = Screen.height - bottomInset - Margin - height;
                break;
            case OverlayCorner.MiddleRight:
                x = Screen.width - width - Margin;
                y = (Screen.height - height) * 0.5f;
                break;
            default: // MiddleLeft
                x = Margin;
                y = (Screen.height - height) * 0.5f;
                break;
        }

        // 拖拽过的位置优先；没拖过就用 cfg 里上次保存的坐标
        if (_panelPosition.HasValue)
        {
            var dragged = ClampPosition(_panelPosition.Value, new Vector2(width, height));
            return new Rect(dragged.x, dragged.y, width, height);
        }

        if (GuestTagConfig.TryGetSavedPosition(out var savedX, out var savedY))
        {
            var saved = ClampPosition(new Vector2(savedX, savedY), new Vector2(width, height));
            return new Rect(saved.x, saved.y, width, height);
        }

        // 保证不出屏
        x = Mathf.Clamp(x, Margin, Mathf.Max(Margin, Screen.width - width - Margin));
        y = Mathf.Clamp(y, Margin, Mathf.Max(Margin, Screen.height - height - Margin));

        return new Rect(x, y, width, height);
    }

    private static float AvailableHeight()
    {
        var corner = GuestTagConfig.ResolveCorner();

        return corner switch
        {
            OverlayCorner.TopLeft or OverlayCorner.TopRight => Screen.height - Margin * 2f,
            OverlayCorner.BottomLeft or OverlayCorner.BottomRight =>
                Screen.height - Mathf.Clamp(GuestTagConfig.BottomMargin.Value, 0, 600) - Margin,
            _ => Screen.height - Margin * 2f,
        };
    }

    private static string ColorFor(string stringId)
    {
        var hash = 0;
        foreach (var ch in stringId)
        {
            hash = unchecked(hash * 31 + ch);
        }

        var index = (hash & 0x7fffffff) % NameColors.Length; // Math.Abs(int.MinValue) 会抛 OverflowException
        return NameColors[index];
    }

    /// <summary>IMGUI 的富文本会把 &lt; 当标签，名字里出现就转义掉。</summary>
    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return text!.Replace("<", "‹").Replace(">", "›");
    }
}
