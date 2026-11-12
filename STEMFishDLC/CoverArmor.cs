using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  CoverArmor - 让"贴皮衣物"的防护覆盖**多个肢体**
//  ---------------------------------------------------------------------------
//  原版一件 Wearable 只挂在**一个**肢体上（Wearable.desiredWearLimb），
//  `Limb.GetArmorReduction()` 也只累加挂在自己身上的那几件 —— 所以把冲锋衣穿在
//  UpTorso 上，手臂和 downTorso 一样是 0% 防护（作者截图里就是这个现象）。
//
//  这三件衣服本来就是"画在皮肤上"的，不该受肢体挂点限制。
//  做法：postfix `Limb.GetArmorReduction()`，按**肢体类型**补护甲：
//      冲锋衣：上半身除手（躯干 + 上臂 + 前臂，不含头、不含手）
//      长裤  ：两条腿的大腿 + 小腿（不含脚）
//      胶靴  ：两只脚
//  物品本身的 wearableArmor 已经设成 0，避免和这里重复累加。
// ============================================================================
public static class CoverArmor
{
    public static bool Enabled = true;

    // v0.18.9：作者反馈"太超模" → 大幅下调（原来是 0.8 / 0.7 / 1.0）
    //   GetArmorReduction 是**除数**：伤害 ÷ (1 + 护甲值)
    //     0.80 → 受伤 56%   |   0.25 → 受伤 80%
    //     0.70 → 受伤 59%   |   0.20 → 受伤 83%
    //     1.00 → 受伤 50%   |   0.30 → 受伤 77%
    public static float CoatArmor = 0.25f;
    public static float PantsArmor = 0.20f;
    public static float BootsArmor = 0.30f;

    private static bool _patched;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Combat", "CoverArmor", true,
                "让开局三件贴皮衣物按部位防护（冲锋衣=上半身除手 / 长裤=大腿小腿 / 靴子=脚）").Value;
            // 数值语义：伤害 ÷ (1 + 值)。0.25 ≈ 受伤 80%，0.5 ≈ 67%，1.0 ≈ 50%
            CoatArmor = Mathf.Clamp(plugin.Config.Bind("Combat", "CoatArmor", 0.25f,
                "冲锋衣护甲值（伤害÷(1+值)：0.25≈挨 80%，1.0≈挨 50%）").Value, 0f, 2f);
            PantsArmor = Mathf.Clamp(plugin.Config.Bind("Combat", "PantsArmor", 0.20f,
                "长裤护甲值（伤害÷(1+值)：0.20≈挨 83%）").Value, 0f, 2f);
            BootsArmor = Mathf.Clamp(plugin.Config.Bind("Combat", "BootsArmor", 0.30f,
                "胶靴护甲值（伤害÷(1+值)：0.30≈挨 77%）").Value, 0f, 2f);
        }
        catch { }

        if (!Enabled) return;
        try
        {
            var m = AccessTools.Method(typeof(Limb), "GetArmorReduction");
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Limb.GetArmorReduction"); return; }
            var post = typeof(CoverArmor).GetMethod("Armor_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.coverarmor").Patch(m, postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(m);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 衣物分部位护甲 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 衣物护甲 hook 失败: {e.Message}");
        }
    }

    private static void Armor_Postfix(Limb __instance, ref float __result)
    {
        if (!_patched || __instance == null) return;
        try
        {
            var body = __instance.body;
            if (body == null) return;

            string n = NameOf(__instance);
            bool hand = n.Contains("hand");
            bool foot = n.Contains("foot") || n.Contains("feet");

            float extra = 0f;

            // 冲锋衣：不盖头和手
            if (!__instance.isHead && !hand && body.HasWearable("fishcoat"))
                extra += CoatArmor;

            // 长裤：两条腿的大腿/小腿（不含脚）
            if (__instance.isLegLimb && !foot && body.HasWearable("fishpants"))
                extra += PantsArmor;

            // 靴子：两只脚
            if (__instance.isLegLimb && foot && body.HasWearable("fishboots"))
                extra += BootsArmor;

            if (extra > 0f) __result += extra;
        }
        catch { }
    }

    private static string NameOf(Limb l)
    {
        string n = null;
        try { n = l.name; } catch { }
        if (string.IsNullOrEmpty(n)) { try { n = l.fullName; } catch { } }
        return (n ?? "").ToLowerInvariant();
    }
}
