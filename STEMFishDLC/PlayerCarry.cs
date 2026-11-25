using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  PlayerCarry - 主角的负重上限倍率（同等力量下比 EXPie 更能背）
//  ---------------------------------------------------------------------------
//  反汇编确认原版公式（`Body.HandlePeriodicChecks`，每 0.5 秒算一次）：
//
//    maxEncumberance = (
//          11
//        + Clamp(weightOffset + 15, -60, 0) * 0.1      // 内脏/体重偏移
//        - sicknessAmount * 0.025                     // 生病扣
//        + (hunger  > 100 ? 1.5 : 0)                  // 吃撑了 +1.5
//        - (hunger  < 40  ? 1.5 : 0)                  // 饿着 -1.5
//        - (thirst  < 40  ? 1.0 : 0)                  // 渴着 -1.0
//        + (STR-10)*0.5 + Min((STR-10)*0.5, (RES-10)*0.5)
//      ) * runSettings["encumbrancecap"]
//
//    overEncumberance = Clamp01(totalEncumberance / maxEncumberance - 1)   // 超重比例
//
//  代入原版新档的 EXPie（STR 9 / RES 8）：
//      11 - 0.5 + Min(-0.5, -1.0) = 9.5 → ×encumbrancecap
//  代入我们下调后的 STEM-Fish（STR 12 / RES 12）：
//      11 + 1 + 1 = 13.0 → 比 EXPie 高 37%（力量已经比它高，还没算倍率）
//
//  这里再加一层**只作用于玩家**的倍率：同样是 STR 12，我们的背上限是 EXPie 的 1.4 倍。
//  实现方式是 `Body.HandlePeriodicChecks` 的 prefix + postfix 组合：
//    prefix 记下调用前的 maxEncumberance；postfix 只有在"这次真的重算过"（值变了）
//    才乘倍率，然后照原公式把 overEncumberance 一起重算。
//    ⚠ 不能只在 postfix 里无条件乘：原版那段是 `if (halfSecondCheckTime > 0.5)`
//      才执行的，每帧都乘会让上限指数增长（10 秒后能背一座山）。
//
//  ⚠ 只认 `PlayerCamera.main.body`：这个方法所有 Body（敌人 / 动物）共用。
// ============================================================================

public static class PlayerCarry
{
    public static bool Enabled = true;
    public static float Mult = 1.4f;          // 1.0 = 原版

    private static bool _patched;
    private static bool _logged;
    private static int _loggedFor;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Skills", "CarryBoost", true,
                "只给主角的负重上限加成（同等力量下比原版 EXPie 更能背）").Value;

            float v = plugin.Config.Bind("Skills", "CarryMult", 1.4f,
                "负重上限倍率：1.0 = 和原版同等力量完全一样；1.4 = 多背 40%").Value;
            Mult = Mathf.Clamp(v, 1.0f, 3.0f);

            if (Mathf.Abs(Mult - v) > 0.001f)
                Plugin.Log.LogWarning($"[STEMFishDLC] CarryMult={v} 超出 1.0~3.0，已夹到 {Mult}");

            if (Enabled && Mult > 1.001f) Patch();
            Plugin.Log.LogInfo($"[STEMFishDLC] 主角负重倍率: {(Enabled ? $"×{Mult:0.00}" : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 负重倍率配置读取失败: {e.Message}");
        }
    }

    private static void Patch()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(Body), "HandlePeriodicChecks", Type.EmptyTypes);
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Body.HandlePeriodicChecks，负重倍率不生效");
                return;
            }
            var pre = typeof(PlayerCarry).GetMethod("PeriodicChecks_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            var post = typeof(PlayerCarry).GetMethod("PeriodicChecks_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.carry").Patch(
                m,
                prefix: new HarmonyMethod(pre),
                postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(m);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 负重倍率 hook 完成 | prefix={info?.Prefixes?.Count ?? -1} postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 负重倍率 hook 失败: {e.Message}");
        }
    }

    // 记下"原版这次还没算之前"的上限值（上一次算出来的是含倍率的，所以能当参照）
    private static void PeriodicChecks_Prefix(Body __instance)
    {
        _preMax = float.NaN;
        if (!Enabled || Mult <= 1.001f || __instance == null) return;
        try { _preMax = __instance.maxEncumberance; } catch { }
    }

    // 原版刚算完 maxEncumberance / totalEncumberance / overEncumberance
    private static void PeriodicChecks_Postfix(Body __instance)
    {
        if (!Enabled || Mult <= 1.001f || __instance == null) return;
        if (float.IsNaN(_preMax)) return;

        try
        {
            Body player = null;
            try { player = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
            if (player == null || !ReferenceEquals(player, __instance)) return;

            // v0.23：负重加成是 SF-07 的体质特性，原版角色保持原版上限
            if (!CharacterSelect.IsSf07(__instance)) return;

            float baseMax = __instance.maxEncumberance;

            // 这次没重算（那段 `if (halfSecondCheckTime > 0.5)` 没进）→ 什么都不做，
            // 否则每帧乘一次，上限会指数爆炸
            if (Mathf.Abs(baseMax - _preMax) < 0.0001f) return;

            float max = baseMax * Mult;
            if (max < 0.0001f) max = 0.0001f;
            __instance.maxEncumberance = max;

            float total = __instance.totalEncumberance;
            __instance.overEncumberance = Mathf.Clamp01(total / max - 1f);

            // 每次换局/读档报一行，方便对数值（开局负不负重看这一行就够）
            int id = __instance.GetInstanceID();
            if (!_logged || _loggedFor != id)
            {
                _logged = true;
                _loggedFor = id;
                float pct = max > 0.0001f ? total / max : 1f;
                Plugin.Log.LogInfo($"[STEMFishDLC] 负重：{total:0.0} / {max:0.0}（{pct * 100f:0}%）" +
                                   $"{(pct > 1f ? " ⚠ 超重" : "")} · 上限已含 ×{Mult:0.00}");
            }
        }
        catch (Exception e)
        {
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                Plugin.Log.LogWarning($"[STEMFishDLC] 负重倍率应用失败: {e.Message}");
            }
        }
    }
    private static string _lastError;
    private static float _preMax = float.NaN;
}
