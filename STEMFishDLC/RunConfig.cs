using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  RunConfig - 游戏模式（开局界面选的那五项）
//  ---------------------------------------------------------------------------
//  反汇编确认：
//    PreRunScript.ApplyPreset(string) → runSettings.Clear()，先铺 "normal" 再叠加目标预设
//    PreRunScript.StartRun()          → SaveSystem.loadedRun=false
//                                       → WorldGeneration.runSettings = runSettings → WaitLoad()
//    RunSettings.presets              → normal / relaxed / paradise / desolate / unchipped
//
//  ★ 最重要的约束：runSettings 是 Dictionary<string,object>，
//    WorldGeneration.GetRunSettingFloat 直接 `unbox.any Single` —— 类型不对当场崩。
//    所以一律「ApplyPreset 先拿原版值，再按原值类型写回」。
//
//  另一件事：GetRunSettingFloat / GetRunSettingInt **不做 clamp**。
//    原版界面滑块的 limits（healingrate 0.1~3、metabolismrate 0.1~2）只约束 UI，
//    我们可以直接写 6.0 / 0.08，游戏照单全收。
//
//  各键的实际用途（已反汇编核对）：
//    ambientlight    int   0=0 / 1=0.12 / 2=0.4 → WorldGeneration.UpdateAmbientLight 写 Light2D.intensity
//    healingrate     ×     Body.bloodRegenSpeed 与 HandleBody 里 brainHealth 的自然恢复
//    metabolismrate  ×     BaseHungerRate / BaseThirstRate / 疾病恢复 / 心情下降
//    basetrapdensity ×     陷阱生成密度
//    encumbrancecap  ×     Body.maxEncumberance = 基础值 × 此值（游戏硬上限 3）
//    startingsupplies str  none / light / full
// ============================================================================

public static class RunConfig
{
    // 急速下坠：唯一一个"默认不建存档"的模式（存档键要手动存才建）
    public const string FreefallCode = "FREEFALL";

    public class Difficulty
    {
        public string preset;         // 原版预设 key（打底）
        public string label;          // 中文名
        public string code;           // 英文代号
        public string desc;           // 详细说明（卡片正文）
        public string perks;          // 特色机制（一行）

        public float enemySpawnMul;   // 生成期怪物数量倍率（DistributeEntities）
        public float dynamicDensity;  // 运行时动态投放密度（0 = 关闭）
        public float trapMul;         // 陷阱密度倍率

        public int ambientLight;      // -1 = 不改，否则写进 ambientlight
        public float healingMul;      // -1 = 不改，否则写 healingrate
        public float metabolismMul;   // -1 = 不改，否则写 metabolismrate
        public float fallDamageMul;   // 摔落伤害倍率（1 = 原版）
        public bool infiniteMags;     // 换下的空弹匣自动补满
        public bool hypeMode;         // 娱乐模式：击杀回心情 + 夸张命中反馈
        public string[] bonusKit;     // 开局额外配发的物品 id
        public string[] bans;         // 这一局完全不生成的实体 id（世界生成 + 动态投放都跳过）

        public Color accent;
    }

    // ---- 四个常规模式：只提高生成期的怪物数量，**不开动态刷怪** ----
    // （动态刷怪太密，实测怪多得没法走路。动态只留给「急速下坠」。）
    public static readonly Difficulty[] All =
    {
        new Difficulty
        {
            preset = "normal", label = "普通", code = "NORMAL",
            desc = "标准生存参数。怪物数量提高到原版的两倍多，陷阱略少。",
            perks = "标准",
            enemySpawnMul = 2.2f, dynamicDensity = 0f, trapMul = 0.85f,
            ambientLight = -1, healingMul = -1f, metabolismMul = -1f,
            fallDamageMul = 1f, infiniteMags = false,
            accent = new Color(0.30f, 0.81f, 1.00f, 1f),
        },
        new Difficulty
        {
            preset = "relaxed", label = "简单", code = "RELAXED",
            desc = "生理压力全面放宽：伤势恢复快、饥渴更慢。怪物比原版多，但没到普通档的程度。",
            perks = "生存宽松",
            enemySpawnMul = 1.7f, dynamicDensity = 0f, trapMul = 0.90f,
            ambientLight = -1, healingMul = -1f, metabolismMul = -1f,
            fallDamageMul = 1f, infiniteMags = false,
            accent = new Color(0.36f, 0.85f, 0.65f, 1f),
        },
        new Difficulty
        {
            preset = "paradise", label = "天堂", code = "PARADISE",
            desc = "最宽松的一档：环境明亮、伤势自愈快、几乎不会饿死渴死。怪物只比原版多一点点。",
            perks = "明亮 · 高自愈",
            enemySpawnMul = 1.4f, dynamicDensity = 0f, trapMul = 0.95f,
            ambientLight = -1, healingMul = -1f, metabolismMul = -1f,
            fallDamageMul = 1f, infiniteMags = false,
            accent = new Color(0.91f, 0.75f, 0.35f, 1f),
        },
        new Difficulty
        {
            preset = "desolate", label = "荒凉", code = "DESOLATE",
            desc = "不给补给、不给缓冲。怪物数量是三倍，陷阱照旧密集。适合已经打通一遍的人。",
            perks = "高压",
            enemySpawnMul = 3.0f, dynamicDensity = 0f, trapMul = 0.80f,
            ambientLight = -1, healingMul = -1f, metabolismMul = -1f,
            fallDamageMul = 1f, infiniteMags = false,
            accent = new Color(0.79f, 0.38f, 0.25f, 1f),
        },

        // ---- 第五个：特殊玩法 ----
        new Difficulty
        {
            preset = "normal", label = "急速下坠", code = "FREEFALL",
            desc = "探照灯全开，伤口自己长好，饿渴几乎不走，摔下去也不太疼。\n" +
                   "代价是地面从来没空过 —— 你会一直开枪，弹匣换下来就是满的。",
            perks = "明亮 · 自愈 · 无限弹匣 · 高强度刷怪 · 击杀回心情",
            enemySpawnMul = 1.6f, dynamicDensity = 3.0f, trapMul = 0.30f,
            ambientLight = 2, healingMul = 6f, metabolismMul = 0.08f,
            fallDamageMul = 0.45f, infiniteMags = true, hypeMode = true,
            // 急速下坠：无限电量激光钻 + 砍刀（两个都是原版物品，钻的电量由 LaserDrill 接管）
            bonusKit = new[] { "minilaserdrill", "machete" },
            // 急速下坠是"一直开枪"的节奏，潜行者（蹲人）和洞穴蜱虫（贴地小怪）
            // 只会拖慢节奏、增加无意义死亡 → 这局不刷
            bans = new[] { "shadecrawler", "cavetick" },
            accent = new Color(0.72f, 0.62f, 1.00f, 1f),
        },
    };

    public static Difficulty Current;

    // ---- 可调 ----
    public static bool Enabled = true;
    public static float EncumbranceCap = 2.4f;     // 负重上限倍率（原版 1，硬上限 3）
    public static float EnemyDensityScale = 1f;    // 怪物数量的全局倍率（生成 + 动态都乘）

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Run", "CustomStartScreen", true,
                "用我们自己的模式选择界面替换原版难度选择（关掉就回到原版界面）").Value;
            EncumbranceCap = Mathf.Clamp(plugin.Config.Bind("Run", "EncumbranceCap", 2.4f,
                "负重上限倍率（原版 1，游戏硬上限 3）。一开局就超重就继续加大").Value, 0.5f, 3f);
            EnemyDensityScale = Mathf.Clamp(plugin.Config.Bind("Run", "EnemyDensityScale", 1f,
                "怪物数量全局倍率（生成期与动态投放都乘这个；1 = 按界面显示的值）").Value, 0f, 4f);

            SpawnDirector.Init(plugin);
            PatchStartRun();
            Plugin.Log.LogInfo($"[STEMFishDLC] 开局配置: 负重上限 ×{EncumbranceCap:0.0} | " +
                               $"怪物全局 ×{EnemyDensityScale:0.0} | {All.Length} 个模式");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 开局配置读取失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  应用一套模式
    // ==========================================================================
    public static bool Apply(Difficulty d)
    {
        if (d == null) return false;

        var pre = PreRunScript.instance;
        if (pre == null)
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 应用模式失败：PreRunScript.instance 为空");
            return false;
        }

        try
        {
            if (pre.runSettings == null)
                pre.runSettings = new Dictionary<string, object>();

            pre.ApplyPreset(d.preset);          // 原版预设打底（含全部键 + 正确类型）

            var s = pre.runSettings;
            Scale(s, "basetrapdensity", d.trapMul);
            Scale(s, "encumbrancecap", EncumbranceCap);
            SetString(s, "startingsupplies", "none");   // 原版初始物资不发，只发我们自己的

            if (d.ambientLight >= 0) SetInt(s, "ambientlight", d.ambientLight);
            if (d.healingMul > 0f) SetFloat(s, "healingrate", d.healingMul);
            if (d.metabolismMul > 0f) SetFloat(s, "metabolismrate", d.metabolismMul);

            // ★ 新的一局默认「有芯片」。
            //   PreRunScript.runSettings 是跨局复用的同一个字典：屏蔽器往里面写过
            //   unchipped=true 之后，如果某一局没经过 ApplyPreset 就开局，这个 true 会漏到下一局。
            //   这里显式钉死 false；读档不受影响 —— TryLoadGame 会用存档里的 runSettings 覆盖。
            // （如果以后加了「未植入芯片」预设模式，就尊重预设，不强行改）
            if (d.preset != "unchipped") SetBool(s, "unchipped", false);

            // ★ 急速下坠 = 「无尽模式」：原版 runSettings 里的 `endless`
            //   （WorldGeneration.Start 用它算 canStartMercRun，BlueHeart 用它跳过结局循环）。
            //   其它模式必须显式写 false —— 和 unchipped 一样，这个字典是跨局复用的。
            SetBool(s, "endless", d.code == FreefallCode);
            try
            {
                var w2 = WorldGeneration.world;
                if (w2 != null && WorldGeneration.runSettings != null && WorldGeneration.runSettings != s)
                    WorldGeneration.runSettings["endless"] = d.code == FreefallCode;
            }
            catch { }
            try
            {
                var w = WorldGeneration.world;
                if (w != null && WorldGeneration.runSettings != null && WorldGeneration.runSettings != s)
                    WorldGeneration.runSettings["unchipped"] = false;
            }
            catch { }

            Current = d;
            SpawnDirector.SetMode(d.enemySpawnMul * EnemyDensityScale,
                                  d.dynamicDensity * EnemyDensityScale,
                                  d.dynamicDensity > 0.01f,
                                  d.bans);
            HypeMode.SetActive(d.hypeMode);

            Plugin.Log.LogInfo($"[STEMFishDLC] 模式已设置: {d.label}({d.preset}) | " +
                               $"生成×{d.enemySpawnMul * EnemyDensityScale:0.0} " +
                               $"动态×{d.dynamicDensity * EnemyDensityScale:0.0} " +
                               $"陷阱×{d.trapMul:0.00} 摔落×{d.fallDamageMul:0.00} " +
                               $"{(d.infiniteMags ? "无限弹匣 " : "")}" +
                               $"{(d.ambientLight >= 0 ? $"环境光={d.ambientLight} " : "")}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 应用模式失败: {e.Message}");
            return false;
        }
    }

    // 开新一局（走原版 StartRun，保证存档/世界流程一致）
    public static void BeginRun()
    {
        var pre = PreRunScript.instance;
        if (pre == null) return;

        // 原版 StartRun 在没做过基础课程时会弹 tutorialWarning 并中止。
        // 我们的界面已经替玩家做了选择，这里直接把标记点上，否则点了模式没反应。
        try
        {
            if (PlayerPrefs.GetInt("didbasiccourse", 0) == 0)
                PlayerPrefs.SetInt("didbasiccourse", 1);
        }
        catch { }

        try { pre.StartRun(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 开始新局失败: {e.Message}"); }
    }

    // ==========================================================================
    //  开新局兜底：所有「新游戏」入口都从"有芯片"开始
    //  ---------------------------------------------------------------------------
    //  PreRunScript.runSettings 是跨局复用的字典；屏蔽器往里面写过 unchipped=true 后，
    //  如果某次开局没走 ApplyPreset（原版界面直接点开始、教程退出后的残留等），
    //  这个 true 就会漏到下一局。这里在 StartRun 入口统一钉死 false。
    //  （读档走的是 LoadRun，不经过这里，所以存档里的无芯片状态不受影响。）
    // ==========================================================================
    private static void PatchStartRun()
    {
        try
        {
            var m = AccessTools.Method(typeof(PreRunScript), "StartRun");
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 PreRunScript.StartRun，新局芯片状态兜底失效"); return; }
            var pre = typeof(RunConfig).GetMethod("StartRun_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.startrun").Patch(m, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 新局芯片状态兜底 hook 完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 新局芯片状态兜底 hook 失败: {e.Message}");
        }
    }

    private static void StartRun_Prefix(PreRunScript __instance)
    {
        try
        {
            if (__instance == null || __instance.runSettings == null) return;

            // 尊重原版隐藏预设「未植入芯片」：选的本来就是它，就别强行改回有芯片
            try
            {
                var presets = RunSettings.presets;
                int i = __instance.currentPreset;
                if (presets != null && i >= 0 && i < presets.Count &&
                    presets[i] != null && presets[i].presetName == "unchipped") return;
            }
            catch { }

            __instance.runSettings["unchipped"] = false;
        }
        catch { }
    }

    // ==========================================================================
    //  按原值类型写回（绝不改变类型）
    // ==========================================================================
    private static void Scale(Dictionary<string, object> d, string key, float mul)
    {
        if (d == null || !d.ContainsKey(key)) return;

        var v = d[key];
        if (v == null) return;

        float cur;
        try { cur = Convert.ToSingle(v); }
        catch { return; }

        float val = cur * mul;

        if (v is float) d[key] = val;
        else if (v is int) d[key] = Mathf.RoundToInt(val);
        else if (v is double) d[key] = (double)val;
        else d[key] = val;
    }

    private static void SetFloat(Dictionary<string, object> d, string key, float val)
    {
        if (d == null || !d.ContainsKey(key)) return;
        if (d[key] is float) d[key] = val;
    }

    private static void SetInt(Dictionary<string, object> d, string key, int val)
    {
        if (d == null || !d.ContainsKey(key)) return;
        if (d[key] is int) d[key] = val;
    }

    private static void SetBool(Dictionary<string, object> d, string key, bool val)
    {
        if (d == null) return;
        // 原值是 bool 就直接写；字典里还没有这个键（例如新加的 endless）也补上，
        // GetRunSettingBool 读的就是 bool 值，补进来是安全的。
        d[key] = val;
    }

    private static void SetString(Dictionary<string, object> d, string key, string val)
    {
        if (d == null || !d.ContainsKey(key)) return;
        if (d[key] is string) d[key] = val;
    }
}
