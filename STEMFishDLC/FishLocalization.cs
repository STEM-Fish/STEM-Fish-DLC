using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace STEMFishDLC;

// ============================================================================
//  FishLocalization - 自定义物品的显示名 / 描述
//  ---------------------------------------------------------------------------
//  反汇编确认的显示链路：
//    物品名  = Locale.GetItem(id) → Locale.GetString(id, 0)  → Language.main[id]
//    描述    = PlayerCamera.ItemHoverDescription → Locale.GetOther(id + "dsc")
//  而 Locale.GetString 查不到时的行为是：**直接返回 key 本身**（安全，不崩）。
//  所以我们不需要改游戏的 json，只要在 "查不到（返回值 == key）" 时补上自己的文本：
//   patch Locale.GetString 的 postfix，命中我们注册的 key 就替换返回值。
//  → 优点：不依赖语言切换、不碰游戏语言表、只影响自定义 key。
// ============================================================================

public static class FishLocalization
{
    private const string HarmonyId = "com.stemfish.dlc.locale";

    // key → 文本（键是 "platecarrier" / "platecarrierdsc" 这种）
    private static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal);
    private static bool _patched;

    public static void Init()
    {
        if (_patched) return;
        try
        {
            var harmony = new Harmony(HarmonyId);
            var target = AccessTools.Method(typeof(Locale), "GetString");
            if (target == null)
            {
                Plugin.Log.LogError("[STEMFishDLC] 找不到 Locale.GetString，物品名会显示为 id");
                return;
            }
            var postfix = typeof(FishLocalization).GetMethod("GetString_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            // 液体名 / 描述走 GetOther；我们的 sfblood 不在游戏语言表里，需要在这条链上补
            var other = AccessTools.Method(typeof(Locale), "GetOther");
            if (other != null)
            {
                var opost = typeof(FishLocalization).GetMethod("GetOther_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(other, postfix: new HarmonyMethod(opost));
            }
            var info = Harmony.GetPatchInfo(target);
            _patched = true;

            if (!Lang.IsChinese)
            {
                Force["minilaserdrill"] = "Experimental Particle Cutter";
                Force["minilaserdrilldsc"] = "A prototype that leaked out of the company lab: the laser drill's focusing head replaced with a particle-cutting array. Rock cut by it vaporizes outright; the price is that it is now genuinely heavy.";
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 本地化注入 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 本地化 hook 失败: {e}");
        }
    }

    // 强制覆盖原版已有文本（血袋提示、激光钻改名等）
    public static void ForceText(string key, string text)
    {
        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(text)) Force[key] = text;
    }

    // 注册一个物品的名字 + 描述（描述键约定为 id + "dsc"）
    public static void Register(string id, string name, string desc)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!string.IsNullOrEmpty(name)) Map[id] = name;
        if (!string.IsNullOrEmpty(desc)) Map[id + "dsc"] = desc;
    }

    // 液体名 / 描述走 Locale.GetOther(localeName)，和物品的 GetString 不是同一条
    private static readonly Dictionary<string, string> OtherMap =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>注册一个液体的显示名 + 描述（id / id+"dsc"，对应游戏 other 表的结构）</summary>
    public static void RegisterLiquid(string id, string name, string desc)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!string.IsNullOrEmpty(name)) OtherMap[id] = name;
        if (!string.IsNullOrEmpty(desc)) OtherMap[id + "dsc"] = desc;
    }

    // 原版已有翻译、但我们想改名的 key（改完由 GetString_Postfix 无条件覆盖）
    private static readonly Dictionary<string, string> Force = new Dictionary<string, string>
    {
        { "minilaserdrill", "实验性粒子切割机" },
        { "minilaserdrilldsc", "公司实验室流出来的原型机：把激光钻的聚焦头换成了粒子切割阵列。切成块的岩层会直接气化，代价是它现在真的有点重。" },
    };

    // 用 __args 而不是具名参数，避免 Harmony "Parameter not found" 静默失败
    private static void GetString_Postfix(object[] __args, ref string __result)
    {
        try
        {
            if (__args == null || __args.Length == 0) return;
            var key = __args[0] as string;
            if (string.IsNullOrEmpty(key)) return;

            // ★ 强制改名表：原版**有**翻译的 key 也要盖掉（激光钻 → 粒子切割机）
            if (Force.TryGetValue(key, out var forced) && !string.IsNullOrEmpty(forced))
            {
                __result = forced;
                return;
            }

            // 其余情况只在"游戏没查到（原样返回 key）"时才接管
            if (!string.Equals(__result, key, StringComparison.Ordinal)) return;
            if (Map.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
                __result = text;
        }
        catch { /* 本地化失败绝不影响游戏 */ }
    }

    // 液体：只覆盖我们注册过的 key（sfblood / sfblooddsc），其它一律原样
    private static void GetOther_Postfix(object[] __args, ref string __result)
    {
        try
        {
            if (__args == null || __args.Length == 0) return;
            var key = __args[0] as string;
            if (string.IsNullOrEmpty(key)) return;
            if (OtherMap.TryGetValue(key, out var text) && !string.IsNullOrEmpty(text))
                __result = text;
        }
        catch { }
    }
}
