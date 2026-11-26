using System;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  Lang - 语言选择 / 文本文件路径
//  ---------------------------------------------------------------------------
//  cfg [General] Language = auto / zh-CN / en-US / ru-RU
//    auto  → 读游戏自己的 Locale.currentLangName
//            （zh / Chinese / 中文 → zh-CN；ru / Russian → ru-RU；其余 → en-US）
//
//  所有外置文本都走同一套命名：
//    items.<lang>.json / voice.<lang>.json / lore.<lang>.json
//    ui.<lang>.json    / chars.<lang>.json
//
//  回退链：当前语言 → en-US → zh-CN。
//    · 英文 / 中文缺文件时按老规矩回退，保证不会因为少一个 json 就白屏
//    · 俄文（以及以后任何新语言）**逐键合并**：以 en-US 为底，翻译文件里有的键覆盖上去，
//      没翻到的键自动保留英文 —— 半成品翻译也能直接用，不会突然冒出一句中文
// ============================================================================
public static class Lang
{
    public static string Code = "zh-CN";

    // cfg 里的原始取值：auto / zh-CN / en-US / ru-RU。
    // 面板上的「语言」入口改的就是它（见 ModMenu 的 language 行）。
    public static string Mode = "auto";

    public static bool IsChinese => Code != null && Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static bool IsRussian => Code != null && Code.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    // 注意：这是「英文分支」的判定，只对 en 成立。
    // 需要「非中文就用英文兜底」的地方请用 !Lang.IsChinese。
    public static bool IsEnglish => Code != null && Code.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static void Init(Plugin plugin)
    {
        try
        {
            Mode = plugin.Config.Bind("General", "Language", "auto",
                "文本语言 / Text language：auto（跟随游戏 / follow the game）/ zh-CN / en-US / ru-RU").Value;
            if (string.IsNullOrEmpty(Mode)) Mode = "auto";
        }
        catch
        {
            Mode = "auto";
        }

        Apply();

        try
        {
            Plugin.Log.LogInfo($"[STEMFishDLC] 文本语言: {Code}（设置 {Mode}，游戏语言 {SafeLangName()}）");
        }
        catch { }
    }

    // auto / zh-CN / en-US / ru-RU → 实际语言代码
    private static void Apply()
    {
        try
        {
            if (Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                Code = Detect();
            else if (Mode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                Code = "en-US";
            else if (Mode.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                Code = "ru-RU";
            else
                Code = "zh-CN";
        }
        catch { Code = "zh-CN"; }
    }

    /// <summary>俄文文本是否随包提供（没有就不把 Русский 放进循环，免得选了个空白语言）</summary>
    public static bool HasRussian()
    {
        try
        {
            return PathFor("ui", "ru-RU") != null ||
                   PathFor("voice", "ru-RU") != null ||
                   PathFor("chars", "ru-RU") != null;
        }
        catch { return false; }
    }

    // 面板入口：auto → 中文 → English → Русский（有俄文时才出现）→ auto
    public static string CycleMode()
    {
        var list = new System.Collections.Generic.List<string> { "auto", "zh-CN", "en-US" };
        if (HasRussian()) list.Add("ru-RU");

        int i = list.FindIndex(m => m.Equals(Mode, StringComparison.OrdinalIgnoreCase));
        string next = list[(i + 1) % list.Count];
        SetMode(next);
        return next;
    }

    public static void SetMode(string mode)
    {
        Mode = string.IsNullOrEmpty(mode) ? "auto" : mode;
        try
        {
            var e = Plugin.Cfg.Bind("General", "Language", Mode,
                "文本语言 / Text language：auto（跟随游戏 / follow the game）/ zh-CN / en-US / ru-RU");
            e.Value = Mode;
            Plugin.Cfg.Save();
        }
        catch (Exception ex)
        {
            try { Plugin.Log.LogWarning($"[STEMFishDLC] 语言设置写回失败: {ex.Message}"); } catch { }
        }

        Apply();
        Plugin.Log.LogInfo($"[STEMFishDLC] 语言设置 → {Mode}（当前 {Code}）");

        // 能热更的先热更：台词库、界面文案、物品名、档案。其余（少数已写死的 TMP）重启后一致。
        try { FishVoice.ReloadVoice(); } catch { }
        try { UiText.Reload(); } catch { }
        try { ItemText.Reload(); } catch { }
        try { LoreData.Reload(); } catch { }
        try { SfBlood.RefreshText(); } catch { }   // 自定义液体的名字也跟着语言走
    }

    // 面板上显示用的短标签
    public static string ModeLabel()
    {
        if (Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return T("跟随游戏", "Follow game") + " · " + CodeLabel(Code);
        return CodeLabel(Code);
    }

    private static string CodeLabel(string code)
    {
        if (string.IsNullOrEmpty(code)) return "中文";
        if (code.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "Русский";
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "English";
        return "中文";
    }

    private static string SafeLangName()
    {
        try
        {
            if (Locale.currentLang == null) Locale.LoadLanguage();
            return Locale.currentLangName ?? "?";
        }
        catch { return "?"; }
    }

    private static string Detect()
    {
        try
        {
            string n = SafeLangName().ToLowerInvariant();
            if (string.IsNullOrEmpty(n) || n == "?")
            {
                // Locale 还没加载好时的兜底：看系统语言（中文系统 → 中文，俄语系统 → 俄文，其余 → 英文）
                try
                {
                    var sys = Application.systemLanguage;
                    if (sys == SystemLanguage.Chinese || sys == SystemLanguage.ChineseSimplified ||
                        sys == SystemLanguage.ChineseTraditional)
                        return "zh-CN";
                    if (sys == SystemLanguage.Russian && HasRussian())
                        return "ru-RU";
                    return "en-US";
                }
                catch { return "zh-CN"; }
            }
            if (n.Contains("zh") || n.Contains("chinese") || n.Contains("中文") ||
                n.Contains("简体") || n.Contains("繁體") || n.Contains("繁体"))
                return "zh-CN";
            if ((n.Contains("ru") || n.Contains("russian") || n.Contains("рус")) && HasRussian())
                return "ru-RU";
            return "en-US";
        }
        catch { return "zh-CN"; }
    }

    private static string Dir
    {
        get
        {
            try { return Path.Combine(Paths.PluginPath, "STEMFishDLC"); }
            catch { return "STEMFishDLC"; }
        }
    }

    private static string PathFor(string baseName, string code)
    {
        try
        {
            string p = Path.Combine(Dir, baseName + "." + code + ".json");
            return System.IO.File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    // 优先当前语言 → 英文 → 中文
    public static string File(string baseName)
    {
        try
        {
            return PathFor(baseName, Code)
                ?? PathFor(baseName, "en-US")
                ?? PathFor(baseName, "zh-CN")
                ?? Path.Combine(Dir, baseName + "." + Code + ".json");
        }
        catch { return baseName + "." + Code + ".json"; }
    }

    /// <summary>
    /// 读取一份文本表，返回可以直接反序列化的 JSON 字符串。
    /// 中文 / 英文：原样读文件。
    /// 俄文：以 en-US 为底逐键覆盖 ru-RU，没翻到的键保留英文。
    /// </summary>
    public static string Read(string baseName)
    {
        return Read(baseName, File(baseName));
    }

    public static string Read(string baseName, string curPath)
    {
        try
        {
            if (!IsRussian)
                return (curPath != null && System.IO.File.Exists(curPath))
                     ? System.IO.File.ReadAllText(curPath) : null;

            string basePath = PathFor(baseName, "en-US") ?? PathFor(baseName, "zh-CN") ?? curPath;
            if (basePath == null || !System.IO.File.Exists(basePath)) return null;

            var root = JObject.Parse(System.IO.File.ReadAllText(basePath));

            if (curPath != null && System.IO.File.Exists(curPath) &&
                !string.Equals(curPath, basePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    MergeInto(root, JObject.Parse(System.IO.File.ReadAllText(curPath)), baseName);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[STEMFishDLC] {Path.GetFileName(curPath)} 解析失败，" +
                                           $"整份退回英文: {ex.Message}");
                }
            }

            return root.ToString(Formatting.None);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读取 {baseName} 文本表失败: {e.Message}");
            return null;
        }
    }

    // 递归覆盖：对象逐键、数组整段替换（voice 的台词表 / ui 的列表都是这个语义）
    // 例外：lore.logs 按 id 合并，这样少写几篇也不会把其它篇弄丢
    private static void MergeInto(JObject dst, JObject src, string baseName)
    {
        foreach (var p in src.Properties())
        {
            var cur = dst[p.Name];

            if (p.Value is JObject so)
            {
                if (cur is JObject dobj) MergeInto(dobj, so, baseName);
                else dst[p.Name] = so.DeepClone();
                continue;
            }

            if (p.Value is JArray sa)
            {
                if (baseName == "lore" && p.Name == "logs" && cur is JArray dlogs)
                {
                    MergeLogs(dlogs, sa);
                    continue;
                }
                dst[p.Name] = sa.DeepClone();
                continue;
            }

            dst[p.Name] = p.Value.DeepClone();
        }
    }

    private static void MergeLogs(JArray dst, JArray src)
    {
        foreach (var s in src.OfType<JObject>())
        {
            string id = (string)s["id"];
            if (string.IsNullOrEmpty(id)) continue;

            var d = dst.OfType<JObject>().FirstOrDefault(x => (string)x["id"] == id);
            if (d == null) { dst.Add(s.DeepClone()); continue; }

            foreach (var p in s.Properties())
            {
                // 空字符串 = "这条还没翻"，保留英文底稿
                var str = p.Value.Type == JTokenType.String ? (string)p.Value : null;
                if (str != null && str.Length == 0) continue;
                d[p.Name] = p.Value.DeepClone();
            }
        }
    }

    // 短小的代码内回退（没有 json 时用）。俄文用英文兜底。
    public static string T(string zh, string en) => IsChinese ? zh : en;

    // 需要给俄文单独一份时用这个（主菜单 / 选角页这种写死在代码里的按钮）
    public static string T3(string zh, string en, string ru)
        => IsRussian ? ru : (IsChinese ? zh : en);
}
