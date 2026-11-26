using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LoadGrace - 读档后的无敌窗口
//  ---------------------------------------------------------------------------
//  读档时地形是分块加载的：玩家 body 已经放好，方块/生物却还在往场上填。
//  这段时间里怪物可能在玩家脸上生成，第一口就咬在还没醒过来的身体上。
//
//  做法分两层：
//    ① 挡住伤害入口（真正意义上的"打不动"）
//         Limb.ImpactDamage             —— 摔落 / 大部分冲撞
//         SpiderHandler(.TBE).DamageLimb —— 蜘蛛类撕咬
//    ② 每帧把血量拉回读档时的快照（兜底：走别的字段直接扣血的生物）
//         skinHealth / muscleHealth / pain / bleedAmount / body.bloodVolume
//
//  计时从"玩家 body 出现"开始，用 unscaledTime，所以读档时的暂停/慢动作不影响。
//  只对玩家自己的 body 生效；`[Save] LoadGrace` 可整个关掉。
// ============================================================================
public static class LoadGrace
{
    public static bool Enabled = true;
    public static float Seconds = 10f;

    private static bool _armed;          // 已经点了"读档"，等 body 出现
    private static float _until;         // unscaledTime 截止点；0 = 还没开始
    private static Body _body;           // 已经快照过的身体

    private static float[] _skin, _muscle, _pain, _bleed;
    private static float _blood;
    private static bool _loggedStart;

    /// <summary>窗口是否生效（伤害前缀直接读这个）</summary>
    public static bool Active
    {
        get
        {
            try { return _armed && _until > 0f && Time.unscaledTime < _until; }
            catch { return false; }
        }
    }

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Save", "LoadGrace", true,
                "读档后的无敌时间（避免地图还没加载完就被怪物咬死）").Value;
            Seconds = Mathf.Clamp(plugin.Config.Bind("Save", "LoadGraceSeconds", 10f,
                "无敌时长（秒），从玩家身体出现开始算；0 = 关闭").Value, 0f, 60f);
        }
        catch { }

        try
        {
            var harmony = new Harmony("com.stemfish.dlc.loadgrace");
            int n = 0;

            var loadRun = AccessTools.Method(typeof(PreRunScript), "LoadRun");
            if (loadRun != null)
            {
                harmony.Patch(loadRun, prefix: new HarmonyMethod(
                    typeof(LoadGrace).GetMethod("LoadRun_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                n++;
            }

            var impact = AccessTools.Method(typeof(Limb), "ImpactDamage");
            if (impact != null)
            {
                harmony.Patch(impact, prefix: new HarmonyMethod(
                    typeof(LoadGrace).GetMethod("Damage_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                n++;
            }

            foreach (var tn in new[] { "SpiderHandler", "SpiderHandlerTBE" })
            {
                var t = AccessTools.TypeByName(tn);
                var m = t != null ? AccessTools.Method(t, "DamageLimb", new[] { typeof(Limb) }) : null;
                if (m == null) continue;
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(LoadGrace).GetMethod("DamageLimb_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
                n++;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 读档无敌 hook 完成 | {n} 处 | {(Enabled ? $"{Seconds:0.#}s" : "关")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读档无敌 hook 失败: {e.Message}");
        }
    }

    // 读档开始：清掉上一局的快照，等 body 出现再计时
    private static void LoadRun_Prefix()
    {
        _armed = true;
        _body = null;
        _until = 0f;
        _loggedStart = false;
        _skin = _muscle = _pain = _bleed = null;
        Plugin.Log.LogInfo("[STEMFishDLC] 读档：无敌窗口已准备（等身体就位）");
    }

    private static bool Damage_Prefix() => !Active;
    private static bool DamageLimb_Prefix() => !Active;

    public static void Tick()
    {
        if (!Enabled || Seconds <= 0.01f || !_armed) return;

        try
        {
            var pc = PlayerCamera.main;
            var b = pc != null ? pc.body : null;
            if (b == null) return;                 // 世界还没建好，继续等

            if (!ReferenceEquals(_body, b))
            {
                _body = b;
                Snapshot(b);
                _until = Time.unscaledTime + Seconds;
                if (!_loggedStart)
                {
                    _loggedStart = true;
                    Plugin.Log.LogInfo($"[STEMFishDLC] 无敌窗口开始：{Seconds:0.#}s（身体已就位，伤害入口已挡住）");
                }
                return;
            }

            if (Time.unscaledTime >= _until)
            {
                _armed = false;
                _until = 0f;
                Plugin.Log.LogInfo("[STEMFishDLC] 无敌窗口结束");
                return;
            }

            Restore(b);
        }
        catch { }
    }

    private static void Snapshot(Body b)
    {
        try
        {
            var limbs = b.limbs;
            int n = limbs != null ? limbs.Length : 0;
            _skin = new float[n];
            _muscle = new float[n];
            _pain = new float[n];
            _bleed = new float[n];

            for (int i = 0; i < n; i++)
            {
                var l = limbs[i];
                if (l == null) continue;
                _skin[i] = l.skinHealth;
                _muscle[i] = l.muscleHealth;
                _pain[i] = l.pain;
                _bleed[i] = l.bleedAmount;
            }
            _blood = b.bloodVolume;
        }
        catch { }
    }

    private static void Restore(Body b)
    {
        try
        {
            var limbs = b.limbs;
            int n = limbs != null ? limbs.Length : 0;
            if (_skin == null || _skin.Length != n) { Snapshot(b); return; }

            for (int i = 0; i < n; i++)
            {
                var l = limbs[i];
                if (l == null) continue;

                if (l.skinHealth < _skin[i]) l.skinHealth = _skin[i];
                if (l.muscleHealth < _muscle[i]) l.muscleHealth = _muscle[i];
                if (l.pain > _pain[i]) l.pain = _pain[i];
                if (l.bleedAmount > _bleed[i]) l.bleedAmount = _bleed[i];
            }

            if (b.bloodVolume < _blood) b.bloodVolume = _blood;
        }
        catch { }
    }
}
