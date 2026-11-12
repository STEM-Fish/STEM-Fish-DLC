using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LaserDrill - 「急速下坠」的无限电量激光钻
//  ---------------------------------------------------------------------------
//  游戏里本来就有 `minilaserdrill`（激光钻）和 `machete`（砍刀），不需要造新物品。
//  电量存在**电池物品**上：
//      BatteryItem.GetCharge()   = myItem.condition * maxCharge
//      BatteryItem.DrainCharge(x)= myItem.condition -= x / (maxCharge * 0.01)
//  所以"无限电量"只要在每次扣电之后把 `myItem.condition` 按回 1。
//
//  ★ 只在「急速下坠」模式里生效，其它模式激光钻照样会耗电。
// ============================================================================

public static class LaserDrill
{
    public const string DrillId = "minilaserdrill";
    public static bool Enabled = true;

    private static FieldInfo _myItem;
    private static bool _patched;
    private static float _t;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Freefall", "InfiniteDrill", true,
                "「急速下坠」里激光钻不耗电（其它模式不受影响）").Value;
        }
        catch { }

        if (!Enabled) return;
        PatchDrain();
        PatchDamage();
    }

    // 模式判断：只有急速下坠才生效
    public static bool Active
    {
        get
        {
            try
            {
                var c = RunConfig.Current;
                return c != null && c.code == "FREEFALL";
            }
            catch { return false; }
        }
    }

    // ==========================================================================
    //  结构伤害：急速下坠里把激光钻（改名「实验性粒子切割机」）的单次结构伤害拉到 100
    //  ---------------------------------------------------------------------------
    //  反汇编：近战/钻头打方块走 `WorldGeneration.DamageBlock(Vector2, float, bool, bool)`。
    //  前缀里改 ref dmg —— 只在这两个条件同时满足时动手：
    //    · 当前模式 = 急速下坠
    //    · 主手物品 = minilaserdrill
    // ==========================================================================
    private static void PatchDamage()
    {
        try
        {
            var m = AccessTools.Method(typeof(WorldGeneration), "DamageBlock",
                new[] { typeof(Vector2), typeof(float), typeof(bool), typeof(bool) });
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 WorldGeneration.DamageBlock(Vector2,...)"); return; }
            var pre = typeof(LaserDrill).GetMethod("DamageBlock_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.drilldmg").Patch(m, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 切割机结构伤害 hook 完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 切割机伤害 hook 失败: {e.Message}");
        }
    }

    private static void DamageBlock_Prefix(ref float dmg)
    {
        try
        {
            if (!Active || dmg >= 100f) return;
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;
            var hand = body.GetItem(0);
            if (hand == null || hand.id != DrillId) return;
            dmg = 100f;
        }
        catch { }
    }

    private static void PatchDrain()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(BatteryItem), "DrainCharge");
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 BatteryItem.DrainCharge，无限电量不生效");
                return;
            }
            _myItem = AccessTools.Field(typeof(BatteryItem), "myItem");
            var post = typeof(LaserDrill).GetMethod("Drain_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.drill").Patch(m, postfix: new HarmonyMethod(post));
            _patched = true;
            Plugin.Log.LogInfo("[STEMFishDLC] 激光钻: 无限电量 hook 完成（仅急速下坠）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 激光钻 hook 失败: {e.Message}");
        }
    }

    // 扣完电立刻回满 —— 等于永不耗电
    private static void Drain_Postfix(BatteryItem __instance)
    {
        try
        {
            if (!Enabled || !Active || __instance == null) return;

            var item = __instance.GetComponentInParent<Item>();
            if (item == null || item.id != DrillId) return;

            // 只对玩家自己生效（其它实体拿着钻不该无限）
            var owner = __instance.GetComponentInParent<Body>();
            var pc = PlayerCamera.main;
            if (pc != null && owner != null && owner != pc.body) return;

            if (_myItem == null) return;
            var bat = _myItem.GetValue(__instance) as Item;
            if (bat != null) bat.condition = 1f;
        }
        catch { }
    }

    // 兜底：钻没插电池的话，给它塞一块（每 1 秒看一次，只在急速下坠里跑）
    public static void Tick()
    {
        if (!Enabled || !Active) return;

        _t += Time.unscaledDeltaTime;
        if (_t < 1f) return;
        _t = 0f;

        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            for (int i = 0; i < Item.allItems.Count; i++)
            {
                var it = Item.allItems[i];
                if (it == null || it.id != DrillId) continue;
                if (it.transform == null || !it.transform.IsChildOf(body.transform)) continue;

                var bi = it.GetComponent<BatteryItem>();
                if (bi == null) continue;

                if (bi.hasBattery)
                {
                    // 已插电池：保证满电
                    if (_myItem != null)
                    {
                        var bat = _myItem.GetValue(bi) as Item;
                        if (bat != null && bat.condition < 1f) bat.condition = 1f;
                    }
                    return;
                }

                // 没电池：现场造一块大的塞进去
                var go = Utils.Create("largebattery", body.transform.position, 0f);
                var batItem = go != null ? go.GetComponent<Item>() : null;
                if (batItem == null) return;
                bi.LoadBattery(batItem);
                Plugin.Log.LogInfo("[STEMFishDLC] 激光钻已装入电池（急速下坠 · 无限电量）");
                return;
            }
        }
        catch { }
    }
}
