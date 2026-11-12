using System;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  PlayerSkills - 开局技能（只影响新开一局，读档不动）
//  ---------------------------------------------------------------------------
//  反汇编确认的技能系统：
//    Body.skills : Skills       ← Body.Awake 里 new + Setup(charType)
//    Skills.STR / RES / INT     ← public int，力量 / 耐力 / 智力
//    Skills.minXXX / maxXXX     ← 当前等级的经验下界/上界（UpdateExpBoundaries 重算）
//    Skills.expXXX              ← 当前经验
//
//  游戏里技能的作用（同一次反汇编）：
//    Recipe.visible  = !(INT - Recipe.INT < -3)    → 智力不够**配方直接看不见**
//    Recognition.recognizable = rec.min <= INT     → 智力不够**物品显示成"未知物体"**
//    Body 的负重 / 击退 / 跳跃都读 STR、RES
//  所以"开局限重、连弹匣都看不懂"就是 INT 太低，这条线拉高最直接。
//
//  为什么不在 Skills.Setup 上打 patch：那是**所有** Body（含动物/敌人）共用的入口，
//  分不清谁是玩家。改成等 PlayerCamera.body 就绪后自己改一次，只认玩家。
// ============================================================================

public static class PlayerSkills
{
    public static bool Enabled = true;
    // 原版新档实测：力量 9 / 耐力 8 / 智力 7（不是 3~4，早期日志看错了）
    // 军事线选育支系 + 战术训练 → 三项都明显高于野生同类，智力给得最多
    // v0.13.6 再抬一档：作者反馈开局仍容易严重负重，负重主要吃 STR
    public static int STR = 18;
    public static int RES = 16;
    public static int INT = 18;

    private static bool _want;
    private static bool _done;

    // 旧版本留在 cfg 里的 14/13/15 会被 Bind 原样读回来，光改默认值没用。
    // 这里做一次性迁移：只往上抬，不往下压（玩家自己设得更高就保留）。
    private const int NewSTR = 18, NewRES = 16, NewINT = 18;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Skills", "Enabled", true,
                "新开一局时把玩家初始技能拉到下面的数值（读档不改）").Value;

            var eStr = plugin.Config.Bind("Skills", "STR", NewSTR,
                "初始力量（负重/击退/近战）。原版新档实测 9");
            var eRes = plugin.Config.Bind("Skills", "RES", NewRES,
                "初始耐力（体力/奔跑）。原版新档实测 8");
            var eInt = plugin.Config.Bind("Skills", "INT", NewINT,
                "初始智力（配方可见性/物品识别）。原版新档实测 7");

            bool migrated = plugin.Config.Bind("Skills", "MigratedTo18", false,
                "（内部）技能默认值迁移标记，不要手改").Value;
            if (!migrated)
            {
                int o1 = eStr.Value, o2 = eRes.Value, o3 = eInt.Value;
                if (eStr.Value < NewSTR) eStr.Value = NewSTR;
                if (eRes.Value < NewRES) eRes.Value = NewRES;
                if (eInt.Value < NewINT) eInt.Value = NewINT;
                plugin.Config.Bind("Skills", "MigratedTo18", false,
                    "（内部）技能默认值迁移标记，不要手改").Value = true;
                try { plugin.Config.Save(); } catch { }
                Plugin.Log.LogInfo($"[STEMFishDLC] 初始技能默认值已迁移: " +
                                   $"{o1}/{o2}/{o3} → {eStr.Value}/{eRes.Value}/{eInt.Value}");
            }

            STR = Mathf.Clamp(eStr.Value, 1, 40);
            RES = Mathf.Clamp(eRes.Value, 1, 40);
            INT = Mathf.Clamp(eInt.Value, 1, 40);

            Plugin.Log.LogInfo($"[STEMFishDLC] 初始技能: {(Enabled ? $"STR={STR} RES={RES} INT={INT}" : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 初始技能配置读取失败: {e.Message}");
        }
    }

    // StarterKit 在 StartRun / LoadRun / StartTutorial 的 prefix 里转发过来
    public static void OnRunEntry(bool isNewRun)
    {
        _want = isNewRun;
        _done = false;
    }

    public static void Tick()
    {
        if (!Enabled || !_want || _done) return;
        var pc = PlayerCamera.main;
        var body = pc != null ? pc.body : null;
        if (body == null || body.skills == null) return;

        _done = true;
        _want = false;
        Apply(body);
    }

    private static void Apply(Body body)
    {
        try
        {
            var s = body.skills;
            int str = Mathf.Clamp(STR, 1, 40);
            int res = Mathf.Clamp(RES, 1, 40);
            int intel = Mathf.Clamp(INT, 1, 40);

            int oldStr = s.STR, oldRes = s.RES, oldInt = s.INT;

            s.STR = str;
            s.RES = res;
            s.INT = intel;
            s.UpdateExpBoundaries();          // 重算 min/max 经验界
            s.expSTR = s.minSTR;              // 新等级从 0 经验起步（和 Setup 的行为一致）
            s.expRES = s.minRES;
            s.expINT = s.minINT;

            Plugin.Log.LogInfo($"[STEMFishDLC] 初始技能已提升: " +
                               $"力量 {oldStr}→{str} / 耐力 {oldRes}→{res} / 智力 {oldInt}→{intel}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 设置初始技能失败: {e.Message}");
        }
    }
}
