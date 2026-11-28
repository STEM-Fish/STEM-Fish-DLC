using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  FreefallMusic - 急速下坠：关掉"遭遇/战斗"时的心跳紧张音
//  ---------------------------------------------------------------------------
//  反汇编确认：原版并没有"遇到敌人就切歌"这套逻辑。战斗时听到的那层音乐是
//  `MusicManager.critSource` / `critSourceUnc` —— 两个常驻 AudioSource 的 drone，
//  由 `body.heartRate > 5` 触发，音量 = droneVolume × 系数，每帧 MoveTowards 渐变。
//  心率在受伤、恐惧、剧烈活动时都会上去，所以打着架一直响。
//
//  做法：patch `MusicManager.Update`，在急速下坠模式下把 `droneVolume` 临时改成 0，
//  Update 内部算出来的目标音量就都是 0，会平滑淡出（不是硬停，不会有咔哒声），
//  postfix 再还原字段，不影响其它模式。
//
//  ★ 保留条件：`Body.horrifiedLevel > 0` —— 这是 ElderThornback（棘背长老）
//    在 Update 里拉起来的恐惧等级，长老离开/死亡会清零。用它可以"长老在场时不静音"，
//    而且比 FindObjectOfType 便宜得多。
// ============================================================================

public static class FreefallMusic
{
    public static bool Enabled = true;

    private static FieldInfo _droneField;
    private static bool _patched;
    private static bool _logged;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Freefall", "MuteCombatDrone", true,
                "急速下坠：关掉遭遇/战斗时的心跳紧张音（棘背长老在场时自动保留）").Value;
        }
        catch { }

        _droneField = AccessTools.Field(typeof(MusicManager), "droneVolume");
        if (_droneField == null)
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 找不到 MusicManager.droneVolume，战斗音乐静音不可用");
            return;
        }

        Patch();
    }

    private static void Patch()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(MusicManager), "Update");
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 MusicManager.Update");
                return;
            }

            var pre = typeof(FreefallMusic).GetMethod("Update_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            var post = typeof(FreefallMusic).GetMethod("Update_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.freefallmusic").Patch(m,
                prefix: new HarmonyMethod(pre),
                postfix: new HarmonyMethod(post));

            _patched = true;
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 急速下坠战斗音 hook 完成 | pre={info?.Prefixes?.Count ?? -1} post={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 战斗音 hook 失败: {e.Message}");
        }
    }

    private static void Update_Prefix(MusicManager __instance, out float __state)
    {
        __state = -1f;
        try
        {
            if (!Enabled || __instance == null) return;

            // 只在急速下坠生效
            if (RunConfig.Current == null || RunConfig.Current.code != RunConfig.FreefallCode) return;

            // 长老在场：保留紧张音
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body != null && body.horrifiedLevel > 0.01f) return;

            __state = (float)_droneField.GetValue(__instance);
            _droneField.SetValue(__instance, 0f);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("[STEMFishDLC] 急速下坠：战斗紧张音已静音（长老在场时自动恢复）");
            }
        }
        catch { __state = -1f; }
    }

    private static void Update_Postfix(MusicManager __instance, float __state)
    {
        if (__state < 0f || __instance == null) return;
        try { _droneField.SetValue(__instance, __state); }
        catch { }
    }
}
