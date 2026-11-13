using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  SaveSlots - 多存档（命名存档）系统
//  ---------------------------------------------------------------------------
//  反汇编确认的事（正式版 1.0）：
//    · 游戏本体**只有单槽存档**：`Application.persistentDataPath + "\save.sv"`
//      （SaveSystem.SaveGame 里 File.WriteAllBytes，HasSave 就是 File.Exists）
//    · 写档入口只有两个：控制台 save 命令 → SaveSystem.SaveGame()；
//      层底的「保存并退出」→ WorldGeneration.SaveAndExit()（内部也是 SaveGame）
//    · 读档入口：PreRunScript.LoadRun() → WaitLoad() → SaveSystem.TryLoadGame()
//    · 档案进度（我们的 LoreData）写在 save.sv.lore，和 save.sv 同生共死
//
//  所以多存档只能"在外面包一层"：
//      save.sv / save.sv.lore   = 工作副本（游戏只认这两个）
//      我们的槽位文件           = <id>.sv / <id>.lore / <id>.json（名字、模式、层数、时长）
//
//  规则：
//    ① 任何一次游戏内存档（层底存档点 / 控制台 / 我们的存档键）→ 立刻同步进当前槽
//       （挂 SaveSystem.SaveGame 的 postfix，原版怎么写我们就怎么跟）
//    ② 层底存档点打开时自动存一次（[Save] AutoSaveAtCheckpoint）
//    ③ 存档键（默认 F4）→ 直接写当前槽；没有当前槽（急速下坠）才现场建一个
//    ④ 载入存档 = 把槽位文件复制回 save.sv(+.lore) → PreRunScript.LoadRun()
//
//  ★ 不改游戏存档格式，也不碰 TryLoadGame —— 这样原版有多完整，我们就有多完整
//    （身体/肢体/物品/组件/配方/心情/层数/稀有度全在 save.sv 里）。
// ============================================================================

public static class SaveSlots
{
    public class Slot
    {
        public string id;
        public string name;
        public string mode;
        public string modeLabel;
        public int depth;
        public float runTime;
        public string savedAt;
        public long bytes;
        public int itemCount;
        public bool hasData;        // false = 还没写过档（刚建好、没到存档点）
    }

    // 槽位元数据（<id>.json）。字段都是公开的，Newtonsoft 直接反序列化
    public class Meta
    {
        public int ver = 1;
        public string name;
        public string mode;
        public string modeLabel;
        public int depth;
        public float runTime;
        public string savedAt;
        public string mod;
    }

    // ---------------- 配置 ----------------
    public static bool Enabled = true;
    public static bool AutoSaveAtCheckpoint = true;
    public static KeyCode SaveKey = KeyCode.F4;

    // ---------------- 路径 ----------------
    public static string Root => Path.Combine(Application.persistentDataPath, "STEMFishDLC_Saves");
    private static string CurrentFile => Path.Combine(Root, "current.txt");
    private static string GameSave => Path.Combine(Application.persistentDataPath, "save.sv");
    private static string GameLore => Path.Combine(Application.persistentDataPath, "save.sv.lore");
    private static string DataPath(string id) => Path.Combine(Root, id + ".sv");
    private static string LorePath(string id) => Path.Combine(Root, id + ".lore");
    private static string MetaPath(string id) => Path.Combine(Root, id + ".json");

    // ---------------- 状态 ----------------
    private static string _current;
    private static bool _panelOpen;
    private static float _autoDelay = -1f;
    private static bool _busy;
    private static readonly HashSet<string> _loadLogged = new HashSet<string>();

    public static string CurrentId => _current;
    public static bool HasCurrent => !string.IsNullOrEmpty(_current);

    public static string CurrentName
    {
        get
        {
            if (!HasCurrent) return null;
            var m = ReadMeta(_current);
            return m != null && !string.IsNullOrEmpty(m.name) ? m.name : _current;
        }
    }

    // ======================================================================
    //  初始化
    // ======================================================================
    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Save", "Enabled", true,
                "启用命名多存档（关掉 = 回到原版单存档 save.sv）").Value;
            AutoSaveAtCheckpoint = plugin.Config.Bind("Save", "AutoSaveAtCheckpoint", true,
                "到达每层底部的存档点时自动写入当前存档").Value;

            string key = plugin.Config.Bind("Save", "SaveKey", "F4",
                "局内存档键（Unity KeyCode 名，比如 F4 / F2 / Home）").Value;
            try { SaveKey = (KeyCode)Enum.Parse(typeof(KeyCode), key, true); }
            catch { SaveKey = KeyCode.F4; }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 存档配置读取失败: {e.Message}");
        }

        try { Directory.CreateDirectory(Root); } catch { }
        LoadCurrent();
        TryImportLegacy();
        PatchSaveGame();
        PatchResourcesLoad();

        Plugin.Log.LogInfo($"[STEMFishDLC] 多存档已启用：{Root}");
        Plugin.Log.LogInfo($"[STEMFishDLC] 当前存档：{(HasCurrent ? $"「{CurrentName}」({_current})" : "(未选，存档键会现场新建)")} | 存档键={SaveKey} | 存档点自动存档={(AutoSaveAtCheckpoint ? "开" : "关")}");
    }

    // 第一次装上多存档时，把已有的单槽 save.sv 收进一个"旧存档"槽位，
    // 免得作者以前的进度在列表里凭空消失（原版 continue 按钮也还在，双保险）。
    private static void TryImportLegacy()
    {
        try
        {
            string marker = Path.Combine(Root, "legacy_imported.txt");
            if (File.Exists(marker)) return;
            if (!File.Exists(GameSave)) { File.WriteAllText(marker, "none"); return; }
            if (Directory.GetFiles(Root, "*.json").Length > 0) { File.WriteAllText(marker, "skip"); return; }

            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var t = File.GetLastWriteTime(GameSave);
            var m = new Meta
            {
                name = "旧存档 · " + t.ToString("MM-dd HH:mm"),
                mode = "",
                modeLabel = "未知模式",
                savedAt = t.ToString("yyyy-MM-dd HH:mm:ss"),
                mod = ModState.Version,
            };
            File.Copy(GameSave, DataPath(id), true);
            if (File.Exists(GameLore)) File.Copy(GameLore, LorePath(id), true);
            WriteMeta(id, m);
            SetCurrent(id);
            File.WriteAllText(marker, "ok");
            Plugin.Log.LogInfo($"[STEMFishDLC] 已把原版 save.sv 导入为存档：「{m.name}」");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 导入旧存档失败: {e.Message}");
        }
    }

    private static void PatchSaveGame()
    {
        try
        {
            var m = AccessTools.Method(typeof(SaveSystem), "SaveGame", Type.EmptyTypes);
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 SaveSystem.SaveGame，存档后不会同步进槽位"); return; }

            var post = typeof(SaveSlots).GetMethod("SaveGame_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            // Priority.Last：LoreData 也挂了 SaveGame 的 postfix（写 save.sv.lore）。
            // 我们要在它写完之后再复制，否则槽位里的档案进度会慢一局。
            var hm = new HarmonyMethod(post) { priority = Priority.Last };
            new Harmony("com.stemfish.dlc.saveslots").Patch(m, postfix: hm);
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 存档同步 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 存档同步 hook 失败: {e.Message}");
        }
    }

    // 原版写完 save.sv 之后，立刻抄一份进当前槽位
    private static void SaveGame_Postfix()
    {
        try
        {
            if (!Enabled || !HasCurrent) return;
            if (SyncFromGameSave()) return;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 存档后同步失败: {e.Message}");
        }
    }

    // ======================================================================
    //  自定义物品读档：原版 TryLoadGame 走 Resources.Load(id) + Instantiate
    //  ---------------------------------------------------------------------
    //  我们的物品没有 Resources 资源，读档时 Resources.Load 返回 null →
    //  Instantiate(null) 报错、物品丢失（开局发的三件衣服就没了）。
    //  这里用 Create() 先造一个"内存模板"（id/贴图/穿戴配置全都已应用），
    //  放到世界外很远的地方，让原版直接 Instantiate 它 —— 克隆出来的实例
    //  Awake/Start 正常跑，id 也是我们自己的。
    // ======================================================================
    private static void PatchResourcesLoad()
    {
        try
        {
            // ★ 不能写 AccessTools.Method(typeof(Resources), "Load", new[]{typeof(string)})：
            //   Resources 里还有泛型 Load<T>(string)，参数表一模一样，AccessTools 会抛
            //   "Ambiguous match found."（实测）。这里手动挑非泛型的那一个。
            MethodInfo m = null;
            foreach (var mi in typeof(Resources).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (mi.Name != "Load" || mi.IsGenericMethodDefinition || mi.ContainsGenericParameters) continue;
                var ps = mi.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { m = mi; break; }
            }
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Resources.Load(string)"); return; }
            var pre = typeof(SaveSlots).GetMethod("ResourcesLoad_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.loaditems").Patch(m, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 自定义物品读档 hook 完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 自定义物品读档 hook 失败: {e.Message}");
        }
    }

    private static bool ResourcesLoad_Prefix(string path, ref UnityEngine.Object __result)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !ItemRegistry.IsCustom(path)) return true;
            var go = ItemRegistry.GetLoadPrefab(path);
            if (go == null) return true;
            __result = go;
            if (_loadLogged.Add(path))
                Plugin.Log.LogInfo($"[STEMFishDLC] 读档还原自定义物品：{path}");
            return false;
        }
        catch { }
        return true;
    }

    // ======================================================================
    //  槽位读写
    // ======================================================================
    private static void LoadCurrent()
    {
        try
        {
            if (!File.Exists(CurrentFile)) { _current = null; return; }
            var id = File.ReadAllText(CurrentFile).Trim();
            _current = (!string.IsNullOrEmpty(id) && File.Exists(MetaPath(id))) ? id : null;
        }
        catch { _current = null; }
    }

    public static void SetCurrent(string id)
    {
        _current = string.IsNullOrEmpty(id) ? null : id;
        try
        {
            Directory.CreateDirectory(Root);
            if (_current == null)
            {
                if (File.Exists(CurrentFile)) File.Delete(CurrentFile);
            }
            else File.WriteAllText(CurrentFile, _current);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 写当前存档标记失败: {e.Message}");
        }
    }

    private static Meta ReadMeta(string id)
    {
        try
        {
            if (!File.Exists(MetaPath(id))) return null;
            return JsonConvert.DeserializeObject<Meta>(File.ReadAllText(MetaPath(id)));
        }
        catch { return null; }
    }

    private static void WriteMeta(string id, Meta m)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(MetaPath(id), JsonConvert.SerializeObject(m, Formatting.Indented));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 写存档信息失败({id}): {e.Message}");
        }
    }

    public static List<Slot> All()
    {
        var list = new List<Slot>();
        try
        {
            if (!Directory.Exists(Root)) return list;
            foreach (var f in Directory.GetFiles(Root, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                var m = ReadMeta(id);
                if (m == null) continue;

                var s = new Slot
                {
                    id = id,
                    name = string.IsNullOrEmpty(m.name) ? id : m.name,
                    mode = m.mode,
                    modeLabel = m.modeLabel,
                    depth = m.depth,
                    runTime = m.runTime,
                    savedAt = m.savedAt,
                };
                s.hasData = File.Exists(DataPath(id));
                if (s.hasData)
                {
                    try { s.bytes = new FileInfo(DataPath(id)).Length; } catch { }
                    FillFromSaveFile(s);       // 直接读存档内容，列表里的层数/时长/物品数才是真的
                }
                list.Add(s);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读取存档列表失败: {e.Message}");
        }

        list.Sort((a, b) => string.Compare(b.savedAt ?? "", a.savedAt ?? "", StringComparison.Ordinal));
        return list;
    }

    // ======================================================================
    //  从存档本体里读摘要（楼层 / 时长 / 物品件数）
    //  ----------------------------------------------------------------------
    //  存档格式：GZip(JSON)，和 SaveSystem.Zip 一致。文件只有几 KB，列表里直接读没问题。
    //  这样连"旧存档"（没有 meta）也能显示真实进度，而不是 00:00 / 第 0 层。
    // ======================================================================
    private static void FillFromSaveFile(Slot s)
    {
        try
        {
            using (var fs = File.OpenRead(DataPath(s.id)))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, Encoding.ASCII))
            {
                var jo = JObject.Parse(sr.ReadToEnd());
                // 存档里的 biome = biomeDepth + 1（SaveGame 就是这么写的），这里换回 0 基，
                // 和 meta.depth 保持一致，显示时再 +1
                var d = jo["biome"]; if (d != null) s.depth = Math.Max(0, d.Value<int>() - 1);
                var t = jo["runTime"]; if (t != null) s.runTime = t.Value<float>();
                var items = jo["items"] as JArray;
                s.itemCount = items != null ? items.Count : 0;
            }

            // ★ .map 头里记的层号最准（原版在最后一层会把 biome 夹成 1，列表会显示成第 1 层）
            bool ok;
            int mapDepth = MapSave.ReadDepth(s.id, out ok);
            if (ok && mapDepth >= 0) s.depth = mapDepth;
            // 旧存档（没有 .map，meta 也没写过层号）就只能用 biome-1
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读存档摘要失败({s.id}): {e.Message}");
        }
    }

    private static string DefaultName(RunConfig.Difficulty d)
    {
        string label = d != null ? d.label : "新的一局";
        return $"{label} · {DateTime.Now:MM-dd HH:mm}";
    }

    // 建一个空槽（还没写数据），并设为当前
    public static string CreateSlot(string name, RunConfig.Difficulty d)
    {
        try
        {
            Directory.CreateDirectory(Root);

            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            int n = 1;
            while (File.Exists(MetaPath(id)) || File.Exists(DataPath(id)))
                id = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + (n++);

            var m = new Meta
            {
                name = string.IsNullOrWhiteSpace(name) ? DefaultName(d) : name.Trim(),
                mode = d != null ? d.code : (RunConfig.Current != null ? RunConfig.Current.code : ""),
                modeLabel = d != null ? d.label : (RunConfig.Current != null ? RunConfig.Current.label : ""),
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                mod = ModState.Version,
            };
            WriteMeta(id, m);
            SetCurrent(id);
            Plugin.Log.LogInfo($"[STEMFishDLC] 新建存档：「{m.name}」 mode={m.mode} id={id}");
            return id;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 新建存档失败: {e.Message}");
            return null;
        }
    }

    public static bool Rename(string id, string newName)
    {
        try
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(newName)) return false;
            var m = ReadMeta(id);
            if (m == null) return false;
            m.name = newName.Trim();
            WriteMeta(id, m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 存档重命名：{id} → {m.name}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 存档重命名失败: {e.Message}");
            return false;
        }
    }

    public static bool Delete(string id)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (File.Exists(DataPath(id))) File.Delete(DataPath(id));
            if (File.Exists(LorePath(id))) File.Delete(LorePath(id));
            if (File.Exists(MetaPath(id))) File.Delete(MetaPath(id));
            if (File.Exists(MapSave.PathFor(id))) File.Delete(MapSave.PathFor(id));
            if (_current == id) SetCurrent(null);
            Plugin.Log.LogInfo($"[STEMFishDLC] 存档已删除：{id}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 删除存档失败: {e.Message}");
            return false;
        }
    }

    // ======================================================================
    //  存档 / 同步
    // ======================================================================
    public static bool SyncFromGameSave()
    {
        if (!HasCurrent) return false;
        if (!File.Exists(GameSave)) return false;

        try
        {
            Directory.CreateDirectory(Root);
            File.Copy(GameSave, DataPath(_current), true);

            // 档案进度（save.sv.lore）属于这一局，一起收进槽位
            if (File.Exists(GameLore)) File.Copy(GameLore, LorePath(_current), true);
            else if (File.Exists(LorePath(_current))) File.Delete(LorePath(_current));

            var m = ReadMeta(_current) ?? new Meta { name = _current };
            if (string.IsNullOrEmpty(m.name)) m.name = _current;
            if (RunConfig.Current != null)
            {
                m.mode = RunConfig.Current.code;
                m.modeLabel = RunConfig.Current.label;
            }
            try { var w = WorldGeneration.world; if (w != null) m.depth = w.biomeDepth; } catch { }
            // 游戏自己的算法：SaveInfo.runTime = SaveSystem.savedRunTime + WorldGeneration.world.realTimeElapsed
            // （savedRunTime 只在读档时写，所以不能单独用它 —— 之前列表里永远是 00:00 就是这原因）
            try
            {
                float t = SaveSystem.savedRunTime;
                var w = WorldGeneration.world;
                if (w != null) t += w.realTimeElapsed;
                if (t > 0f) m.runTime = t;
            }
            catch { }
            m.savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            m.mod = ModState.Version;
            WriteMeta(_current, m);

            // 地图 + 角色坐标（原版 save.sv 里没有，单独存 <id>.map）
            MapSave.Save(_current);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 写入槽位失败: {e.Message}");
            return false;
        }
    }

    // 直接对当前存档执行保存
    public static bool SaveCurrent(bool auto)
    {
        if (!Enabled) return false;
        if (_busy) return false;

        if (!InWorld(out var w))
        {
            if (!auto) Toast("不在局内，没有可保存的进度");
            return false;
        }

        // 没有当前槽：自动存档不建（急速下坠），手动存档现场建一个
        if (!HasCurrent)
        {
            if (auto) return false;
            string id = CreateSlot(DefaultName(RunConfig.Current), RunConfig.Current);
            if (id == null) { Toast("新建存档失败，看日志"); return false; }
            Toast($"这个模式没有存档，已新建「{CurrentName}」");
        }

        _busy = true;
        try
        {
            // 存档点打开时原版会把 body.forceWalk 置真（不让玩家走开）。写档前先压回去，
            // 免得读档后人物一直"被强制走路"；写完再恢复，画面行为不变。
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            bool forceWalk = false;
            try { if (body != null) { forceWalk = body.forceWalk; body.forceWalk = false; } } catch { }

            try { SaveSystem.SaveGame(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] SaveSystem.SaveGame 失败: {e}");
                Toast("存档失败：" + e.Message);
                return false;
            }
            finally
            {
                try { if (body != null) body.forceWalk = forceWalk; } catch { }
            }

            if (!SyncFromGameSave())
            {
                Toast("存档写入了，但复制到槽位失败（看日志）");
                return false;
            }

            string name = CurrentName;
            Plugin.Log.LogInfo($"[STEMFishDLC] {(auto ? "自动" : "手动")}存档完成：「{name}」层 {MetaDepth()} 时长 {TimeText(RunTimeOfCurrent())}");
            // 存档只给一条底部提示，不说台词（v0.18.8 按作者要求去掉）
            Toast((auto ? "已自动保存：" : "已保存：") + name);
            return true;
        }
        finally { _busy = false; }
    }

    private static int MetaDepth()
    {
        try { return WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0; } catch { return 0; }
    }

    private static float RunTimeOfCurrent()
    {
        try { return SaveSystem.savedRunTime; } catch { return 0f; }
    }

    private static bool InWorld(out WorldGeneration w)
    {
        w = null;
        try { w = WorldGeneration.world; } catch { return false; }
        if (w == null) return false;
        try { if (!w.worldExists) return false; } catch { return false; }

        var pc = PlayerCamera.main;
        if (pc == null || pc.body == null) return false;
        return true;
    }

    // ======================================================================
    //  开局 / 载入
    // ======================================================================
    // 新的一局：先建槽（命名），再应用模式、开局
    public static bool CreateAndStart(RunConfig.Difficulty d, string name)
    {
        MapSave.Cancel();
        string id = CreateSlot(name, d);
        if (id == null) return false;

        if (!RunConfig.Apply(d))
        {
            Delete(id);
            return false;
        }
        RunConfig.BeginRun();
        return true;
    }

    // 急速下坠：不建槽，而且把当前槽清掉，免得存档键覆盖掉别的存档
    public static bool StartNoSlot(RunConfig.Difficulty d)
    {
        SetCurrent(null);
        MapSave.Cancel();
        if (!RunConfig.Apply(d)) return false;
        RunConfig.BeginRun();
        return true;
    }

    public static bool Load(string id)
    {
        var m = ReadMeta(id);
        if (m == null)
        {
            Toast("存档信息读不出来");
            return false;
        }

        SetCurrent(id);

        // 模式：存档里记了什么就恢复什么（我们的模式效果是运行时的，不进 save.sv）
        if (!ApplyMode(m.mode))
            Plugin.Log.LogWarning($"[STEMFishDLC] 存档「{m.name}」没有可用模式记录（{m.mode}），按当前模式继续");

        // 还没写过数据 → 用同一个槽、同一个模式重新开一局
        if (!File.Exists(DataPath(id)))
        {
            Plugin.Log.LogInfo($"[STEMFishDLC] 存档「{m.name}」还没有进度，直接开新局");
            if (!RunConfig.Enabled) return false;
            RunConfig.BeginRun();
            return true;
        }

        try
        {
            File.Copy(DataPath(id), GameSave, true);
            if (File.Exists(LorePath(id))) File.Copy(LorePath(id), GameLore, true);
            else if (File.Exists(GameLore)) File.Delete(GameLore);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 还原存档文件失败: {e.Message}");
            Toast("还原存档失败：" + e.Message);
            return false;
        }

        // 原版 LoadRun 在没做过基础课程时会弹 tutorialWarning 并中止（和 StartRun 同款）
        try
        {
            if (PlayerPrefs.GetInt("didbasiccourse", 0) == 0)
                PlayerPrefs.SetInt("didbasiccourse", 1);
        }
        catch { }

        var pre = PreRunScript.instance;
        if (pre == null)
        {
            Toast("主菜单脚本不在，无法载入");
            return false;
        }

        try
        {
            Plugin.Log.LogInfo($"[STEMFishDLC] 载入存档「{m.name}」" +
                               $"（{m.modeLabel} 第 {m.depth} 层 · {TimeText(m.runTime)}）");

            // 地图存档先读进内存，等新世界生成完由 MapSave.Tick 写回
            MapSave.Prepare(id);

            pre.LoadRun();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] LoadRun 失败: {e.Message}");
            Toast("载入失败：" + e.Message);
            return false;
        }
    }

    private static bool ApplyMode(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        var d = Array.Find(RunConfig.All, x => x.code == code);
        if (d == null) return false;
        return RunConfig.Apply(d);
    }

    // ======================================================================
    //  每帧：存档键 + 存档点自动存档 + 提示条
    // ======================================================================
    public static void Tick()
    {
        TickToast();
        if (!Enabled) return;

        // ---- 存档键 ----
        if (Input.GetKeyDown(SaveKey) && !IsTyping())
            SaveCurrent(false);

        // ---- 层底存档点：面板刚打开的那一帧之后存一次 ----
        if (!AutoSaveAtCheckpoint) return;

        bool open = false;
        try
        {
            var w = WorldGeneration.world;
            open = w != null && w.savePanel != null && w.savePanel.activeInHierarchy;
        }
        catch { }

        if (!open)
        {
            _panelOpen = false;
            _autoDelay = -1f;
            return;
        }

        if (!_panelOpen)
        {
            _panelOpen = true;
            _autoDelay = 0.8f;      // 等面板动画站稳、给玩家看清
        }
        else if (_autoDelay > 0f)
        {
            _autoDelay -= Time.unscaledDeltaTime;
            if (_autoDelay <= 0f)
            {
                _autoDelay = -1f;
                SaveCurrent(true);
            }
        }
    }

    private static bool IsTyping()
    {
        try
        {
            var es = EventSystem.current;
            if (es == null || es.currentSelectedGameObject == null) return false;
            return es.currentSelectedGameObject.GetComponent<TMP_InputField>() != null;
        }
        catch { return false; }
    }

    public static string TimeText(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string SlotInfo(Slot s)
    {
        if (s == null) return "";
        string mode = string.IsNullOrEmpty(s.modeLabel) ? (s.mode ?? "?") : s.modeLabel;
        string when = string.IsNullOrEmpty(s.savedAt) ? "未保存" : s.savedAt;
        string data = s.hasData
            ? $"{TimeText(s.runTime)} · 第 {s.depth + 1} 层 · {s.itemCount} 件物品"
            : "还没写过档";
        return $"{mode} · {data} · {when}";
    }

    // ======================================================================
    //  屏幕提示（存档反馈）
    // ======================================================================
    private static GameObject _toastRoot;
    private static TextMeshProUGUI _toastText;
    private static float _toastLife;

    public static void Toast(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return;
        try
        {
            EnsureToast();
            if (_toastText == null) return;
            _toastText.text = msg;
            _toastText.color = new Color(0.87f, 0.92f, 0.94f, 1f);
            _toastLife = 2.6f;
            _toastRoot.SetActive(true);
        }
        catch { }
    }

    private static void TickToast()
    {
        if (_toastLife <= 0f) return;
        _toastLife -= Time.unscaledDeltaTime;
        if (_toastText != null)
        {
            var c = _toastText.color;
            c.a = Mathf.Clamp01(_toastLife / 0.9f);
            _toastText.color = c;
        }
        if (_toastLife <= 0f && _toastRoot != null) _toastRoot.SetActive(false);
    }

    private static void EnsureToast()
    {
        if (_toastRoot != null) return;

        _toastRoot = new GameObject("STEMFishDLC_SaveToast");
        UnityEngine.Object.DontDestroyOnLoad(_toastRoot);

        var canvas = _toastRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 29500;

        var go = new GameObject("Text", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_toastRoot.transform, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 92f);
        rt.sizeDelta = new Vector2(900f, 30f);

        _toastText = go.AddComponent<TextMeshProUGUI>();
        try { if (DlcPanel.Font != null) _toastText.font = DlcPanel.Font; } catch { }
        _toastText.fontSize = 20;
        _toastText.alignment = TextAlignmentOptions.Center;
        _toastText.enableWordWrapping = false;
        _toastText.raycastTarget = false;
        _toastText.text = "";

        _toastRoot.SetActive(false);
    }
}
