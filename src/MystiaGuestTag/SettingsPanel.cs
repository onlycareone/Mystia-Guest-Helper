using System;
using UnityEngine;

namespace MystiaGuestTag;

/// <summary>
/// 游戏内设置面板：按 F9 唤出，用 GUILayout 画滑条，点「保存」立刻生效并写回 cfg。
/// 风格沿用稀客面板的木质暗金：深棕底 + 米金边框 + 明黄标题。
/// </summary>
internal static class SettingsPanel
{
    private const string HarmonyTag = "[GuestTag]";

    private const KeyCode ToggleKey = KeyCode.F9;

    /// <summary>窗口尺寸——高度按控件数量算死，省得 GUILayout 自适应用法踩坑。</summary>
    private const float WindowWidth = 380f;
    private const float WindowHeight = 268f;
    private const float TitleHeight = 30f;
    private const float Pad = 14f;

    private static bool _open;
    private static Rect _rect = new Rect(120f, 120f, WindowWidth, WindowHeight);
    private static Vector2? _dragOffset;

    // 编辑中的草稿值：点保存才写进 cfg，点取消直接丢弃
    private static int _maxGuests = 3;
    private static int _maxCombos = 3;

    private static GUIStyle? _titleStyle;
    private static GUIStyle? _labelStyle;
    private static GUIStyle? _valueStyle;
    private static GUIStyle? _hintStyle;
    private static GUIStyle? _buttonStyle;
    private static Texture2D? _buttonTexture;
    private static Font? _font;
    private static bool _styleFailed;

    internal static bool IsOpen => _open;

    /// <summary>每帧在 OnGUI 里先调它：只在 Layout 事件里轮询按键，避免同一帧被 OnGUI 多次调用重复翻转。</summary>
    internal static void HandleInput()
    {
        try
        {
            var currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.Layout) return;

            if (Input.GetKeyDown(ToggleKey))
            {
                Toggle();
            }
        }
        catch (Exception ex)
        {
            if (!_styleFailed)
            {
                _styleFailed = true;
                Plugin.Logger.LogError($"{HarmonyTag} 设置面板按键检测失败: {ex}");
            }
        }
    }

    internal static void Toggle()
    {
        if (_open)
        {
            _open = false;
            return;
        }

        // 每次打开都从 cfg 读当前值，保证面板里看到的就是实际生效的
        _maxGuests = GuestTagConfig.GetMaxGuests();
        _maxCombos = GuestTagConfig.GetCombos();
        _open = true;
    }

    internal static void Draw()
    {
        if (!_open) return;

        try
        {
            if (!EnsureStyles()) return;

            HandleDrag();

            // 底：米金外框 + 深棕内芯，和稀客面板同一套配色
            Fill(_rect, new Color(0.87f, 0.77f, 0.52f, 0.95f));
            Fill(new Rect(_rect.x + 2f, _rect.y + 2f, _rect.width - 4f, _rect.height - 4f),
                new Color(0.24f, 0.16f, 0.10f, 0.96f));

            var titleBar = new Rect(_rect.x + 2f, _rect.y + 2f, _rect.width - 4f, TitleHeight - 2f);
            Fill(titleBar, new Color(0.35f, 0.24f, 0.14f, 0.98f));
            Fill(new Rect(titleBar.x, titleBar.yMax - 1f, titleBar.width, 1f), new Color(0.72f, 0.57f, 0.26f, 0.9f));

            GUI.Label(new Rect(titleBar.x + 10f, titleBar.y + 4f, titleBar.width - 80f, TitleHeight - 6f),
                "稀客助手 · 设置", _titleStyle);

            if (GUI.Button(new Rect(titleBar.xMax - 26f, titleBar.y + 4f, 22f, 20f), "×", _buttonStyle))
            {
                _open = false;
                return;
            }

            var area = new Rect(_rect.x + Pad, _rect.y + TitleHeight + 10f,
                _rect.width - Pad * 2f, _rect.height - TitleHeight - 18f);

            GUILayout.BeginArea(area);
            GUILayout.Label("拖动滑条调整，点「保存并关闭」即时生效并写回 cfg。", _hintStyle);
            GUILayout.Space(8f);

            // 套餐模式：菜品+酒水合成「套餐」，上限 3（游戏里稀客一般一道菜配一杯酒）
            _maxGuests = Slider("最多显示稀客", _maxGuests, 1, 10);
            _maxCombos = Slider("每人推荐套餐数", _maxCombos, 1, 3);

            GUILayout.Space(12f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("保存并关闭", _buttonStyle, GUILayout.Height(28f)))
            {
                Save();
            }

            if (GUILayout.Button("取消", _buttonStyle, GUILayout.Height(28f)))
            {
                _open = false;
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label($"{ToggleKey} 开关本面板　·　F10 隐藏/显示稀客面板　·　滚轮翻页", _hintStyle);
            GUILayout.EndArea();
        }
        catch (Exception ex)
        {
            if (!_styleFailed)
            {
                _styleFailed = true;
                Plugin.Logger.LogError($"{HarmonyTag} 设置面板绘制异常（后续静默）: {ex}");
            }
        }
    }

    private static int Slider(string label, int value, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(150f));
        var next = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max, GUILayout.Height(20f)));
        GUILayout.Label(next.ToString(), _valueStyle, GUILayout.Width(28f));
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);
        return Mathf.Clamp(next, min, max);
    }

    private static void Save()
    {
        try
        {
            GuestTagConfig.ApplyDisplaySettings(_maxGuests, _maxCombos);

            // 推荐结果是按 stringId 缓存的，条数变了要立刻重算，不然要等 30 秒
            SetMatchRecommender.ClearCache();

            Plugin.Logger.LogInfo(
                $"{HarmonyTag} 设置已保存：显示稀客 {_maxGuests} 位，每人推荐 {_maxCombos} 个套餐");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"{HarmonyTag} 保存设置失败: {ex}");
        }

        _open = false;
    }

    private static void HandleDrag()
    {
        var currentEvent = Event.current;
        if (currentEvent == null) return;

        var titleBar = new Rect(_rect.x + 2f, _rect.y + 2f, _rect.width - 4f, TitleHeight - 2f);

        switch (currentEvent.type)
        {
            case EventType.MouseDown when currentEvent.button == 0 && titleBar.Contains(currentEvent.mousePosition):
                _dragOffset = currentEvent.mousePosition - _rect.position;
                currentEvent.Use();
                break;

            case EventType.MouseDrag when _dragOffset.HasValue:
                _rect.position = Clamp(currentEvent.mousePosition - _dragOffset.Value);
                currentEvent.Use();
                break;

            case EventType.MouseUp when _dragOffset.HasValue:
                _dragOffset = null;
                currentEvent.Use();
                break;
        }
    }

    private static Vector2 Clamp(Vector2 position)
    {
        var maxX = Mathf.Max(0f, Screen.width - _rect.width);
        var maxY = Mathf.Max(0f, Screen.height - _rect.height);
        return new Vector2(Mathf.Clamp(position.x, 0f, maxX), Mathf.Clamp(position.y, 0f, maxY));
    }

    private static void Fill(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static bool EnsureStyles()
    {
        if (_titleStyle != null) return true;

        try
        {
            _font ??= Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun", "Segoe UI" }, 16);

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                font = _font,
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                richText = true,
            };
            _titleStyle.normal.textColor = new Color(0.95f, 0.84f, 0.58f);

            _labelStyle = new GUIStyle(_titleStyle) { fontSize = 14, fontStyle = FontStyle.Normal };
            _labelStyle.normal.textColor = new Color(0.95f, 0.90f, 0.78f);

            _valueStyle = new GUIStyle(_labelStyle) { alignment = TextAnchor.MiddleRight };
            _valueStyle.normal.textColor = new Color(1f, 0.88f, 0.40f);

            _hintStyle = new GUIStyle(_labelStyle) { fontSize = 12 };
            _hintStyle.normal.textColor = new Color(0.72f, 0.66f, 0.55f);
            _hintStyle.wordWrap = true;

            _buttonTexture = MakeTexture(new Color(0.42f, 0.29f, 0.16f, 1f));
            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                font = _font,
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
            };
            _buttonStyle.normal.background = _buttonTexture;
            _buttonStyle.hover.background = _buttonTexture;
            _buttonStyle.active.background = _buttonTexture;
            _buttonStyle.normal.textColor = new Color(0.98f, 0.92f, 0.78f);
            _buttonStyle.hover.textColor = new Color(1f, 0.88f, 0.40f);
            _buttonStyle.active.textColor = new Color(1f, 0.88f, 0.40f);
            // 逐项赋 border，别用 4 参数构造（Il2Cpp 侧 RectOffset 的构造函数签名不全）
            var border = new RectOffset();
            border.left = 2;
            border.right = 2;
            border.top = 2;
            border.bottom = 2;
            _buttonStyle.border = border;
            return true;
        }
        catch (Exception ex)
        {
            if (!_styleFailed)
            {
                _styleFailed = true;
                Plugin.Logger.LogError($"{HarmonyTag} 设置面板样式初始化失败: {ex}");
            }

            return false;
        }
    }

    private static Texture2D MakeTexture(Color color)
    {
        var texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }
}
