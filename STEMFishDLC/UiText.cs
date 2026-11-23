using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  UiText - 界面文案外置
//  ---------------------------------------------------------------------------
//  以前「进入游戏」界面（模式卡片、标题、说明）的文案全部硬编码在
//  `RunConfig.All` 和 `NewRunPanel` 里，改一个字都要重编译。
//  现在优先读 `BepInEx/plugins/STEMFishDLC/ui.zh-CN.json`：
//
//      {
//        "modes": {
//          "NORMAL": { "label": "普通", "desc": "…", "perks": "标准" }
//        },
//        "newrun": {
//          "title": "STEM-FISH DLC",
//          "sub":   "选择一局新游戏，或者继续之前的存档"
//        }
//      }
//
//  文件不存在 / 解析失败 → 全部回落到代码里的默认值（不会因为少个 json 就白屏）。
//  改完重启游戏生效（面板是开局建一次，不跟着 json 热更）。
// ============================================================================
public static class UiText
{
    public static string FilePath = "";

    private static readonly Dictionary<string, Dictionary<string, string>> _data =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    private static bool _loaded;
    private static bool _initTried;

    public static void Init(Plugin plugin)
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            // 和 voice / items / lore 放一起：plugins/STEMFishDLC/
            FilePath = Lang.File("ui");

            if (File.Exists(FilePath)) Load(FilePath);
            else Plugin.Log.LogInfo($"[STEMFishDLC] 没有 {Path.GetFileName(FilePath)}，界面文案用代码内置默认值");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 界面文案载入失败: {e.Message}");
        }

        ApplyModes();
    }

    private static void Load(string path)
    {
        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            _data.Clear();
            int n = 0;
            foreach (var sec in root.Properties())
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (sec.Value is JObject o)
                {
                    foreach (var kv in o.Properties())
                    {
                        if (kv.Value is JObject inner)           // modes.NORMAL.desc 这种两层
                        {
                            foreach (var kv2 in inner.Properties())
                                dict[kv.Name + "." + kv2.Name] = kv2.Value.ToString();
                        }
                        else dict[kv.Name] = kv.Value.ToString();
                        n++;
                    }
                }
                _data[sec.Name] = dict;
            }
            _loaded = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 界面文案已载入：{n} 条（{Path.GetFileName(path)}）");
        }
        catch (Exception e)
        {
            _loaded = false;
            Plugin.Log.LogWarning($"[STEMFishDLC] 界面文案解析失败（用默认值）: {e.Message}");
        }
    }

    // 取一条通用 UI 文案（ui.<lang>.json 的 strings 段）
    public static string T(string key, string fallback)
    {
        return Get("strings", key, fallback);
    }

    // 取一条文案；没有就用 fallback
    public static string Get(string section, string key, string fallback)
    {
        if (!_loaded) return fallback;
        try
        {
            if (_data.TryGetValue(section, out var dict) && dict != null &&
                dict.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
        }
        catch { }
        return fallback;
    }

    // 把 modes.* 覆盖到 RunConfig.All（label / desc / perks）
    private static void ApplyModes()
    {
        if (!_loaded) return;
        try
        {
            int n = 0;
            for (int i = 0; i < RunConfig.All.Length; i++)
            {
                var d = RunConfig.All[i];
                if (d == null || string.IsNullOrEmpty(d.code)) continue;

                string label = Get("modes", d.code + ".label", null);
                string desc = Get("modes", d.code + ".desc", null);
                string perks = Get("modes", d.code + ".perks", null);

                if (!string.IsNullOrEmpty(label)) { d.label = label; n++; }
                if (!string.IsNullOrEmpty(desc)) d.desc = desc;
                if (!string.IsNullOrEmpty(perks)) d.perks = perks;
            }
            if (n > 0) Plugin.Log.LogInfo($"[STEMFishDLC] 模式文案已按 json 覆盖 {n} 项");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 覆盖模式文案失败: {e.Message}");
        }
    }
}
