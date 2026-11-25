using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  ItemText - 物品显示名 / 描述的**外部文本表**
//  ---------------------------------------------------------------------------
//  文件：plugins/STEMFishDLC/items.zh-CN.json
//  格式：
//    {
//      "platecarrier": { "name": "战术弹挂甲", "desc": "…" },
//      "mdrz9":        { "name": "MDRZ9 7.62×51", "desc": "…" }
//    }
//
//  优先级：**JSON > 代码里的内置文本**
//    · 文件存在且能解析 → 名字/描述一律以文件为准（你改完重启就生效）
//    · 文件缺失 → 自动从内置文本生成一份，方便你直接编辑
//    · 文件里缺某个 id → 那一条回落到内置文本（不会显示成 id）
//
//  这样改文案完全不用碰 dll。
// ============================================================================

public static class ItemText
{
    public class Entry
    {
        public string name;
        public string desc;
    }

    public static string Dir => Path.Combine(Paths.PluginPath, "STEMFishDLC");
    public static string Path_ => Lang.File("items");

    private static readonly Dictionary<string, Entry> _file = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Entry> _builtin = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static bool _loaded;

    // 代码里注册内置文本（ItemRegistry 调用）
    public static void SetBuiltin(string id, string name, string desc)
    {
        if (string.IsNullOrEmpty(id)) return;
        _builtin[id] = new Entry { name = name, desc = desc };
    }

    // 取最终生效的文本：JSON 优先
    public static string Name(string id)
    {
        EnsureLoaded();
        if (_file.TryGetValue(id, out var f) && !string.IsNullOrEmpty(f.name)) return f.name;
        if (_builtin.TryGetValue(id, out var b) && !string.IsNullOrEmpty(b.name)) return b.name;
        return null;
    }

    public static string Desc(string id)
    {
        EnsureLoaded();
        if (_file.TryGetValue(id, out var f) && !string.IsNullOrEmpty(f.desc)) return f.desc;
        if (_builtin.TryGetValue(id, out var b) && !string.IsNullOrEmpty(b.desc)) return b.desc;
        return null;
    }

    // 语言切换后重读（物品名在下次打开背包时生效）
    public static void Reload()
    {
        _loaded = false;
        _file.Clear();
        EnsureLoaded();
    }

    // ==========================================================================
    //  载入 / 生成
    // ==========================================================================
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(Path_))
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(Path_));
                if (d != null)
                {
                    _file.Clear();
                    foreach (var kv in d)
                        if (kv.Value != null && !string.IsNullOrEmpty(kv.Key)) _file[kv.Key] = kv.Value;
                }
                Plugin.Log.LogInfo($"[STEMFishDLC] 物品文本表已载入 {_file.Count} 条（{Path_}）");
            }
            else
            {
                Plugin.Log.LogInfo($"[STEMFishDLC] 物品文本表不存在，将自动生成: {Path_}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 物品文本表读取失败（改用内置文本）: {e.Message}");
        }
    }

    // 首次运行时导出一份模板（把内置文本写出去，方便玩家改）
    public static void ExportTemplateIfMissing()
    {
        try
        {
            if (File.Exists(Path_)) return;
            if (_builtin.Count == 0) return;
            if (Lang.IsEnglish) return;          // 英文模式不导出中文内置模板

            Directory.CreateDirectory(Dir);
            var d = new Dictionary<string, Entry>();
            foreach (var kv in _builtin) d[kv.Key] = kv.Value;
            File.WriteAllText(Path_, JsonConvert.SerializeObject(d, Formatting.Indented));
            Plugin.Log.LogInfo($"[STEMFishDLC] 已导出物品文本模板 {d.Count} 条 → {Path_}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 导出文本模板失败: {e.Message}");
        }
    }
}
