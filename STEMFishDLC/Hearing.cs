using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;

namespace STEMFishDLC;

// ============================================================================
//  Hearing - 战术耳塞：抵抗射击噪音 + 戴上后全局"闷声"
//  ---------------------------------------------------------------------------
//  ★ 反汇编确认的原版听力系统（不是猜的）：
//
//  ① 听力损失只有一个来源：GunScript.Fire
//         body.hearingLoss += gun.loudness * (body.hearingLoss > 20 ? 0.2 : 1)
//     （即每开一枪按枪的 loudness 累加；超过 20 之后累积速度降到 20%）
//     自然恢复在 Body.HandleBody：hearingLoss -= 0.05 * deltaTime（很慢，20 秒掉 1）
//     其它写入点只有：控制台的 reset、嗅盐类物品 -8、MindwipeScript（剧情洗脑）——都不是环境噪音。
//
//  ② 听力损失怎么变成"听不清"：PlayerCamera.HandleAmbientSound 每帧算
//         m = 1 - Max(hearingLoss*0.01,
//                     1 - Clamp(consciousness*0.01, 0.3, 1),
//                     brainHealth*0.01)
//         SoundCutoff = Lerp(50, 22000, m*m)
//     然后 mixer.SetFloat("SoundCutoff", ...) —— 这是个**全局低通**，
//     50Hz 几乎全闷、22000Hz 等于不衰减。
//
//  → 我们要做的两件事正好对应这两处：
//     A. 拦截 Fire 造成的 hearingLoss 增量（戴着耳塞就只吃 10%）
//     B. 在 HandleAmbientSound 之后**覆盖** SoundCutoff（取更闷的那个），
//        这样不受损也能听到"塞住耳朵"的效果；而且已经受损时不会被我们改好。
//
//  ★ 只读/写 SoundCutoff，绝不碰 mixer 的 Volume —— 那是玩家自己的音量设置。
// ============================================================================

public static class Hearing
{
    public const string PlugsId = "earplugs";

    public static bool Enabled = true;
    public static float Protect = 0.9f;      // 听力损伤削减比例（0.9 = 只吃 10%）
    public static float CutoffHz = 2200f;    // 戴上耳塞后的低通截止（越低越闷）
    public static float Strength = 1f;       // 闷声强度 0~1（1 = 完全按 CutoffHz）

    private static bool _worn;               // 当前是否戴着
    private static float _plug;              // 0~1 平滑量（戴上/摘下不硬切）
    private static float _checkT;
    private static Body _body;

    // ==========================================================================
    //  初始化
    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Hearing", "Enabled", true,
                "战术耳塞：抵抗射击噪音造成的听力损失，并在佩戴时对所有声音做低通处理").Value;
            Protect = Mathf.Clamp(plugin.Config.Bind("Hearing", "Protect", 0.9f,
                "听力损伤削减比例（0.9 = 只承受 10% 的损失，1 = 完全不掉）").Value, 0f, 1f);
            CutoffHz = Mathf.Clamp(plugin.Config.Bind("Hearing", "CutoffHz", 2200f,
                "戴耳塞时的低通截止频率（Hz，越低越闷；1200 很闷 / 2200 闷但听得清 / 4000 轻微）").Value, 300f, 20000f);
            Strength = Mathf.Clamp(plugin.Config.Bind("Hearing", "Strength", 1f,
                "闷声强度（0.5 = 只闷一半，1 = 完全按上面的截止频率）").Value, 0f, 1f);
        }
        catch { }

        if (!Enabled) { Plugin.Log.LogInfo("[STEMFishDLC] 耳塞：关闭"); return; }

        var harmony = new Harmony("com.stemfish.dlc.hearing");
        int n = 0;

        // A. 射击噪音 → 听力损伤
        var fire = AccessTools.Method(typeof(GunScript), "Fire");
        if (fire != null)
        {
            harmony.Patch(fire,
                prefix: new HarmonyMethod(typeof(Hearing).GetMethod("Fire_Prefix", BindingFlags.NonPublic | BindingFlags.Static)),
                postfix: new HarmonyMethod(typeof(Hearing).GetMethod("Fire_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
            var pi = Harmony.GetPatchInfo(fire);
            if (pi != null) n += pi.Prefixes.Count + pi.Postfixes.Count;
        }
        else Plugin.Log.LogWarning("[STEMFishDLC] 耳塞：找不到 GunScript.Fire");

        // B. 全局低通覆盖
        var ambient = AccessTools.Method(typeof(PlayerCamera), "HandleAmbientSound");
        if (ambient != null)
        {
            harmony.Patch(ambient, postfix: new HarmonyMethod(
                typeof(Hearing).GetMethod("Ambient_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
            var pi2 = Harmony.GetPatchInfo(ambient);
            if (pi2 != null) n += pi2.Postfixes.Count;
        }
        else Plugin.Log.LogWarning("[STEMFishDLC] 耳塞：找不到 PlayerCamera.HandleAmbientSound");

        Plugin.Log.LogInfo($"[STEMFishDLC] 耳塞就绪 | 合力={n} | 削减 {Protect * 100f:0}% 损伤 | " +
                           $"低通 {CutoffHz:0}Hz × {Strength:0.00}");
    }

    // ==========================================================================
    //  每帧：刷新"是否戴着" + 平滑过渡
    // ==========================================================================
    public static void Tick()
    {
        if (!Enabled) return;

        _checkT += Time.unscaledDeltaTime;
        if (_checkT >= 0.2f)                 // 别每帧遍历 wearable 列表
        {
            _checkT = 0f;
            try
            {
                var b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (b != _body) { _body = b; _worn = false; }
                if (b != null) _worn = HasPlugs(b);
            }
            catch { _worn = false; }
        }

        // 0.35 秒过渡：戴上/摘下的瞬间不会"啪"一下换声音
        _plug = Mathf.MoveTowards(_plug, _worn ? 1f : 0f, Time.unscaledDeltaTime * 2.8f);
    }

    private static bool HasPlugs(Body body)
    {
        try
        {
            // HasWearable(string) 只看 desiredWearLimb 那一个肢体的子物体，比
            // GetAllWearables()（遍历全部肢体 + 新建 List）省得多，适合高频调用。
            // ⚠ 它内部要查 Item.GlobalItems[itemid] → 未注册的 id 会 KeyNotFound，
            //   所以这里 enclose 在 try 里；earplugs 已由 ItemRegistry 注册。
            return body.HasWearable(PlugsId);
        }
        catch { }
        return false;
    }

    public static bool Wearing => _worn;

    // ==========================================================================
    //  A. 拦截听力损伤
    //  ---------------------------------------------------------------------------
    //  原版在 Fire 里对**持枪者**做 body.hearingLoss += loudness * k。
    //  我们没法"少加一点"，所以 prefix 记下玩家的原值，postfix 把增量按比例退回去。
    //
    //  ★ 不需要判断持枪者是谁：Fire 只在持枪者身上加听力损失，
    //    所以"玩家 hearingLoss 的差值"天然就等于"这一枪打在玩家身上加了多少"。
    //    敌人开枪时玩家的值不动 → delta = 0 → 什么都不做。
    //    （GunScript.body 是 nonpub 属性，正好省掉一层反射）
    //
    //  ★ Fire 是同步执行的，prefix 和 postfix 之间不可能插进别的代码改 hearingLoss，
    //    所以这个"差值回退"是精确的，不会误伤嗅盐/剧情那类正常变化。
    // ==========================================================================
    private static float _hlBefore;
    private static Body _hlBody;
    private static int _guard;      // 防重入（万一有嵌套调用）

    private static void Fire_Prefix(GunScript __instance)
    {
        if (!_worn || _plug < 0.5f) return;
        try
        {
            var pc = PlayerCamera.main;
            var b = pc != null ? pc.body : null;
            if (b == null) return;

            _hlBody = b;
            _hlBefore = b.hearingLoss;
            _guard++;
        }
        catch { _hlBody = null; }
    }

    private static void Fire_Postfix(GunScript __instance)
    {
        if (_guard <= 0) return;
        _guard--;

        var b = _hlBody;
        if (b == null) { if (_guard <= 0) _guard = 0; return; }

        try
        {
            float delta = b.hearingLoss - _hlBefore;     // 游戏刚加上去的量
            if (delta > 0f)
            {
                float keep = Mathf.Clamp01(1f - Protect);
                b.hearingLoss = Mathf.Max(0f, _hlBefore + delta * keep);
            }
        }
        catch { }
        finally { if (_guard <= 0) { _guard = 0; _hlBody = null; } }
    }

    // ==========================================================================
    //  B. 全局低通覆盖
    //  ---------------------------------------------------------------------------
    //  游戏刚在 HandleAmbientSound 里设过 SoundCutoff，我们在它之后：
    //    · 取"更闷的那个"，所以听力已经受损时不会被我们改好听
    //    · 按 _plug 在对数频率轴上插值（人耳是对数感知，线性插值听着很怪）
    // ==========================================================================
    private static AudioMixer _mixer;
    private static float _mixerFindT = -99f;
    private static int _getFail;
    private static int _getFailLogged;

    private static void Ambient_Postfix(PlayerCamera __instance)
    {
        if (_plug <= 0.002f || Strength <= 0.001f) return;

        try
        {
            var mixer = GetMixer();
            if (mixer == null) return;

            float cur;
            if (!mixer.GetFloat("SoundCutoff", out cur) || cur <= 0f)
            {
                // ★ 读不到游戏刚设的值就什么都别做。
                //   不能像早期那样"读不到就当作 22000"，否则玩家听力已经严重受损
                //   （游戏把截止压到 50Hz）时，我们会把它设回 2200Hz ——
                //   等于戴上耳塞把耳朵治好了，正好反了。
                //   （游戏自己就在 SetFloat 这个参数，说明它是 mixer 的 exposed 参数，
                //     正常情况下 GetFloat 一定读得到；这里只是兜底）
                _getFail++;
                if (_getFail == 60 && _getFailLogged == 0)
                {
                    _getFailLogged = 1;
                    Plugin.Log.LogWarning("[STEMFishDLC] 耳塞：读不到 mixer 参数 SoundCutoff，" +
                                          "闷声效果已停用（听力保护仍然生效）。");
                }
                return;
            }
            _getFail = 0;

            float plugged = Mathf.Min(cur, CutoffHz);    // 只往"更闷"的方向走
            float k = Mathf.Clamp01(_plug * Strength);

            // 对数插值：22000 → 2200 的过程听上去是均匀变闷，而不是"突然掉下去"
            float target = Mathf.Exp(Mathf.Lerp(Mathf.Log(Mathf.Max(1f, cur)),
                                                Mathf.Log(Mathf.Max(1f, plugged)), k));
            mixer.SetFloat("SoundCutoff", target);
        }
        catch { }
    }

    private static AudioMixer GetMixer()
    {
        if (_mixer != null) return _mixer;

        // 缓存失效时最多每 2 秒重找一次，避免每帧 new GetComponent
        if (Time.unscaledTime - _mixerFindT < 2f) return _mixer;
        _mixerFindT = Time.unscaledTime;

        try
        {
            var w = WorldGeneration.world;
            var grp = w != null ? w.soundMixerGroup : null;
            if (grp != null) _mixer = grp.audioMixer;
        }
        catch { }
        return _mixer;
    }
}
