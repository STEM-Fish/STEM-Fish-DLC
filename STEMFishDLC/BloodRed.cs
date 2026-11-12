using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  BloodRed - 把主角的血改成红色
//  ---------------------------------------------------------------------------
//  游戏里"人形生物"的血色是三处独立的东西：
//    ① 肢体材质 `Limb` 的 `_BloodDark` / `_BloodLight`（受伤时皮肤/毛上的血渍）
//       原值：深 (0.73, 0.44, 0.07) / 浅 (1.00, 0.86, 0.00) —— 橙黄色（克隆体血）
//    ② 肢体上挂的流血粒子 `BleedParticle` 的 startColor，同上橙色
//       还有一个水下版本 `Special/UnderwaterBleedParticle`
//    ③ 落地/溅墙的贴花 `wallbloodhuman`，这张本来就是红的 (218,2,5)
//  所以只改 ①②，整体就统一成红色了。三者都在 `Limb.Awake` 里初始化，
//  所以挂 Awake 的 postfix 一次性上色。
//
//  ⚠ 只处理人形（`Limb`）。商人的肢体用的是 `TraderLimbMat`，材质名里带 "Trader"，
//    这里跳过 —— 他是另一套生理，血本来就不是红的。
// ============================================================================
public static class BloodRed
{
    public static bool Enabled = true;

    // 深色（干涸/稀薄）与亮色（新鲜）—— 与贴花 wallbloodhuman 的 (218,2,5) 同一色系
    public static Color Dark = new Color(0.42f, 0.03f, 0.03f, 1f);
    public static Color Light = new Color(0.80f, 0.08f, 0.08f, 1f);

    private static readonly HashSet<int> _done = new HashSet<int>();

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("General", "RedBlood", true,
                "把主角的血改成红色（肢体血渍 + 流血粒子；原版是克隆体的橙黄色）").Value;
            Dark = ParseHex(plugin.Config.Bind("General", "BloodDark", "#6B0808",
                "血液深色（#RRGGBB）").Value, new Color(0.42f, 0.03f, 0.03f, 1f));
            Light = ParseHex(plugin.Config.Bind("General", "BloodLight", "#CC1414",
                "血液亮色（#RRGGBB）").Value, new Color(0.80f, 0.08f, 0.08f, 1f));
        }
        catch { }

        if (!Enabled) return;
        try
        {
            var m = AccessTools.Method(typeof(Limb), "Awake");
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Limb.Awake，血色不改"); return; }
            var post = typeof(BloodRed).GetMethod("Awake_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.bloodred").Patch(m, postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 血液上色 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 血液上色 hook 失败: {e.Message}");
        }
    }

    private static Color ParseHex(string s, Color fallback)
    {
        try
        {
            Color c;
            if (!string.IsNullOrEmpty(s) && ColorUtility.TryParseHtmlString(s.Trim(), out c)) return c;
        }
        catch { }
        return fallback;
    }

    private static void Awake_Postfix(Limb __instance)
    {
        if (!Enabled || __instance == null) return;
        try
        {
            int id = __instance.GetInstanceID();
            if (_done.Contains(id)) return;
            _done.Add(id);

            var fMat = AccessTools.Field(typeof(Limb), "mat");
            var mat = fMat != null ? fMat.GetValue(__instance) as Material : null;
            if (mat != null)
            {
                // 商人 / 其它族的材质跳过着色
                string mn = mat.name ?? "";
                if (mn.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) >= 0) return;

                if (mat.HasProperty("_BloodDark")) mat.SetColor("_BloodDark", Dark);
                if (mat.HasProperty("_BloodLight")) mat.SetColor("_BloodLight", Light);
            }

            Tint(AccessTools.Field(typeof(Limb), "bleedPart"), __instance);
            Tint(AccessTools.Field(typeof(Limb), "waterBleedPart"), __instance);
        }
        catch (Exception e)
        {
            if (_done.Count <= 1) Plugin.Log.LogWarning($"[STEMFishDLC] 血液上色失败: {e.Message}");
        }
    }

    private static void Tint(FieldInfo f, Limb limb)
    {
        try
        {
            if (f == null) return;
            var ps = f.GetValue(limb) as ParticleSystem;
            if (ps == null) return;
            var main = ps.main;
            main.startColor = new ParticleSystem.MinMaxGradient(Dark, Light);
        }
        catch { }
    }
}
