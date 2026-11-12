using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  CombatFeel - 打生物的"手感"：黄血 + 小型生物击退
//  ---------------------------------------------------------------------------
//  ★ v0.18.10 重做：以前挂在 `SpiderHandler.OnCollisionEnter2D` 上，用
//    "撞到带 Item 的东西 + 相对速度够快" 来猜"这是子弹"，结果生物互相撞、
//    撞墙、被掉落物砸到都会喷血 —— 作者反馈"没中枪也冒血花"。那个判据本质上
//    是猜的，猜不准。
//
//    现在改成挂在**伤害入口** `SpiderHandler.AnimalHit(float damage)` 上：
//    子弹、近战（Body.Attack）、炮塔（TurretScript）、无人机（LaserDroneScript）
//    最后都是 `SendMessage("AnimalHit", 伤害)` → 只要这个方法被调用，就是**真的挨打了**，
//    不存在误触发。方向则用"玩家 → 生物"的水平向量，视觉上就是被顶飞。
//
//  黄血：实验体（以及这批生物）的血是黄的 —— 用原版 `BloodExplosion` 粒子，
//       把 main.startColor 染黄再生成。
//  击退：只有"小型"生物（id 含 elder 的棘背长老、trader 都不算）。
// ============================================================================
public static class CombatFeel
{
    public static bool Enabled = true;
    public static bool YellowBlood = true;
    public static bool Knockback = true;
    public static float KnockbackPower = 14f;
    public static float MinDamage = 0.5f;

    private static bool _patched;
    private static GameObject _bloodPrefab;
    private static bool _bloodMissing;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Combat", "Enabled", true,
                "战斗手感：黄血特效 + 小型生物击退").Value;
            YellowBlood = plugin.Config.Bind("Combat", "YellowBlood", true,
                "生物受伤时喷黄血（实验体的血是黄的）").Value;
            Knockback = plugin.Config.Bind("Combat", "Knockback", true,
                "小型生物挨打时被顶飞（棘背长老/商人除外）").Value;
            KnockbackPower = Mathf.Clamp(plugin.Config.Bind("Combat", "KnockbackPower", 14f,
                "击退力度（直接给刚体的速度，越大飞得越远）").Value, 0f, 60f);
            MinDamage = Mathf.Clamp(plugin.Config.Bind("Combat", "MinDamage", 0.5f,
                "低于这个伤害不演特效（防止蹭一下就喷血）").Value, 0f, 50f);
        }
        catch { }

        if (!Enabled) return;

        try
        {
            var m = AccessTools.Method(typeof(SpiderHandler), "AnimalHit", new[] { typeof(float) });
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 SpiderHandler.AnimalHit(float)"); return; }
            var post = typeof(CombatFeel).GetMethod("AnimalHit_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.combatfeel").Patch(m, postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(m);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 战斗手感 hook 完成 | AnimalHit postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 战斗手感 hook 失败: {e.Message}");
        }
    }

    // 真正的伤害入口：只有挨打才会走到这里
    private static void AnimalHit_Postfix(SpiderHandler __instance, float damage)
    {
        if (!_patched || __instance == null) return;
        if (damage < MinDamage) return;

        Vector2 point;
        Vector2 dir = Vector2.right;
        try
        {
            point = __instance.transform.position;

            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body != null)
            {
                var d = point - (Vector2)body.transform.position;
                if (d.sqrMagnitude > 0.01f) dir = d.normalized;
                // 太远（>50）的战斗不演特效，和原版一致
                if (d.sqrMagnitude > 2500f) return;
            }
        }
        catch
        {
            point = Vector2.zero;
        }

        if (YellowBlood) SpawnYellowBlood(point);

        if (!Knockback) return;

        try
        {
            var bld = __instance.GetComponent<BuildingEntity>();
            if (bld == null || !IsSmall(bld.id)) return;

            var rb = bld.GetComponent<Rigidbody2D>();
            if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic) return;

            // 直接覆盖速度（叠加会被它自己的移动逻辑吃掉），并给一段硬直，
            // 否则下一帧 SpiderHandler 又按自己的意图把速度改回去
            rb.velocity = dir * KnockbackPower;
            try { __instance.stunTime = Mathf.Max(__instance.stunTime, 0.4f); } catch { }
        }
        catch { }
    }

    // 小型：蛛类里除棘背长老；trader 不是 SpiderHandler，天然不会走到这里
    private static bool IsSmall(string id)
    {
        if (string.IsNullOrEmpty(id)) return true;
        var s = id.ToLowerInvariant();
        if (s.Contains("elder")) return false;
        if (s.Contains("trader")) return false;
        return true;
    }

    private static void SpawnYellowBlood(Vector2 pos)
    {
        try
        {
            if (_bloodMissing) return;
            if (_bloodPrefab == null)
            {
                _bloodPrefab = Resources.Load<GameObject>("BloodExplosion");
                if (_bloodPrefab == null) { _bloodMissing = true; return; }
            }

            var go = UnityEngine.Object.Instantiate(_bloodPrefab, pos, Quaternion.identity);
            if (go == null) return;

            var ps = go.GetComponent<ParticleSystem>();
            if (ps != null)
            {
                var main = ps.main;
                var yellow = new Color(0.95f, 0.82f, 0.22f, 1f);
                var deep = new Color(0.72f, 0.55f, 0.08f, 1f);
                main.startColor = new ParticleSystem.MinMaxGradient(yellow, deep);
                ps.Play();
            }
            UnityEngine.Object.Destroy(go, 3f);
        }
        catch { }
    }
}
