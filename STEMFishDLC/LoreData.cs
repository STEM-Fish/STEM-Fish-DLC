using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LoreData - 剧情档案（P3）
//  ---------------------------------------------------------------------------
//  文件布局（都在 plugins/STEMFishDLC/ 下，玩家可直接编辑）：
//    lore.zh-CN.json     档案正文（id / title / subtitle / body / sprite）
//    lore_unlocks.json   阅读记录（只存已读完的 id，自动生成）
//
//  反汇编确认的播放通道：
//    ScrollableText.CreateText(text, cover, sprites, font)  ← 游戏原生打字机（静态方法）
//    SurvivorNote 就是这么播的：SetTimeScale(SpeedType 5 = 0.16倍, false, false)
//      → 播完再 SetTimeScale(0 = 1.0 倍) 恢复
//    世界里的笔记物件 = Resources.Load("LoreNote")，点击由
//      PlayerCamera.TryPickupFromWorld → SendMessage("OnUse") 触发
//  → 我们克隆 LoreNote，把 SurvivorNote 换掉挂自己的 FishLoreNote，读写时机完全可控。
// ============================================================================

public static class LoreData
{
    public class Entry
    {
        public string id;
        public string kind;        // 扫描对象类型：纸片 / 档案 / 遗体 / 本机（列表里当标签）
        public string title;
        public string subtitle;    // 来源
        public string body;        // 扫描到的原文
        public string note;        // 主角的批注（永远和原文并列）
        public string noteLater;   // 「第 N 层补记」：只有下到 noteLayer 层才显示
        public int noteLayer;      // noteLater 解锁所需层数（biomeDepth >= 这个值才显示）
        public string sprite;
        public bool autoUnlock;    // true = 终端里本来就有（不消耗世界投放名额）
        public int layer;          // 该在哪一层解锁（0 = 不绑定，退回“下一篇未读”）
        public List<Variant> variants;   // 按「芯片状态」分支的正文/批注（没有就整篇同一个版本）
    }

    // 同一篇档案的不同版本。when 只认三个值：
    //   unchipped —— 芯片已拆
    //   declined  —— 有屏蔽器但选择了「留」
    //   none      —— 没拿到屏蔽器（或没走到抉择）
    public class Variant
    {
        public string when;
        public string body;
        public string note;
        public string noteLater;
    }

    private class FileRoot
    {
        public int version = 1;
        public List<Entry> logs = new List<Entry>();
    }

    private class SaveRoot
    {
        public List<string> read = new List<string>();
    }

    public static string Dir => Path.Combine(Paths.PluginPath, "STEMFishDLC");
    private static string LorePath => Lang.File("lore");

    // ★ 归档进度**绑定存档**：写到游戏存档旁边（save.sv.lore），而不是插件目录。
    //   游戏的存档只有单槽：Application.persistentDataPath + "\save.sv"（见 SaveSystem.HasSave）。
    //   所以同目录的 .lore 旁文件天然和那个存档同生共死：
    //     存档在 → 进度在；新开一局 → 清空；覆盖存档 → 跟着换。
    private static string SavePath => Path.Combine(Application.persistentDataPath, "save.sv.lore");
    private static string LegacySavePath => Path.Combine(Dir, "lore_unlocks.json");

    private static readonly List<Entry> _entries = new List<Entry>();
    private static readonly HashSet<string> _read = new HashSet<string>();
    private static bool _loaded;

    // 教程模式：所有档案默认可读，包括「第 N 层补记」
    public static bool TutorialMode { get; private set; }

    public static List<Entry> Entries { get { EnsureLoaded(); return _entries; } }
    public static int Total { get { EnsureLoaded(); return _entries.Count; } }
    public static int ReadCount { get { EnsureLoaded(); return TutorialMode ? _entries.Count : _read.Count; } }

    public static bool IsRead(string id)
    {
        EnsureLoaded();
        if (TutorialMode) return true;                 // 教程：全部可读
        return id != null && _read.Contains(id);
    }

    public static Entry Get(string id)
    {
        EnsureLoaded();
        return _entries.Find(e => e.id == id);
    }

    // 当前芯片状态：unchipped / declined / none
    //  - 游戏自己的「未植入芯片」预设也算 unchipped
    //  - Jammer 在弹出抉择面板时就把 declined 写上（真拆了会被 unchipped 盖掉）
    public static string ChipState()
    {
        try { if (WorldGeneration.unchipped) return "unchipped"; } catch { }
        try
        {
            var rs = WorldGeneration.runSettings;
            if (rs != null && rs.ContainsKey("stemfish_jammer_declined")) return "declined";
        }
        catch { }

        // 还揣着屏蔽器 = 至少还没拆（也覆盖"刚进第 10 层、面板还没弹出来"的那几秒）
        try
        {
            var b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            Item it = null;
            if (b != null && b.FindByIdThorough(Jammer.Id, out it) && it != null) return "declined";
        }
        catch { }
        return "none";
    }

    private static Variant PickVariant(Entry e)
    {
        if (e == null || e.variants == null || e.variants.Count == 0) return null;
        var st = ChipState();
        for (int i = 0; i < e.variants.Count; i++)
            if (e.variants[i] != null && e.variants[i].when == st) return e.variants[i];
        return null;
    }

    // 正文（有对应变体就用变体的）
    public static string BodyFor(Entry e)
    {
        var v = PickVariant(e);
        if (v != null && !string.IsNullOrEmpty(v.body)) return v.body;
        return e != null ? e.body : "";
    }

    // 当前层能看到的批注（后补的部分要下到对应层才出现；教程模式全给）
    public static string NoteFor(Entry e)
    {
        if (e == null) return "";
        var v = PickVariant(e);
        string baseNote = (v != null && !string.IsNullOrEmpty(v.note)) ? v.note : e.note;
        string lateNote = (v != null && !string.IsNullOrEmpty(v.noteLater)) ? v.noteLater : e.noteLater;

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(baseNote)) sb.Append(baseNote);
        if (!string.IsNullOrEmpty(lateNote))
        {
            bool show = TutorialMode;
            if (!show)
            {
                int depth = 0;
                try { if (WorldGeneration.world != null) depth = WorldGeneration.world.biomeDepth; } catch { }
                int need = e.noteLayer <= 0 ? 1 : e.noteLayer;
                show = depth >= need;
            }
            if (show)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(lateNote);
            }
        }
        return sb.ToString();
    }

    public static void MarkRead(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        EnsureLoaded();
        if (TutorialMode) return;                      // 教程里不写盘
        if (!_read.Add(id)) return;
        Save();
        Plugin.Log.LogInfo($"[STEMFishDLC] 档案已解锁: {id}（{_read.Count}/{_entries.Count}）");
    }

    // 下一个还没读的档案（世界生成器用它挑一篇投放；预置条目已在 _read 里，会自动跳过）
    public static Entry NextUnread()
    {
        EnsureLoaded();
        if (TutorialMode) return null;                  // 教程里没有"未读"
        foreach (var e in _entries)
            if (!e.autoUnlock && !_read.Contains(e.id)) return e;
        return null;
    }

    // 按层解锁：优先取「layer == 当前层」的未读条目；
    // 该层没配（老存档 / 改过 json）就退回“下一篇未读”，永远不会卡住进度。
    public static Entry UnlockForDepth(int depth)
    {
        EnsureLoaded();
        if (TutorialMode) return null;
        foreach (var e in _entries)
            if (!e.autoUnlock && !_read.Contains(e.id) && e.layer == depth) return e;
        return NextUnread();
    }

    // ==========================================================================
    //  加载 / 存档
    // ==========================================================================
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (File.Exists(LorePath))
            {
                var root = JsonConvert.DeserializeObject<FileRoot>(File.ReadAllText(LorePath));
                if (root != null && root.logs != null)
                {
                    _entries.Clear();
                    foreach (var e in root.logs)
                        if (e != null && !string.IsNullOrEmpty(e.id) &&
                            (!string.IsNullOrEmpty(e.body) || (e.variants != null && e.variants.Count > 0)))
                            _entries.Add(e);
                }
            }
            else Plugin.Log.LogWarning($"[STEMFishDLC] 找不到档案文件: {LorePath}");

            TryMigrateLegacySave();

            if (File.Exists(SavePath))
            {
                var s = JsonConvert.DeserializeObject<SaveRoot>(File.ReadAllText(SavePath));
                if (s != null && s.read != null)
                    foreach (var id in s.read) if (!string.IsNullOrEmpty(id)) _read.Add(id);
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 档案载入 {_entries.Count} 篇，已读 {_read.Count} 篇");

            // 终端里本来就有的条目（autoUnlock）：第一次启动就当作已归档
            // 这样玩家一进游戏打开终端，就知道自己手里是什么设备、为什么敢拆
            bool added = false;
            foreach (var e in _entries)
                if (e.autoUnlock && _read.Add(e.id)) added = true;
            if (added)
            {
                Save();
                Plugin.Log.LogInfo($"[STEMFishDLC] 预置条目已归档（共 {_read.Count} 篇在终端里）");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 档案载入失败: {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            var s = new SaveRoot { read = new List<string>(_read) };
            File.WriteAllText(SavePath, JsonConvert.SerializeObject(s, Formatting.Indented));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 档案存档失败: {e.Message}");
        }
    }

    private static void TryMigrateLegacySave()
    {
        try
        {
            if (File.Exists(SavePath) || !File.Exists(LegacySavePath)) return;
            File.Copy(LegacySavePath, SavePath, false);
            Plugin.Log.LogInfo("[STEMFishDLC] 归档进度已迁移到存档旁（save.sv.lore）");
        }
        catch { }
    }

    // ==========================================================================
    //  存档绑定（patch 游戏的 SaveSystem / PreRunScript）
    //  ---------------------------------------------------------------------------
    //  SaveGame()    → postfix 落盘（MarkRead 已实时写，这里兜底）
    //  TryLoadGame() → postfix 用旁文件覆盖内存里的已读集合
    //  StartRun()    → prefix 新开局重置（只留 autoUnlock）
    //  LoadRun()     → prefix 退出教程模式，之后由 TryLoadGame 恢复
    //  StartTutorial()→ prefix 进入教程模式（全部可读，含 noteLater，不写盘）
    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        try
        {
            var h = new Harmony("com.stemfish.dlc.lore");
            int n = 0;
            var flags = BindingFlags.NonPublic | BindingFlags.Static;

            var save = AccessTools.Method(typeof(SaveSystem), "SaveGame");
            if (save != null)
            {
                h.Patch(save, postfix: new HarmonyMethod(typeof(LoreData).GetMethod("SaveGame_Postfix", flags)));
                n++;
            }

            var load = AccessTools.Method(typeof(SaveSystem), "TryLoadGame");
            if (load != null)
            {
                h.Patch(load, postfix: new HarmonyMethod(typeof(LoreData).GetMethod("TryLoadGame_Postfix", flags)));
                n++;
            }

            var start = AccessTools.Method(typeof(PreRunScript), "StartRun");
            if (start != null)
            {
                h.Patch(start, prefix: new HarmonyMethod(typeof(LoreData).GetMethod("StartRun_Prefix", flags)));
                n++;
            }

            var lr = AccessTools.Method(typeof(PreRunScript), "LoadRun");
            if (lr != null)
            {
                h.Patch(lr, prefix: new HarmonyMethod(typeof(LoreData).GetMethod("LoadRun_Prefix", flags)));
                n++;
            }

            var tut = AccessTools.Method(typeof(PreRunScript), "StartTutorial");
            if (tut != null)
            {
                h.Patch(tut, prefix: new HarmonyMethod(typeof(LoreData).GetMethod("StartTutorial_Prefix", flags)));
                n++;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 归档存档绑定 hook 完成 | {n}/5 | 文件 {SavePath}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 归档存档绑定失败: {e.Message}");
        }
    }

    private static void SaveGame_Postfix()
    {
        try { EnsureLoaded(); Save(); } catch { }
    }

    private static void TryLoadGame_Postfix()
    {
        try
        {
            EnsureLoaded();
            _read.Clear();
            if (File.Exists(SavePath))
            {
                var s = JsonConvert.DeserializeObject<SaveRoot>(File.ReadAllText(SavePath));
                if (s != null && s.read != null)
                    foreach (var id in s.read) if (!string.IsNullOrEmpty(id)) _read.Add(id);
            }
            foreach (var e in _entries) if (e.autoUnlock) _read.Add(e.id);

            Plugin.Log.LogInfo($"[STEMFishDLC] 读档：归档进度已恢复（{_read.Count}/{_entries.Count}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读档恢复归档进度失败: {e.Message}");
        }
    }

    private static void StartRun_Prefix()
    {
        TutorialMode = false;
        try
        {
            EnsureLoaded();
            _read.Clear();
            foreach (var e in _entries) if (e.autoUnlock) _read.Add(e.id);

            try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch { }
            Save();

            Plugin.Log.LogInfo($"[STEMFishDLC] 新开局：归档进度已重置（{_read.Count}/{_entries.Count}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 重置归档进度失败: {e.Message}");
        }
    }

    private static void LoadRun_Prefix()
    {
        TutorialMode = false;
    }

    private static void StartTutorial_Prefix()
    {
        TutorialMode = true;
        try { EnsureLoaded(); } catch { }
        Plugin.Log.LogInfo($"[STEMFishDLC] 教程模式：{Total} 篇档案全部可读（含补记），不记录进度");
    }

    // ==========================================================================
    //  播放（游戏原生打字机 + 慢动作，读完自动恢复）
    // ==========================================================================
    private static FieldInfo _textObjField;
    private static MonoBehaviour _runner;

    public static void Show(Entry e)
    {
        if (e == null) return;
        try
        {
            MarkRead(e.id);

            var pc = PlayerCamera.main;
            if (pc != null && pc.body != null)
            {
                try { pc.SetTimeScale((PlayerCamera.SpeedType)5, false, false); } catch { }   // 5 = 0.16 倍速
            }

            // 注意：游戏原生的 ScrollableText 收了 sprites 也**不会画**——
            // 反汇编确认 sprites 字段只被赋值、全程序集没有任何读取点，
            // prefab 的 imgs 数组也没人填。所以配图真正的显示位置是
            // 终端「归档」页（DlcPanel.ShowCodexEntry 自己摆 Image）。
            var sprites = LoadSprites(e);

            ScrollableText.CreateText(BodyFor(e), false, sprites, DlcPanel.Font);

            // 扫完之后的一句——按扫描对象类型取，说的是"他刚看完这个"而不是"关于这个"
            SayAfterScan(e);

            // 用一个常驻的隐藏组件跑"等文本关掉再恢复时间"的协程
            if (_runner == null)
            {
                var go = new GameObject("STEMFishDLC_LoreRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<LoreRunner>();
            }
            _runner.StartCoroutine(RestoreWhenClosed());
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 播放档案失败: {ex.Message}");
        }
    }

    private static IEnumerator RestoreWhenClosed()
    {
        if (_textObjField == null)
            _textObjField = AccessTools.Field(typeof(ScrollableText), "textObj");

        // 等到打字机对象真的出现，再等它被关掉（避免刚创建就被判为已关闭）
        for (int i = 0; i < 120; i++)
        {
            if (TextObjAlive()) break;
            yield return null;
        }
        while (TextObjAlive()) yield return null;

        try
        {
            var pc = PlayerCamera.main;
            if (pc != null) pc.SetTimeScale((PlayerCamera.SpeedType)0, false, false);     // 0 = 1 倍速
        }
        catch { }
    }

    private static bool TextObjAlive()
    {
        try
        {
            if (_textObjField == null) return false;
            var o = _textObjField.GetValue(null) as UnityEngine.Object;
            return o != null;
        }
        catch { return false; }
    }

    // ==========================================================================
    //  扫完一句（不解释内容，只说"我看完了"）
    // ==========================================================================
    private static readonly string[] AfterPaper =
    {
        "扫进去了。名字不知道，字迹留着。",
        "这个人写完这段之后活了多久？",
        "纸是脆的，我小心一点。",
        "又一个。我记着。",
    };

    private static readonly string[] AfterFile =
    {
        "他们写字比我们喘气还轻松。",
        "条款写得很干净。干净得像没人死过。",
        "这些文件里，我们都是第三人称。",
        "存档。哪天我要是忘了，就回来看。",
    };

    private static readonly string[] AfterBody =
    {
        "对不住。我把你收下了。",
        "别趴着。我帮你翻过来。",
        "总得有人记得你死在哪一层。",
        "……我记住你的姿势了。",
    };

    private static readonly string[] AfterSelf =
    {
        "记一下。以后可能有用。",
        "往回翻，看自己写过什么。",
        "我还在。字是我写的。",
    };

    private static void SayAfterScan(Entry e)
    {
        try
        {
            string[] pool = AfterPaper;
            switch (e.kind)
            {
                case "档案": pool = AfterFile; break;
                case "遗体":
                case "尸体": pool = AfterBody; break;
                case "本机": pool = AfterSelf; break;
            }
            if (pool.Length == 0) return;
            FishVoice.SayLine(pool[UnityEngine.Random.Range(0, pool.Length)]);
        }
        catch { }
    }

    // 配图加载：先找插件目录的 lore_img/<名字>.png（玩家自己加的图），
    // 找不到再退回游戏内置资源（Resources.Load）。
    // 名字可以写多个，用英文逗号分隔，例如 "char_a, char_b"。
    // ★ 结果缓存：切来切去不该每次 new 一个 Texture2D（会漏显存）
    private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
    private static readonly HashSet<string> _spriteMiss = new HashSet<string>();

    public static List<Sprite> LoadSprites(Entry e)
    {
        var list = new List<Sprite>();
        if (e == null || string.IsNullOrEmpty(e.sprite)) return list;
        foreach (var raw in e.sprite.Split(','))
        {
            var one = raw.Trim();
            if (one.Length == 0) continue;
            var s = LoadLoreSprite(one);
            if (s != null) list.Add(s);
        }
        return list;
    }

    public static Sprite LoadLoreSprite(string name)
    {
        try
        {
            Sprite cached;
            if (_spriteCache.TryGetValue(name, out cached)) return cached;
            if (_spriteMiss.Contains(name)) return null;

            var got = LoadLoreSpriteRaw(name);
            if (got != null) _spriteCache[name] = got;
            else _spriteMiss.Add(name);
            return got;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 加载配图失败 {name}: {e.Message}");
            return null;
        }
    }

    private static Sprite LoadLoreSpriteRaw(string name)
    {
        var dir = Path.Combine(Dir, "lore_img");
        var file = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";
        var path = Path.Combine(dir, file);

        if (File.Exists(path))
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(File.ReadAllBytes(path)))
            {
                tex.filterMode = FilterMode.Point;          // 像素风：不要双线性糊
                tex.wrapMode = TextureWrapMode.Clamp;
                var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                       new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                Plugin.Log.LogInfo($"[STEMFishDLC] 配图已加载: {file}（{tex.width}×{tex.height}）");
                return sp;
            }
            UnityEngine.Object.Destroy(tex);
        }

        var builtin = Resources.Load<Sprite>(name);
        if (builtin == null)
            Plugin.Log.LogWarning($"[STEMFishDLC] 配图找不到: {path}（也没有同名的游戏内置资源）");
        return builtin;
    }

    // 空的常驻 runner（只用来跑协程）
    private class LoreRunner : MonoBehaviour { }
}
