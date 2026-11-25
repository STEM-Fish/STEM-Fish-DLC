using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  FishVoice - 自言自语引擎（STEM Fish 的高智能吐槽）
//  ---------------------------------------------------------------------------
//  两条腿走路：
//   1) 扩充原版台词库：patch Locale.GetCharacter(str, chr) postfix
//      → 游戏原有的 104 种情境（bigpain/starving/freezing/bleeding…）都会随机
//        说出我们追加的台词（原版台词仍保留，混合出现）
//   2) 主动吐槽：独立触发器按状态/时间挑台词 → Talker.Talk(lines, null, false, true)
//
//  游戏原生接口（反汇编确认）:
//   Talker.Talk(List<string> lines, Limb limb, bool force, bool resetTalkTimer)
//     - 随机取一句 → "<limb>" 占位符替换成肢体名 → 打字机逐字显示
//     - 门槛: 清醒 / 脑健康>50 / 未洗脑 / （极痛时非 force 不说）
//   Talker.Update 的原版台词类型: bigpain/cantBreathe/dying/starving/freezing/
//     bleeding/verysick/pain/limbinfected/... 共 37 类（character[0] 有 104 个 key）
//
//  台词库: BepInEx/plugins/STEMFishDLC/voice.zh-CN.json（可自由编辑）
// ============================================================================

public static class FishVoice
{
    // 原版台词类型 → 我们追加的台词
    private static Dictionary<string, List<string>> _expand = new Dictionary<string, List<string>>();
    // 自定义吐槽分类 → 台词
    private static Dictionary<string, List<string>> _chatter = new Dictionary<string, List<string>>();
    // 零散台词（key → 台词），给代码里"随机说一句"的场合用，比如换弹
    private static Dictionary<string, List<string>> _lines = new Dictionary<string, List<string>>();

    private static float _nextIdleAt = -1f;
    private static readonly Dictionary<string, float> _lastSaidAt = new Dictionary<string, float>();
    private const float CategoryCooldown = 45f;   // 同分类冷却（秒）

    // ---- 开关（面板/热键控制）----
    public static bool Enabled = true;            // 主动吐槽
    public static bool ExpandEnabled = true;      // 原版台词扩充
    public static void Toggle() => Enabled = !Enabled;
    public static void ToggleExpand() => ExpandEnabled = !ExpandEnabled;
    public static int ChatterLineCount => _chatter.Values.Sum(v => v?.Count ?? 0);
    public static int ExpandLineCount => _expand.Values.Sum(v => v?.Count ?? 0);

    public static void Init(Plugin plugin)
    {
        try
        {
            ReplaceAll = plugin.Config.Bind("Voice", "ReplaceAllOriginal", true,
                "完全替换原版台词：主角只说我们的台词，原版一句都不出（NPC/交易对话不受影响）").Value;
        }
        catch { }

        LoadVoice();
        TryPatchLocale();
        TryExportTemplate();
    }

    // ==========================================================================
    //  导出原版台词模板（给作者改写用）
    //  ---------------------------------------------------------------------------
    //  反汇编：Locale.currentLang.character 是 Dictionary<string,List<string>>[]，
    //  下标 = charType（0 = 主角 experiment）。GetCharacter 就是查这张表。
    //  第一次启动把它整份写成 voice_template.json，作者的流程就变成
    //  「拷一段 → 改成自己的话 → 贴进 voice.zh-CN.json 的 expand」。
    // ==========================================================================
    private class TemplateRoot
    {
        public string note;
        public Dictionary<string, List<string>> expand;
    }

    private static bool _exportTried;

    private static void TryExportTemplate()
    {
        if (_exportTried) return;
        try
        {
            var path = Path.Combine(LoreData.Dir, "voice_template.json");
            if (File.Exists(path)) { _exportTried = true; return; }

            var lang = Locale.currentLang;
            if (lang == null) { try { Locale.LoadLanguage(); } catch { } lang = Locale.currentLang; }
            if (lang == null || lang.character == null || lang.character.Length == 0) return;

            var dict = lang.character[0];
            if (dict == null || dict.Count == 0) return;

            var root = new TemplateRoot
            {
                note = "从游戏原版导出的主角台词库（charType=0）。把需要的键整段复制到 " +
                       "voice.zh-CN.json 的 expand 里，再把句子改成自己的。键名不要改，那是触发条件。",
                expand = new Dictionary<string, List<string>>(),
            };
            foreach (var kv in dict)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                root.expand[kv.Key] = kv.Value != null ? new List<string>(kv.Value) : new List<string>();
            }

            File.WriteAllText(path, JsonConvert.SerializeObject(root, Formatting.Indented));
            _exportTried = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 原版台词已导出：{root.expand.Count} 个情境 → {path}");
        }
        catch (Exception e)
        {
            _exportTried = true;
            Plugin.Log.LogWarning($"[STEMFishDLC] 导出原版台词失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  1) 接管原版台词库（patch Locale.GetCharacter + Talker.Talk）
    //  ---------------------------------------------------------------------------
    //  原版说话链路：
    //      xxx() → Locale.GetCharacter(key, charType) → List<string>
    //            → Talker.Talk(lines, limb, force, reset) → 随机取一句显示
    //  key 就是 "hungry" / "bigpain" 这些，正好和我们 voice.json 的 expand 键一一对应。
    //
    //  所以两个 patch 配合：
    //    GetCharacter  ★替换内容★  有这个 key → 换成我们的；没有 → 返回空列表
    //    Talk          ★保底★      空列表直接不显示（否则 PickRandom 取 0 项会崩）
    //
    //  ReplaceAll = false 时退回旧行为（追加，不替换）。
    // ==========================================================================
    public static bool ReplaceAll = true;
    private static readonly List<string> NoLines = new List<string>();

    private static void TryPatchLocale()
    {
        try
        {
            var harmony = new Harmony("com.stemfish.dlc.voice");
            var target = AccessTools.Method(typeof(Locale), "GetCharacter", new[] { typeof(string), typeof(int) });
            if (target == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Locale.GetCharacter(string,int)，跳过台词接管");
                return;
            }
            var postfix = typeof(FishVoice).GetMethod("GetCharacter_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            var talk = AccessTools.Method(typeof(Talker), "Talk",
                new[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) });
            if (talk != null)
            {
                var pre = typeof(FishVoice).GetMethod("Talk_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(talk, prefix: new HarmonyMethod(pre));
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Talker.Talk(List<string>,...)，空台词保护失效");

            // TalkDelayed 内部同样是 Utils.PickRandom(lines)，空表必炸 —— 剧情过场会因此中断。
            // 这里加一道拦截：空表直接跳过，绝不把异常抛进调用方（Body.EmbedCargo 之类）。
            var delayed = AccessTools.Method(typeof(Talker), "TalkDelayed",
                new[] { typeof(float), typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool), typeof(int) });
            if (delayed != null)
            {
                var pre2 = typeof(FishVoice).GetMethod("TalkDelayed_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(delayed, prefix: new HarmonyMethod(pre2));
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Talker.TalkDelayed(...)，剧情空台词保护失效");

            var info = Harmony.GetPatchInfo(target);
            Plugin.Log.LogInfo($"[STEMFishDLC] 台词接管完成 | postfix={info?.Postfixes?.Count ?? -1} | " +
                               $"模式={(ReplaceAll ? "完全替换" : "扩充")} | 情境 {_expand.Count} 类");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 台词接管 hook 失败: {e}");
        }
    }

    // 原版取台词后：完全替换模式下只用我们的
    // ⚠ 参数名必须与游戏方法一致：Locale.GetCharacter(string str, int chr)
    private static void GetCharacter_Postfix(string str, int chr, ref List<string> __result)
    {
        try
        {
            if (chr != 0) return;                              // 只改主角（0 = experiment）
            if (CharacterSelect.Current != CharacterSelect.Sf07) return;   // 原版角色用原版台词库
            if (string.IsNullOrEmpty(str)) return;

            bool hasMine = _expand.TryGetValue(str, out var mine) && mine != null && mine.Count > 0;

            if (ReplaceAll)
            {
                // 完全替换：有这个情境就说我们的，没有就**保留原版**。
                // ★ 绝对不能返回空列表：游戏里 Talker.TalkDelayed → Utils.PickRandom(list)
                //   对空列表会抛 ArgumentOutOfRangeException。剧情过场（比如第 10 层货物植入
                //   的 Body.EmbedCargo → "cargoimpale"）就在这个调用里，抛出去会把协程打断，
                //   协程末尾的 body.forceNoMovement = false 永远执行不到 → 人物永久不能动。
                __result = hasMine ? new List<string>(mine) : __result;
                return;
            }

            if (!ExpandEnabled) return;
            if (!hasMine || __result == null) return;
            __result.AddRange(mine);
        }
        catch { /* 台词处理失败绝不影响游戏 */ }
    }

    // 空台词直接不显示（PickRandom 对空 List 会越界）
    private static bool TalkDelayed_Prefix(List<string> lines)
    {
        if (lines != null && lines.Count > 0) return true;
        return false;      // 跳过原方法 = 不说这句，但调用方协程继续跑
    }

    // 空台词直接不显示（PickRandom 对空 List 会越界）
    private static bool Talk_Prefix(Talker __instance, List<string> lines)
    {
        try
        {
            if (__instance == null) return true;
            if (__instance.trader != null) return true;        // 交易对话保留原版
            var pc = PlayerCamera.main;
            if (pc == null || __instance.body == null) return true;
            if (__instance.body != pc.body) return true;       // NPC 放行

            if (lines == null || lines.Count == 0) return false;
        }
        catch { }
        return true;
    }

    // ==========================================================================
    //  2) 主动吐槽（定时 + 状态检测）
    // ==========================================================================
    public static void Update()
    {
        if (!CharacterSelect.IsPlayerSf07()) return;      // 原版角色不播 SF-07 的独白
        try
        {
            TryExportTemplate();       // 语言资源可能晚于 Init 就绪，这里再试一次
            PollVoiceReload();         // 改了 voice.zh-CN.json 不用重启，2 秒内自动生效
            if (!Enabled) return;
            var body = PlayerCamera.main?.body;
            if (body == null || !body.conscious) return;       // 没进世界 / 昏迷不说话

            if (_nextIdleAt < 0f)
            {
                _nextIdleAt = Time.time + UnityEngine.Random.Range(20f, 35f);
                return;
            }
            if (Time.time < _nextIdleAt) return;
            _nextIdleAt = Time.time + UnityEngine.Random.Range(28f, 55f);

            string category = PickCategory(body);
            Say(body, category);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 吐槽触发异常: {e.Message}");
        }
    }

    // 按角色状态挑一个最贴切的分类
    private static string PickCategory(Body body)
    {
        try
        {
            // 优先说"要命的"状态
            if (body.bloodVolume < 35f || body.brainHealth < 45f) return "lowhp";
            if (body.averagePain > 45f) return "pain";
            if (body.hunger < 25f) return "hungry";
            if (body.thirst < 25f) return "thirsty";
            if (body.temperature < 30f) return "cold";
            if (body.radiationSickness > 20f) return "radiation";
            if (HasBadWound(body)) return "wound";

            // 环境：附近有尸体 / 深度
            if (NearCorpse(body)) return "corpse";
            int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0;
            if (depth >= 3 && UnityEngine.Random.value < 0.35f) return "depth";

            return "idle";
        }
        catch
        {
            return "idle";
        }
    }

    private static bool HasBadWound(Body body)
    {
        try
        {
            if (body.limbs == null) return false;
            foreach (var l in body.limbs)
            {
                if (l == null) continue;
                if (l.dismembered) return true;
                if (l.muscleHealth < 50f || l.skinHealth < 50f) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool NearCorpse(Body body)
    {
        try
        {
            var pos = (Vector2)body.transform.position;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<CorpseScript>())
            {
                if (c == null) continue;
                if (Vector2.Distance(pos, (Vector2)c.transform.position) < 6f) return true;
            }
        }
        catch { }
        return false;
    }

    // 台词库热重载：文件时间戳变了就重新读一遍
    // ---------------------------------------------------------------------------
    //  作者经常直接改 voice.zh-CN.json（换弹台词、吐槽都是），以前必须重启游戏。
    //  这里每 2 秒看一眼文件修改时间，变了就重读 —— 编辑器保存瞬间写了一半导致
    //  解析失败也没关系，LoadVoice 里 catch 住了，旧台词还在。
    private static DateTime _voiceStamp;
    private static float _voicePoll;

    private static void PollVoiceReload()
    {
        _voicePoll -= Time.unscaledDeltaTime;
        if (_voicePoll > 0f) return;
        _voicePoll = 2f;

        try
        {
            string path = Lang.File("voice");
            if (!File.Exists(path)) return;

            var stamp = File.GetLastWriteTime(path);
            if (stamp == _voiceStamp) return;
            _voiceStamp = stamp;
            if (_voiceStamp == default) return;

            LoadVoice();
            Plugin.Log.LogInfo("[STEMFishDLC] 台词库已热重载");
        }
        catch { }
    }

    // 供语言切换用：立刻按当前语言重读台词库（不等 2 秒轮询）
    public static void ReloadVoice()
    {
        LoadVoice();
        Plugin.Log.LogInfo($"[STEMFishDLC] 台词库已按 {Lang.Code} 重新载入");
    }

    // 从 voice.zh-CN.json 的 "lines" 里随机取一句（key 不存在就用 fallback）
    // ---------------------------------------------------------------------------
    //  给"代码里想随机说一句"的场合用，目前是换弹（GunHandling）。
    //  这样加句、改句只改 json，不用重新编译。
    public static string RandomLine(string key, string fallback)
    {
        if (!ModState.Enabled) return fallback;
        try
        {
            if (!string.IsNullOrEmpty(key) &&
                _lines.TryGetValue(key, out var list) && list != null && list.Count > 0)
            {
                var s = list[UnityEngine.Random.Range(0, list.Count)];
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { }
        return fallback;
    }

    // 按顺序取一整段台词（剧情序列用，不随机；key 不存在返回 null）
    public static List<string> Sequence(string key)
    {
        if (!ModState.Enabled) return null;
        try
        {
            if (!string.IsNullOrEmpty(key) &&
                _lines.TryGetValue(key, out var list) && list != null && list.Count > 0)
                return new List<string>(list);
        }
        catch { }
        return null;
    }

    // 让 Talker 说一句指定台词（换弹等即时反馈用，绕过分类冷却）
    public static void SayLine(string text)
    {
        if (!ModState.Enabled) return;

        // ★ 所有"即时台词"都是 SF-07 自己的独白。捡到档案 / 开弹药箱 / 开局配给 /
        //   杀同类惩罚 …… 这些模块在选原版角色时**照样会调用** SayLine，
        //   所以门槛必须设在这里，而不是指望每个调用点各自判断。
        //   原版角色：一句话都不说，完全用原版台词库。
        if (CharacterSelect.Current != CharacterSelect.Sf07) return;
        if (CharacterSelect.IsTutorial()) return;

        try
        {
            if (string.IsNullOrEmpty(text)) return;
            var body = PlayerCamera.main?.body;
            if (body == null || !body.conscious) return;
            var talker = GetTalker(body);
            if (talker == null) return;
            talker.Talk(new System.Collections.Generic.List<string> { text }, null, false, true);
            Plugin.Log.LogInfo($"[STEMFishDLC] 自言自语（即时）: {text}");
        }
        catch { }
    }

    // 让 Talker 说一句（随机取该分类的一句）
    private static void Say(Body body, string category)
    {
        if (!_chatter.TryGetValue(category, out var lines) || lines == null || lines.Count == 0)
        {
            if (category == "idle" && !_chatter.TryGetValue("idle", out lines)) return;
            if (lines == null || lines.Count == 0) return;
        }

        // 同分类冷却，避免复读
        if (_lastSaidAt.TryGetValue(category, out float last) && Time.time - last < CategoryCooldown)
            return;

        var talker = GetTalker(body);
        if (talker == null) return;

        _lastSaidAt[category] = Time.time;
        // force=false: 让游戏的"极度痛苦/昏迷"逻辑仍然可以拦下不合适的台词
        talker.Talk(lines, null, false, true);
        Plugin.Log.LogInfo($"[STEMFishDLC] 自言自语 [{category}]: {lines[UnityEngine.Random.Range(0, lines.Count)]}");
    }

    private static Talker GetTalker(Body body)
    {
        try
        {
            // Talker 通常挂在 Body 上
            var t = body.GetComponent<Talker>();
            if (t != null) return t;
            // 兜底：找 body 字段匹配的那个
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<Talker>())
                if (candidate != null && candidate.body == body) return candidate;
        }
        catch { }
        return null;
    }

    // ==========================================================================
    //  台词库加载
    // ==========================================================================
    public class VoiceFile
    {
        public Dictionary<string, List<string>> expand = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> chatter = new Dictionary<string, List<string>>();
        public Dictionary<string, List<string>> lines = new Dictionary<string, List<string>>();
    }

    private static void LoadVoice()
    {
        try
        {
            string path = Lang.File("voice");
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 台词库不存在: {path}");
                BuildDefaults();
                return;
            }
            var data = JsonConvert.DeserializeObject<VoiceFile>(File.ReadAllText(path));
            if (data != null)
            {
                _expand = data.expand ?? new Dictionary<string, List<string>>();
                _chatter = data.chatter ?? new Dictionary<string, List<string>>();
                _lines = data.lines ?? new Dictionary<string, List<string>>();
            }
            int e = _expand.Values.Sum(v => v?.Count ?? 0);
            int c = _chatter.Values.Sum(v => v?.Count ?? 0);
            int l = _lines.Values.Sum(v => v?.Count ?? 0);
            Plugin.Log.LogInfo($"[STEMFishDLC] 台词库加载: 扩充 {e} 句（{_expand.Count} 类）/ " +
                               $"吐槽 {c} 句（{_chatter.Count} 类）/ 零散 {l} 句（{_lines.Count} 类）");
            if (e + c + l == 0) BuildDefaults();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 台词库解析失败: {ex.Message}");
            BuildDefaults();
        }
    }

    // 无 json 时的内建保底台词
    private static void BuildDefaults()
    {
        if (Lang.IsEnglish)
        {
            _chatter["idle"] = new List<string> { "...Log: still here.", "It is too quiet down here." };
            _chatter["lowhp"] = new List<string> { "No... I can't stop here." };
        }
        else
        {
            _chatter["idle"] = new List<string> { "……记录：我还在。", "这里安静得让人不安。" };
            _chatter["lowhp"] = new List<string> { "不行……我还不能停在这里。" };
        }
        Plugin.Log.LogWarning("[STEMFishDLC] 使用内建保底台词");
    }
}
