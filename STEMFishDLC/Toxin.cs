using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  Toxin - SF-07 的可可碱 / 咖啡因不耐受
//  ---------------------------------------------------------------------------
//  原版就有这条设定（char0quirks：「犬科烙印 —— 你的胃难以容忍可可碱」），
//  但代码里**没有按种族区分**：Liquids..cctor 给每种饮料绑的 onDrink 是固定数值，
//  谁喝都一样。反汇编出来的每升疾病增量：
//      coffee         150/L
//      chocolatemilk  120/L
//      energydrink    200/L
//      icedtea          0（无咖啡因）
//
//  做法：启动后把 Registry 里这几种液体的 onDrink 包一层（原委托照常执行，
//  只对带 SF-07 Tag 的身体追加疾病）。不 patch 任何游戏方法 —— 原版更新后
//  就算 lambda 名字变了也不受影响，最多是包不上。
//
//  固体巧克力（chocolatebar）走 Body.Eat：prefix 里看一眼手里正在吃的东西。
// ============================================================================

public static class Toxin
{
    public static bool Enabled = true;

    // 总毒性倍率（相对原版）。3 = 原版 3 倍，喝一杯 250ml 咖啡直接重病。
    public static float CocoaMultiplier = 3f;

    // 原版每升的疾病增量（反汇编 Liquids..cctor）
    private static readonly Dictionary<string, float> VanillaPerLiter = new Dictionary<string, float>
    {
        { "coffee", 150f },
        { "chocolatemilk", 120f },
        { "energydrink", 200f },
    };

    private const float ChocolateBarSickness = 90f;   // 一根巧克力棒在原版没有毒性，这是我们加的基准

    private static readonly HashSet<string> _wrapped = new HashSet<string>();
    private static float _poll;
    private static bool _warned;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Mechanics", "CocoaToxin", true,
                "SF-07 对可可碱/咖啡因更敏感（咖啡、巧克力奶、能量饮料、巧克力棒）").Value;
            CocoaMultiplier = Mathf.Clamp(plugin.Config.Bind("Mechanics", "CocoaMultiplier", 3f,
                "SF-07 的可可碱毒性倍率（原版 = 1；3 = 三倍疾病）").Value, 1f, 10f);
        }
        catch { }

        try
        {
            var eat = AccessTools.Method(typeof(Body), "Eat", new[] { typeof(float), typeof(float), typeof(int) });
            if (eat != null)
                new Harmony("com.stemfish.dlc.toxin").Patch(eat, prefix: new HarmonyMethod(
                    typeof(Toxin).GetMethod("Eat_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 巧克力棒毒性 hook 失败: {e.Message}");
        }

        Plugin.Log.LogInfo($"[STEMFishDLC] 可可碱不耐受: {(Enabled ? $"×{CocoaMultiplier:0.#}" : "关闭")}");
    }

    // Liquids.Registry 可能在 Init 之后才由 .cctor 建好 → 轮询到包上为止
    public static void Tick()
    {
        if (!Enabled || _wrapped.Count >= VanillaPerLiter.Count) return;
        _poll -= Time.unscaledDeltaTime;
        if (_poll > 0f) return;
        _poll = 1f;
        TryWrap();
    }

    private static void TryWrap()
    {
        try
        {
            var reg = Liquids.Registry;
            if (reg == null || reg.Count == 0) return;

            int n = 0;
            foreach (var kv in VanillaPerLiter)
            {
                if (_wrapped.Contains(kv.Key)) continue;
                if (!reg.TryGetValue(kv.Key, out var lt) || lt == null) continue;
                if (lt.onDrink == null) continue;

                var orig = lt.onDrink;
                string id = kv.Key;
                float extraPerLiter = kv.Value * (CocoaMultiplier - 1f);

                lt.onDrink = new LiquidType.OnDrink((ml, body) =>
                {
                    try { orig(ml, body); } catch { }
                    if (!Enabled || extraPerLiter <= 0f) return;
                    if (!CharacterSelect.IsSf07(body)) return;
                    try
                    {
                        // ml 是毫升；原版 onDrink 里都是 ml * 0.001 = 升
                        body.sicknessAmount += extraPerLiter * (ml * 0.001f);
                    }
                    catch { }
                });

                _wrapped.Add(id);
                n++;
            }

            if (n > 0)
                Plugin.Log.LogInfo($"[STEMFishDLC] 可可碱不耐受已接入 {n} 种饮料（×{CocoaMultiplier:0.#}）");
        }
        catch (Exception e)
        {
            if (!_warned)
            {
                _warned = true;
                Plugin.Log.LogWarning($"[STEMFishDLC] 饮料毒性包装失败: {e.Message}");
            }
        }
    }

    // 巧克力棒：Body.Eat 之前手里（槽位 2）正在吃的那个
    private static void Eat_Prefix(Body __instance)
    {
        if (!Enabled || __instance == null) return;
        if (!CharacterSelect.IsSf07(__instance)) return;

        try
        {
            var item = __instance.GetItem(2);
            if (item == null || item.id != "chocolatebar") return;

            float extra = ChocolateBarSickness * (CocoaMultiplier - 1f);
            if (extra <= 0f) return;
            __instance.sicknessAmount += extra;
            Plugin.Log.LogInfo($"[STEMFishDLC] SF-07 吃了巧克力棒：疾病 +{extra:0}");
        }
        catch { }
    }
}
