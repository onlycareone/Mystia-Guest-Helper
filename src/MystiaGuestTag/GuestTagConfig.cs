using BepInEx.Configuration;

namespace MystiaGuestTag;

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

    /// <summary>是否在推荐菜后面显示「主料 + 加料建议」（v1.3.0 起走本地 JSON）。</summary>
    internal static ConfigEntry<bool> ShowIngredients = null!;

    /// <summary>推荐池来源：Auto = 已解锁菜谱优先，读不到退回今晚菜单；Tonight = 只用今晚菜单；All = 全菜谱。</summary>
    internal static ConfigEntry<string> RecipePoolMode = null!;

    /// <summary>总开关（m07921）：false 时插件完全不干活，用来做 A/B 排查。</summary>
    internal static ConfigEntry<bool> Enabled = null!;

    /// <summary>是否输出调试日志（m07921）：默认关。关掉时连字符串都不会拼接。</summary>
    internal static ConfigEntry<bool> VerboseLog = null!;

    /// <summary>是否只用背包食材做得出的菜（主料一样不缺）。</summary>
    internal static ConfigEntry<bool> RequireIngredients = null!;

    /// <summary>每位稀客显示几个「套餐」（一道菜 + 一杯酒），上限 3。</summary>

    internal static ConfigEntry<int> MaxCombos = null!;

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

        MaxCombos = config.Bind(
            "Display",
            "MaxCombos",
            3,
            new ConfigDescription(
                "每位稀客显示几个套餐（一道菜 + 一杯酒）。游戏里稀客一般一道菜配一杯酒，所以上限只给到 3",
                new AcceptableValueRange<int>(1, 3)));

        ShowIngredients = config.Bind(
            "Display",
            "ShowIngredients",
            true,
            "推荐菜后面是否显示【主料: 鱼x1+肉x2】【加料: 肉·特产】。加料建议按稀客喜好标签 + 背包库存算，背包里没有的会标（缺 N 种）");

        RecipePoolMode = config.Bind(
            "UI",
            "RecipePoolMode",
            "Auto",
            "推荐池来源：Auto（默认）= 只推游戏里已解锁的菜谱（读 RunTimeStorage.Recipes），读不到时退回今晚菜单；"
            + "Tonight = 只在今晚菜单里挑；All = 全菜谱（不推荐，会推出没解锁的菜）");

        RequireIngredients = config.Bind(
            "UI",
            "RequireIngredients",
            true,
            "是否只推主料齐全的菜：true 时按背包食材库存过滤，缺主料的菜不进推荐池；库存读不到时自动跳过这道过滤");

        Enabled = config.Bind(
            "General",
            "Enabled",
            true,
            "总开关：关掉后插件不读稀客、不计算、不绘制（排查掉帧用）");

        VerboseLog = config.Bind(
            "Logging",
            "VerboseLog",
            false,
            "是否输出调试日志：默认关。关掉时日志字符串根本不会被拼接，不会吃 CPU");

        CustomPrices = config.Bind(
            "Prices",
            "CustomPrices",
            string.Empty,
            "自定义价格覆盖：每项写成「菜名 = 价格」，多项用分号隔开，例如：冷豆腐 = 20; 二天一流 = 120。"
            + "这里填了的菜按这个价格显示，没填的仍然读游戏里的实际售价（TrueValue）。改完重开游戏生效。");

        PositionX = config.Bind(
            "UI",
            "UI_X",
            -1,
            "面板左上角 X 坐标（拖动面板标题栏后会自动写回这里；-1 = 按 Corner 自动算）");

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
            + "数字只作备注，插件按「种类」过滤：煮锅(Pot) / 烧烤架(Grill) / 油锅(Fryer) / 蒸锅(Steamer) / 料理台(CuttingBoard)。"
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

            Diag.DbgSb(() => $"{Plugin.PluginName} 面板位置已保存: ({roundedX}, {roundedY})");
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
    internal static void ApplyDisplaySettings(int maxGuests, int combos)
    {
        var clampedGuests = System.Math.Clamp(maxGuests, 1, 10);
        var clampedCombos = System.Math.Clamp(combos, 1, 3);

        MaxGuestsToDisplay.Value = clampedGuests;
        MaxCombos.Value = clampedCombos;

        // 老键继续同步：老版本的 cfg / 第三方工具还认这两个名字（v1.3.0 起它们只受「套餐」数控制）
        MaxRecipesPerGuest.Value = clampedCombos;
        MaxDrinksPerGuest.Value = clampedCombos;

        try
        {
            _file?.Save();
        }
        catch (System.Exception ex)
        {
            Plugin.Logger.LogWarning($"[GuestTag] 设置写回 cfg 失败（本次游戏内仍生效）: {ex.Message}");
        }
    }

    /// <summary>套餐数（1~3）：一道菜 + 一杯酒算一个套餐。</summary>

    internal static int GetCombos() => System.Math.Clamp(MaxCombos.Value, 1, 3);

    // 推荐器只关心「要几条」，套餐模式下菜品和酒水都按套餐数取
    internal static int GetMaxRecipes() => GetCombos();

    internal static int GetMaxDrinks() => GetCombos();

    internal static int GetMaxGuests() => System.Math.Clamp(MaxGuestsToDisplay.Value, 1, 10);


    internal static bool UseTwoColumns()
    {
        return string.Equals(Layout.Value?.Trim(), "TwoColumns", System.StringComparison.OrdinalIgnoreCase);
    }
}
