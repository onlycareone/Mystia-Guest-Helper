using System;
using HarmonyLib;
using UnityEngine;

namespace MystiaGuestTag;

/// <summary>
/// m05835：鼠标停在插件面板上时，把游戏的键盘输入「吃掉」，避免按一个键就弹到别的页面。
/// 做法是给 UnityEngine.Input.GetKeyDown 挂 prefix —— 鼠标在面板上时直接返回 false，
/// 插件自己读键（F10 / ↑↓）时临时打开 AllowKeys，不受影响。
/// </summary>
internal static class InputBlocker
{
    private const string Tag = "[GuestTag]";

    /// <summary>鼠标这一帧是否压在面板上（由 GuestTagOverlay 每帧更新）。</summary>
    internal static bool MouseOverPanel;

    /// <summary>插件自己在读键时置 true，避免被自己的拦截误伤。</summary>
    internal static bool AllowKeys;

    internal static void Install(Harmony harmony)
    {
        try
        {
            var patched = 0;

            var keyCode = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) });
            if (keyCode != null)
            {
                harmony.Patch(keyCode, prefix: new HarmonyMethod(typeof(InputBlocker), nameof(GetKeyDownPrefix)));
                patched++;
            }

            var keyName = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(string) });
            if (keyName != null)
            {
                harmony.Patch(keyName, prefix: new HarmonyMethod(typeof(InputBlocker), nameof(GetKeyDownPrefix)));
                patched++;
            }

            Plugin.Logger.LogInfo($"{Tag} 已挂载键盘拦截点：{patched} 个 Input.GetKeyDown 重载");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"{Tag} 键盘拦截挂载失败（不影响面板功能）: {ex.Message}");
        }
    }

    private static bool GetKeyDownPrefix(ref bool __result)
    {
        if (AllowKeys || !MouseOverPanel) return true;

        __result = false;
        return false;
    }

    /// <summary>插件自己读键时用，确保按键不被自己的拦截吞掉。</summary>
    internal static IDisposable Reading()
    {
        var previous = AllowKeys;
        AllowKeys = true;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly bool _previous;

        internal Restore(bool previous) => _previous = previous;

        public void Dispose() => AllowKeys = _previous;
    }
}
