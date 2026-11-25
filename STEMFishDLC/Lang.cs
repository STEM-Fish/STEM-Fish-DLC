using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  Lang - 语言选择 / 文本文件路径
//  ---------------------------------------------------------------------------
//  cfg [General] Language = auto / zh-CN / en-US
//    auto  → 读游戏自己的 Locale.currentLangName
//            （zh / Chinese / 中文 → zh-CN，其余 → en-US）
//
//  所有外置文本都走同一套命名：
//    items.zh-CN.json / items.en-US.json
//    voice.zh-CN.json / voice.en-US.json
//    lore.zh-CN.json  / lore.en-US.json
//    ui.zh-CN.json    / ui.en-US.json
//
//  英文文件缺失时回退中文，保证不会因为少一个 json 就白屏。
// ============================================================================
public static class Lang
{
    public static string Code = "zh-CN";

    // cfg 里的原始取值：auto / zh-CN / en-US。
    // 面板上的「语言」入口改的就是它（见 ModMenu 的 language 行）。
    public static string Mode = "auto";

    public static bool IsEnglish => Code != null && Code.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static void Init(Plugin plugin)
    {
        try
        {
            Mode = plugin.Config.Bind("General", "Language", "auto",
                "文本语言 / Text language：auto（跟随游戏 / follow the game）/ zh-CN / en-US").Value;
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

    // auto / zh-CN / en-US → 实际语言代码
    private static void Apply()
    {
        try
        {
            if (Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                Code = Detect();
            else if (Mode.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                Code = "en-US";
            else
                Code = "zh-CN";
        }
        catch { Code = "zh-CN"; }
    }

    // 面板入口：按 auto → 中文 → English 循环，写回 cfg。
    // 运行时会立刻换 Code（voice 台词库 2 秒内自动热重载），其余文本重启后完全一致。
    public static string CycleMode()
    {
        string next = Mode.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "zh-CN"
                    : Mode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en-US"
                    : "auto";
        SetMode(next);
        return next;
    }

    public static void SetMode(string mode)
    {
        Mode = string.IsNullOrEmpty(mode) ? "auto" : mode;
        try
        {
            var e = Plugin.Cfg.Bind("General", "Language", Mode,
                "文本语言 / Text language：auto（跟随游戏 / follow the game）/ zh-CN / en-US");
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
    }

    // 面板上显示用的短标签
    public static string ModeLabel()
    {
        if (Mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return T("跟随游戏", "Follow game") + " · " + (IsEnglish ? "English" : "中文");
        return IsEnglish ? "English" : "中文";
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
                // Locale 还没加载好时的兜底：看系统语言（中文系统 → 中文，其余 → 英文）
                try
                {
                    var sys = Application.systemLanguage;
                    if (sys == SystemLanguage.Chinese || sys == SystemLanguage.ChineseSimplified ||
                        sys == SystemLanguage.ChineseTraditional)
                        return "zh-CN";
                    return "en-US";
                }
                catch { return "zh-CN"; }
            }
            if (n.Contains("zh") || n.Contains("chinese") || n.Contains("中文") ||
                n.Contains("简体") || n.Contains("繁體") || n.Contains("繁体"))
                return "zh-CN";
            return "en-US";
        }
        catch { return "zh-CN"; }
    }

    // 优先当前语言；文件不存在就回退中文
    public static string File(string baseName)
    {
        try
        {
            string dir = Path.Combine(Paths.PluginPath, "STEMFishDLC");
            string cur = Path.Combine(dir, baseName + "." + Code + ".json");
            if (System.IO.File.Exists(cur)) return cur;
            string zh = Path.Combine(dir, baseName + ".zh-CN.json");
            if (System.IO.File.Exists(zh)) return zh;
            return cur;
        }
        catch { return baseName + "." + Code + ".json"; }
    }

    // 短小的代码内回退（没有 json 时用）
    public static string T(string zh, string en) => IsEnglish ? en : zh;
}
