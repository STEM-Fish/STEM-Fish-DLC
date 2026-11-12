using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  HypeMode - 「急速下坠」的娱乐性加成
//  ---------------------------------------------------------------------------
//  反汇编结论：世界里的敌对生物（shadecrawler / wallbiter / cavetick）不是
//  `Body`，而是 `BuildingEntity`（那个类同时管箱子、动物、可破坏物）。
//  `Body` 上根本没有 health 字段，`Body.health` 的写入点全程序集为 0。
//
//  所以"击杀"的判定点 = `BuildingEntity.Update` 里 `health < 0.5` 的碎裂分支。
//  用 prefix 在它执行前抓一次，按实例 id 去重（对象销毁前会连着跑好几帧）。
//
//  只对 id 命中的敌对生物算，箱子/植物不会误判。
// ============================================================================

public static class HypeMode
{
    public static bool Active;
    public static float KillHappiness = 1f;     // 每次击杀回多少心情
    public static float HitFxScale = 1.7f;      // 命中特效倍率（GunFx 读）
    public static bool Enabled = true;

    private static readonly HashSet<int> _credited = new HashSet<int>();
    // v0.18.2：把尖刺爬行者（thornback*）也算进敌对生物，同时提高默认心情奖励
    private static readonly string[] HostileIds = { "shadecrawler", "wallbiter", "cavetick", "crystal", "thornback" };

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Hype", "Enabled", true,
                "急速下坠的娱乐加成（击杀回心情 + 夸张命中反馈）").Value;
            var killEntry = plugin.Config.Bind("Hype", "KillHappiness", 12f,
                "每击杀一只敌对生物回复多少心情（v0.18.10 起默认 12，无上限）");
            KillHappiness = Mathf.Clamp(killEntry.Value, 0f, 200f);

            // v0.18.2：默认 1 → 4。老 cfg 里存着 1，改默认值不会生效，做一次性迁移
            //（带标记，作者以后自己改回 1 不会被覆盖）。
            try
            {
                var marker = plugin.Config.Bind("Hype", "HappinessMigrated", false,
                    "内部标记：击杀心情奖励是否已从 1 升到 4，别手动改");
                if (!marker.Value && Mathf.Abs(KillHappiness - 1f) < 0.001f)
                {
                    killEntry.Value = 4f;
                    KillHappiness = 4f;
                    Plugin.Log.LogInfo("[STEMFishDLC] 急速下坠击杀心情：1 → 4（一次性迁移）");
                }
                marker.Value = true;
            }
            catch { }

            // v0.18.10：默认 4 → 12（作者反馈"局内不明显"）。同样带标记迁移。
            try
            {
                var m2 = plugin.Config.Bind("Hype", "HappinessMigrated2", false,
                    "内部标记：击杀心情奖励是否已从 4 升到 12，别手动改");
                if (!m2.Value && Mathf.Abs(KillHappiness - 4f) < 0.001f)
                {
                    killEntry.Value = 12f;
                    KillHappiness = 12f;
                    Plugin.Log.LogInfo("[STEMFishDLC] 急速下坠击杀心情：4 → 12（一次性迁移）");
                }
                m2.Value = true;
            }
            catch { }
            HitFxScale = Mathf.Clamp(plugin.Config.Bind("Hype", "HitFxScale", 1.7f,
                "急速下坠模式下命中特效的放大倍率（1 = 和其他模式一样）").Value, 1f, 3f);

            var m = AccessTools.Method(typeof(BuildingEntity), "Update");
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 BuildingEntity.Update，击杀回心情失效");
                return;
            }
            var pre = typeof(HypeMode).GetMethod("Building_Update_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.hype").Patch(m, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(m);

            // ★ 正式版的死亡销毁其实在 `BuildingEntity.CheckDamage()` 里（health<0.5 →
            //   didDestroyRoutine → Destroy），Update 那条分支基本收不到 → 这就是
            //   "击杀回心情在局内感觉不到"的原因。主判据改成 CheckDamage 的 prefix。
            var cd = AccessTools.Method(typeof(BuildingEntity), "CheckDamage");
            if (cd != null)
            {
                var pre2 = typeof(HypeMode).GetMethod("CheckDamage_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                new Harmony("com.stemfish.dlc.hype").Patch(cd, prefix: new HarmonyMethod(pre2));
                info = Harmony.GetPatchInfo(cd);
            }
            Plugin.Log.LogInfo($"[STEMFishDLC] 击杀回心情 hook 完成 | Update prefix=" +
                               $"{Harmony.GetPatchInfo(m)?.Prefixes?.Count ?? -1} CheckDamage prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 击杀回心情 hook 失败: {e.Message}");
        }
    }

    public static void SetActive(bool on)
    {
        Active = on && Enabled;
        if (!Active) _credited.Clear();
    }

    public static void Tick()
    {
        // 实例 id 会复用；攒太多就清一次，宁可漏记一次也不吃内存
        if (_credited.Count > 512) _credited.Clear();
    }

    private static void Building_Update_Prefix(BuildingEntity __instance)
    {
        if (!Active || __instance == null) return;
        if (__instance.health >= 0.5f) return;             // 还没碎
        if (!IsHostile(__instance.id)) return;

        int key;
        try { key = __instance.GetInstanceID(); } catch { return; }
        if (!_credited.Add(key)) return;                    // 这一只已经算过

        Body body = null;
        try
        {
            var pc = PlayerCamera.main;
            body = pc != null ? pc.body : null;
        }
        catch { }
        if (body == null) return;

        Credit(body, __instance.id);
    }

    // 正式版真正销毁生物的地方
    private static void CheckDamage_Prefix(BuildingEntity __instance)
    {
        if (!Active || __instance == null) return;
        try
        {
            if (__instance.health >= 0.5f) return;                 // 还没死
            if (!IsHostile(__instance.id)) return;
        }
        catch { return; }

        int key;
        try { key = __instance.GetInstanceID(); } catch { return; }
        if (!_credited.Add(key)) return;                            // 已经算过

        Body body = null;
        try
        {
            var pc = PlayerCamera.main;
            body = pc != null ? pc.body : null;
        }
        catch { }
        if (body == null) return;

        Credit(body, __instance.id);
    }

    private static void Credit(Body body, string id)
    {
        try
        {
            float before = body.happiness;
            body.happiness = Mathf.Min(body.happiness + KillHappiness, 999f);   // 不设上限（只防溢出）
            body.traumaAmount = Mathf.Max(0f, body.traumaAmount - 2f);          // 顺手抚平创伤
            float total = body.totalHappiness;
            Plugin.Log.LogInfo($"[STEMFishDLC] 击杀 {id} → 心情 {before:0} → {body.happiness:0}" +
                               $"（+{KillHappiness:0.#}，面板显示 {total:0}）");
        }
        catch { }
    }

    private static bool IsHostile(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var l = id.ToLowerInvariant();
        for (int i = 0; i < HostileIds.Length; i++)
            if (l.Contains(HostileIds[i])) return true;
        return false;
    }
}
