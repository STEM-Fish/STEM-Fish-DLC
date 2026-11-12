using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  FreefallTweaks - 「急速下坠」的装备宽容设置
//  ---------------------------------------------------------------------------
//  ① 弹挂甲 / 头盔不掉耐久
//     `Limb.DamageWearables(float)` 是原版唯一扣装备耐久的地方。
//     prefix 里：只要这个肢体穿的是我们的 platecarrier / tachelmet，就整段跳过。
//  ② 枪不故障
//     `GunScript.JamChance()` 返回卡壳概率，postfix 直接压成 0。
//  ★ 只有「急速下坠」生效，其它模式照常。
// ============================================================================

public static class FreefallTweaks
{
    public static bool Enabled = true;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Freefall", "IndestructibleGear", true,
                "「急速下坠」里弹挂甲/头盔不掉耐久，枪不会卡壳").Value;
        }
        catch { }

        if (!Enabled) return;

        try
        {
            var dw = AccessTools.Method(typeof(Limb), "DamageWearables");
            if (dw != null)
            {
                var pre = typeof(FreefallTweaks).GetMethod("Wearables_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                new Harmony("com.stemfish.dlc.freefallwear").Patch(dw, prefix: new HarmonyMethod(pre));
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Limb.DamageWearables");

            var jam = AccessTools.Method(typeof(GunScript), "JamChance");
            if (jam != null)
            {
                var post = typeof(FreefallTweaks).GetMethod("Jam_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
                new Harmony("com.stemfish.dlc.freefalljam").Patch(jam, postfix: new HarmonyMethod(post));
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 GunScript.JamChance");

            Plugin.Log.LogInfo("[STEMFishDLC] 急速下坠：装备免耐久 + 枪不卡壳");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 急速下坠装备 hook 失败: {e.Message}");
        }
    }

    private static bool Active
    {
        get { try { var c = RunConfig.Current; return c != null && c.code == "FREEFALL"; } catch { return false; } }
    }

    private static bool Wearables_Prefix(Limb __instance)
    {
        try
        {
            if (!Enabled || !Active || __instance == null) return true;

            var pc = PlayerCamera.main;
            if (pc == null || pc.body == null) return true;
            if (__instance.body != pc.body) return true;      // 只护着玩家

            var list = __instance.GetLimbWearables();
            if (list == null) return true;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null) continue;
                if (it.id == "platecarrier" || it.id == "tachelmet") return false;
            }
        }
        catch { }
        return true;
    }

    private static void Jam_Postfix(ref float __result)
    {
        if (Enabled && Active) __result = 0f;
    }
}
