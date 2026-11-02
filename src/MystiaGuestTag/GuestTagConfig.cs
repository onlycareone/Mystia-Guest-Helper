using BepInEx.Configuration;

namespace MystiaGuestTag;

/// <summary>推荐策略：价格高的优先 / 价格低的优先 / 需要的食材种类少的优先。</summary>
internal enum RecommendStrategy
{
    PriceLow,
    PriceHigh,
    FewIngredients,
}

internal enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    MiddleLeft,
    MiddleRight,
}

/// <summary>
/// 面板外观配置（BepInEx\\config\\cc.mystia.guesttag.cfg，游戏内改完重开生效）。
/// 默认停靠屏幕中左侧竖向居中：右下角会压住游戏底部金钱栏 / 按键提示，右上角被自带 HUD 占着，
/// 左侧中部既碰不到 COMBO 区，也不会盖住底部操作提示。
/// </summary>
internal static class GuestTagConfig
{
    internal static ConfigEntry<string> Corner = null!;
    internal static ConfigEntry<int> PanelWidth = null!;
    internal static ConfigEntry<int> FontSize = null!;

    /// <summary>是否启用内置查表名单（m03688，默认 false = 纯动态）。</summary>
    internal static ConfigEntry<bool> UseCuratedHints = null!;

    /// <summary>厨具手填兜底开关（m03688）。</summary>
    internal static ConfigEntry<bool> UseCookwareOverride = null!;

    /// <summary>手填厨具名单，例如 `煮锅 = 2; 油锅 = 1`。</summary>
    internal static ConfigEntry<string> CookwareOverride = null!;
    internal static ConfigEntry<float> BackgroundAlpha = null!;
    internal static ConfigEntry<int> BottomMargin = null!;
    internal static ConfigEntry<int> MaxWidth = null!;

    /// <summary>上次拖拽后记住的面板左上角坐标；-1 表示按 Corner 自动算。</summary>
    internal static ConfigEntry<int> PositionX = null!;
    internal static ConfigEntry<int> PositionY = null!;

    /// <summary>每个稀客最多显示几条推荐菜品 / 几条推荐酒水。</summary>
    internal static ConfigEntry<int> MaxRecipesPerGuest = null!;
    internal static ConfigEntry<int> MaxDrinksPerGuest = null!;

    /// <summary>面板上最多同时显示几位稀客，超出的折叠进标题里的「已显示 x/y 位」。</summary>
    internal static ConfigEntry<int> MaxGuestsToDisplay = null!;

    /// <summary>玩家自定义价格覆盖（「菜名 = 价格」，分号分隔）。</summary>
    internal static ConfigEntry<string> CustomPrices = null!;

    /// <summary>推荐策略（面板顶部策略按钮循环切换，改完立刻写回 cfg）。</summary>
    internal static ConfigEntry<string> Strategy = null!;

    private static ConfigFile? _file;
    internal static ConfigEntry<string> Layout = null!;

    internal static void Bind(ConfigFile config)
    {
        _file = config;

        Corner = config.Bind(
            "UI",
            "Corner",
            nameof(OverlayCorner.MiddleLeft),
            "面板停靠位置（还没拖过时的初始位置）：MiddleLeft（中左，默认）/ TopLeft / TopRight / MiddleRight / BottomLeft / BottomRight");

        Layout = config.Bind(
            "UI",
            "Layout",
            "Single",
            "稀客多时的排版：Single（单列，默认）/ TwoColumns（两列并排）");

        PanelWidth = config.Bind(
            "UI",
            "PanelWidth",
            380,
            new ConfigDescription("单列宽度（像素）：文字超过这个宽度会自动换行，别设太大免得横向拉太长", new AcceptableValueRange<int>(220, 900)));

        MaxWidth = config.Bind(
            "UI",
            "MaxWidth",
            560,
            new ConfigDescription(
                "面板最大宽度（像素）：实际宽度取 min(PanelWidth, MaxWidth, 屏幕宽度×0.45)，超出的文字自动换行",
                new AcceptableValueRange<int>(200, 1200)));

        FontSize = config.Bind(
            "UI",
            "FontSize",
            16,
            new ConfigDescription("正文字号（16 是当前默认值；嫌占地方就往下调 1~2 号）", new AcceptableValueRange<int>(10, 32)));

        BackgroundAlpha = config.Bind(
            "UI",
            "BackgroundAlpha",
            0.7f,
            new ConfigDescription("黑色底框不透明度（0=全透明，1=纯黑）", new AcceptableValueRange<float>(0f, 1f)));

        BottomMargin = config.Bind(
            "UI",
            "BottomMargin",
            250,
            new ConfigDescription(
                "底部留白（像素）：仅 BottomLeft / BottomRight 生效，面板底边距离屏幕底部的距离；调大 = 面板整体往上提",
                new AcceptableValueRange<int>(0, 600)));

        MaxRecipesPerGuest = config.Bind(
            "Display",
            "MaxRecipesPerGuest",
            3,
            new ConfigDescription("每个稀客最多显示几条推荐菜品", new AcceptableValueRange<int>(1, 10)));

        MaxDrinksPerGuest = config.Bind(
            "Display",
            "MaxDrinksPerGuest",
            3,
            new ConfigDescription("每个稀客最多显示几条推荐酒水（客人一晚会点好几杯，默认给 3 款不同的）", new AcceptableValueRange<int>(1, 10)));

        MaxGuestsToDisplay = config.Bind(
            "Display",
            "MaxGuestsToDisplay",
            3,
            new ConfigDescription(
                "面板上最多同时显示几位稀客；超出的会折叠，标题里显示「已显示 x/y 位」。设 1 只显示最近一位，面板高度也会自动缩短",
                new AcceptableValueRange<int>(1, 10)));

        CustomPrices = config.Bind(
            "Prices",
            "CustomPrices",
            string.Empty,
            "自定义价格覆盖：每项写成「菜名 = 价格」，多项用分号隔开，例如：冷豆腐 = 20; 二天一流 = 120。"
            + "这里填了的菜按这个价格显示，没填的仍然读游戏里的实际售价（TrueValue）。改完重开游戏生效。");

        Strategy = config.Bind(
            "Display",
            "Strategy",
            nameof(RecommendStrategy.PriceLow),
            new ConfigDescription(
                "推荐策略：PriceLow 低价优先（清库存）/ PriceHigh 高价优先（狠狠赚一笔）/ FewIngredients 少料易做（优先食材种类少的菜）；面板顶部的 [策略: …] 按钮可以循环切换",
                new AcceptableValueList<string>(
                    nameof(RecommendStrategy.PriceLow),
                    nameof(RecommendStrategy.PriceHigh),
                    nameof(RecommendStrategy.FewIngredients))));

        PositionX = config.Bind(
            "UI",
            "UI_X",
            -1,
            "面板左上角 X 坐标（拖动面板标题栏后会自动写回这里；-1 = 按 Corner 自动算）");

        UseCuratedHints = config.Bind(
            "Display",
            "UseCuratedHints",
            false,
            "是否使用内置的「夜雀食堂小助手」查表名单（魔理沙/莉格露/露米娅/阿求/橙）。"
            + "默认 false = 完全按稀客的喜好标签 + 预算 + 食材 + 厨具动态过滤，新 DLC 稀客无需改代码；"
            + "改成 true 时，低价优先/少料易做会优先采用名单里的菜（仍然受预算与食材约束）。");

        UseCookwareOverride = config.Bind(
            "Cookware",
            "UseCookwareOverride",
            true,
            "读不到游戏里的厨具时，是否允许用下面手填的 [Cookware] Override 名单来过滤菜谱。");

        CookwareOverride = config.Bind(
            "Cookware",
            "Override",
            string.Empty,
            "手填今晚带的厨具（读不到游戏数据时的兜底）。写成「煮锅 = 2; 油锅 = 1」，"
            + "数字只作备注，插件按「种类」过滤：煮锅(Pot) / 烤架(Grill) / 油锅(Fryer) / 蒸笼(Steamer) / 砧板(CuttingBoard)。"
            + "留空 = 不启用，完全读游戏里台上的厨具。改完重开游戏生效。");

        PositionY = config.Bind(
            "UI",
            "UI_Y",
            -1,
            "面板左上角 Y 坐标（拖动面板标题栏后会自动写回这里；-1 = 按 Corner 自动算）");
    }

    /// <summary>拖拽结束时把新坐标写回 cfg，下次启动沿用。</summary>
    internal static void SavePosition(float x, float y)
    {
        try
        {
            var roundedX = UnityEngine.Mathf.RoundToInt(x);
            var roundedY = UnityEngine.Mathf.RoundToInt(y);
            if (PositionX.Value == roundedX && PositionY.Value == roundedY) return;

            PositionX.Value = roundedX;
            PositionY.Value = roundedY;
            _file?.Save();

            Plugin.Logger.LogDebug($"{Plugin.PluginName} 面板位置已保存: ({roundedX}, {roundedY})");
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogWarning($"{Plugin.PluginName} 保存面板位置失败: {ex.Message}");
        }
    }

    /// <summary>有没有用户拖出来的坐标。</summary>
    internal static bool TryGetSavedPosition(out int x, out int y)
    {
        x = PositionX.Value;
        y = PositionY.Value;
        return x >= 0 && y >= 0;
    }

    internal static OverlayCorner ResolveCorner()
    {
        return System.Enum.TryParse<OverlayCorner>(Corner.Value, true, out var corner)
            ? corner
            : OverlayCorner.MiddleLeft;
    }

    /// <summary>读配置里的上限，顺手夹到合法范围（玩家手改 cfg 写成 0 或 999 也不会崩）。</summary>
    /// <summary>
    /// 游戏内设置面板保存时调用：先改内存里的值（下一帧 UI 立刻按新值排版），再写回 cfg 文件。
    /// 任何异常都不往外抛，最多丢一次落盘。
    /// </summary>
    internal static void ApplyDisplaySettings(int maxGuests, int maxRecipes, int maxDrinks)
    {
        MaxGuestsToDisplay.Value = System.Math.Clamp(maxGuests, 1, 10);
        MaxRecipesPerGuest.Value = System.Math.Clamp(maxRecipes, 1, 10);
        MaxDrinksPerGuest.Value = System.Math.Clamp(maxDrinks, 1, 10);

        try
        {
            _file?.Save();
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogWarning($"[GuestTag] 设置写回 cfg 失败（本次游戏内仍生效）: {ex.Message}");
        }
    }

    internal static int GetMaxRecipes() => System.Math.Clamp(MaxRecipesPerGuest.Value, 1, 10);

    internal static int GetMaxDrinks() => System.Math.Clamp(MaxDrinksPerGuest.Value, 1, 10);

    internal static int GetMaxGuests() => System.Math.Clamp(MaxGuestsToDisplay.Value, 1, 10);

    /// <summary>读当前策略；cfg 被手改坏了就退回低价优先。</summary>
    internal static RecommendStrategy GetStrategy()
    {
        return (Strategy.Value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pricehigh" => RecommendStrategy.PriceHigh,
            "fewingredients" => RecommendStrategy.FewIngredients,
            _ => RecommendStrategy.PriceLow,
        };
    }

    /// <summary>点一下策略按钮：低价优先 → 高价优先 → 少料易做 → 低价优先。</summary>
    internal static RecommendStrategy NextStrategy(RecommendStrategy current) => current switch
    {
        RecommendStrategy.PriceLow => RecommendStrategy.PriceHigh,
        RecommendStrategy.PriceHigh => RecommendStrategy.FewIngredients,
        _ => RecommendStrategy.PriceLow,
    };

    internal static string StrategyDisplayName(RecommendStrategy strategy) => strategy switch
    {
        RecommendStrategy.PriceHigh => "高价优先",
        RecommendStrategy.FewIngredients => "少料易做",
        _ => "低价优先",
    };

    /// <summary>策略按钮落账：写内存 + 落盘，下一帧推荐就按新策略重排。</summary>
    internal static RecommendStrategy ApplyStrategy(RecommendStrategy strategy)
    {
        Strategy.Value = strategy.ToString();
        try
        {
            _file?.Save();
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogWarning($"[GuestTag] 策略写回 cfg 失败（本次游戏内仍生效）: {ex.Message}");
        }

        return strategy;
    }

    internal static bool UseTwoColumns()
    {
        return string.Equals(Layout.Value?.Trim(), "TwoColumns", System.StringComparison.OrdinalIgnoreCase);
    }
}
