using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace MystiaGuestTag;

/// <summary>
/// 《东方夜雀食堂》稀客喜好标签提示插件（BepInEx 6 IL2CPP）。
///
/// 取数链路（全部经 interop 元数据核实，见 tools/dump_type.ps1）：
///   NightScene.GuestManagementUtility.GuestsManager.SpawnSpecialGuestGroup(...)
///     → 返回 SpecialGuestsController
///     → SpecialGuestsController.SpecialGuest（GameData.Core.Collections.NightSceneUtility.SpecialGuest）
///     → SpecialGuest.stringId / SpecialGuest.Id
/// 绘制链路：
///   Harmony postfix 挂到 NightScene.SceneManager.OnGUI()（营业场景常驻的私有 OnGUI），
///   在 postfix 里用 IMGUI 画右上角面板——不注入自定义 MonoBehaviour，
///   规避 Il2CppInterop 注入类 Update/OnGUI 不触发的已知问题。
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "cc.mystia.guesttag";
    public const string PluginName = "Mystia Guest Tag";
    public const string PluginVersion = "1.2.3";

    internal static ManualLogSource Logger = null!;

    public override void Load()
    {
        Logger = base.Log;
        Logger.LogInfo($"{PluginName} v{PluginVersion} 载入中……");

        GuestTagConfig.Bind(Config);

        var harmony = new Harmony(PluginGuid);

        // Hook 点 1：稀客生成（营业场景顾客生成逻辑）——本插件的主取数点
        SpecialGuestTracking.Install(harmony);

        // Hook 点 2：营业场景的常驻 OnGUI —— 面板绘制点
        GuestTagOverlay.Install(harmony);

        // 附带：把游戏里的全量稀客权重数据导出成 JSON（数据加载完就写，只写一次）
        SpecialGuestExport.Install(harmony);
        SpecialGuestExport.TryExportOnce();

        Logger.LogInfo($"{PluginName} 已就绪：面板顶部 [-]/[+] 调显示人数与菜/酒条数，下面一行 [策略: …] 循环切换低价优先/高价优先/少料易做（都立即写入 cfg）；鼠标在面板上滚轮翻页（到底回顶），按住标题栏可拖动，F10 隐藏/显示，F9 打开滑条版设置。");
    }
}
