using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  FishSkin - 内置皮肤替换（不再依赖 SkinSyncMod）
//  ---------------------------------------------------------------------------
//  为什么自己做：隔离模式会屏蔽其他 MOD，皮肤替换 MOD 一被屏蔽角色就变回原版实验体。
//  这里用同一份皮肤包（plugins/CustomSprites/<包名>/）自己做替换。
//
//  皮肤包结构（照 SkinSyncMod 的约定）：
//    baseSizes.json   { "Body/experimentUpTorso": [10,12], ... }  ← 该 sprite 的"基准像素尺寸"
//    Body/*.png       肢体贴图，文件名 = 游戏里 Sprite.name
//    Head/*.png       头 + 眼睛各状态，同上
//    Blood/*.png      血（粒子/肢体血，暂不处理）
//
//  PPU 算法（和 .txt 里记录的一致，已验证）：
//    .txt 里 m_PixelsToUnits = 8、m_Rect = 基准尺寸；
//    → ppu = 8 × (实际PNG宽 / 基准宽)
//    这样 2 倍放大的美术在世界里还是原来的大小。
//
//  应用方式：不用逐个组件替换（Limb/TailScript/FacialExpression 各一套逻辑，很脆），
//  而是**按 Sprite.name 全局替换**：遍历玩家身上所有 SpriteRenderer，
//  如果它当前 sprite 的名字在我们的字典里，就换掉。
//  眼睛状态、尾巴、肢体、毁容版全都是同一个机制 → 一份代码全覆盖。
// ============================================================================

public static class FishSkin
{
    public static bool Enabled = true;
    public static bool PreferBuiltIn;          // true = 即使装了别的皮肤 MOD 也用我们的
    public static string PackName = "EXP_STEMFISH_DLC";

    private const float BasePpu = 8f;          // 游戏里肢体/头贴图的统一 PPU
    private static readonly Dictionary<string, Sprite> _map = new Dictionary<string, Sprite>();
    private static readonly Dictionary<string, Sprite> _bySelf = new Dictionary<string, Sprite>();   // 防空转
    private static bool _loaded;
    private static int _tick;

    public static string CustomSpritesDir => Path.Combine(Paths.PluginPath, "CustomSprites");
    public static string PackDir => Path.Combine(CustomSpritesDir, PackName);

    public static int SpriteCount => _map.Count;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Skin", "Enabled", true,
                "用内置皮肤替换（不依赖 SkinSyncMod）。隔离模式下也能保持角色外观。").Value;
            PackName = plugin.Config.Bind("Skin", "PackName", "EXP_STEMFISH_DLC",
                "皮肤包文件夹名（plugins/CustomSprites/ 下）").Value;

            PreferBuiltIn = plugin.Config.Bind("Skin", "PreferBuiltIn", false,
                "即使检测到其他皮肤 MOD 也强制使用内置皮肤（默认让位给专业皮肤 MOD）").Value;

            if (!Enabled) { Plugin.Log.LogInfo("[STEMFishDLC] 内置皮肤替换：关闭"); return; }

            // 有专业皮肤 MOD 在跑时让它赢，避免我们把它选的皮肤又盖回去
            if (!PreferBuiltIn && HasExternalSkinMod())
            {
                Plugin.Log.LogInfo("[STEMFishDLC] 检测到 SkinSyncMod，内置皮肤让位（隔离模式下它会消失，届时自动接管）");
                _loaded = false;
                _deferred = true;
                return;
            }

            Load();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 皮肤模块初始化失败: {e}");
        }
    }

    // ==========================================================================
    //  载入皮肤包
    // ==========================================================================
    private static void Load()
    {
        _loaded = true;
        _map.Clear();

        if (!Directory.Exists(PackDir))
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 皮肤包不存在: {PackDir}");
            return;
        }

        // baseSizes.json：name → [w,h]
        var baseSizes = new Dictionary<string, Vector2Int>();
        try
        {
            var jsonPath = Path.Combine(PackDir, "baseSizes.json");
            if (File.Exists(jsonPath))
            {
                var jo = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int[]>>(
                    File.ReadAllText(jsonPath));
                if (jo != null)
                    foreach (var kv in jo)
                        if (kv.Value != null && kv.Value.Length >= 2)
                            baseSizes[kv.Key] = new Vector2Int(kv.Value[0], kv.Value[1]);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] baseSizes.json 解析失败: {e.Message}");
        }

        int ok = 0, fail = 0;
        foreach (var sub in new[] { "Body", "Head" })
        {
            var dir = Path.Combine(PackDir, sub);
            if (!Directory.Exists(dir)) continue;

            foreach (var png in Directory.GetFiles(dir, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(png);
                if (name.EndsWith("~", StringComparison.Ordinal)) continue;   // 编辑器备份文件
                try
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(File.ReadAllBytes(png)))
                    {
                        UnityEngine.Object.Destroy(tex);
                        fail++;
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.name = name;

                    // PPU：没有基准尺寸时按原尺寸 1:1 处理
                    // 世界尺寸 = baseSize / BasePpu，所以把 ppu 除以体型倍率就能放大外观
                    // （见 BodyProportion：只改显示大小，不动碰撞/物理）
                    float ppu = BasePpu;
                    if (baseSizes.TryGetValue(sub + "/" + name, out var bs) && bs.x > 0)
                        ppu = BasePpu * ((float)tex.width / bs.x);
                    ppu = BodyProportion.ApplyToPpu(ppu);

                    var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
                    sp.name = name;

                    _map[name] = sp;
                    _bySelf[name] = sp;
                    ok++;
                }
                catch (Exception e)
                {
                    fail++;
                    Plugin.Log.LogWarning($"[STEMFishDLC] 皮肤贴图载入失败 {name}: {e.Message}");
                }
            }
        }

        Plugin.Log.LogInfo($"[STEMFishDLC] 内置皮肤已载入 {ok} 张（失败 {fail}）来源 {PackName}");
    }

    // ==========================================================================
    //  应用：每帧（节流）扫玩家身上的 SpriteRenderer
    // ==========================================================================
    private static bool _deferred;

    private static bool HasExternalSkinMod()
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name;
                if (n == "SkinSyncMod") return true;
            }
        }
        catch { }
        return false;
    }

    public static void Tick()
    {
        // 启动时让位给别的皮肤 MOD，但它可能在隔离模式下根本没加载 → 这里再确认一次
        if (_deferred && !HasExternalSkinMod())
        {
            _deferred = false;
            Load();
            Plugin.Log.LogInfo("[STEMFishDLC] 未检测到其他皮肤 MOD，内置皮肤接管");
        }
        if (!Enabled || !_loaded || _map.Count == 0) return;

        _tick++;
        if (_tick % 2 != 0) return;

        try
        {
            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body == null) return;
            Apply(body.gameObject);
        }
        catch (Exception e)
        {
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                Plugin.Log.LogWarning($"[STEMFishDLC] 皮肤应用失败: {e.Message}");
            }
        }
    }
    private static string _lastError;

    public static int Apply(GameObject playerObj)
    {
        if (playerObj == null) return 0;
        int n = 0;

        // ---- ① 头部 / 眼睛：必须改**组件字段**，不能只改 renderer ----
        //  ---------------------------------------------------------------------
        //  FacialExpression.Update 每帧都会做「head.sprite = defaultHead(或 disfiguredHead[...])」
        //  这种赋值 —— 只改 SpriteRenderer.sprite 的话，下一帧就被它写回去了。
        //  这就是"身体换了、头还是原版"的直接原因（v0.11.7 的现象）。
        //  照搬 SkinSyncMod 的做法：改它手里那份 sprite 引用（字段），renderer 自然跟着变。
        //  注意 Eye 是 struct，List<Eye>[i] 拿到的是副本，改完要写回去。
        foreach (var face in playerObj.GetComponentsInChildren<FacialExpression>(true))
        {
            if (face == null) continue;
            try
            {
                if (Swap(ref face.defaultHead)) n++;
                if (Swap(ref face.defaultHeadMouth)) n++;
                if (Swap(ref face.defaultHeadMouthHalf)) n++;
                if (Swap(ref face.eyesGone)) n++;
                if (Swap(ref face.eyesGoneHealed)) n++;

                if (face.disfiguredHead != null)
                    for (int i = 0; i < face.disfiguredHead.Length; i++)
                        if (Swap(ref face.disfiguredHead[i])) n++;

                if (face.disfiguredHeadHeal != null)
                    for (int i = 0; i < face.disfiguredHeadHeal.Length; i++)
                        if (Swap(ref face.disfiguredHeadHeal[i])) n++;

                if (face.eyeList != null)
                {
                    for (int i = 0; i < face.eyeList.Count; i++)
                    {
                        var e = face.eyeList[i];          // struct 副本
                        bool changed = false;
                        if (Swap(ref e.front)) changed = true;
                        if (Swap(ref e.back)) changed = true;
                        if (changed) { face.eyeList[i] = e; n++; }
                    }
                }
            }
            catch (Exception ex) { LogOnce($"[STEMFishDLC] 头部皮肤替换失败: {ex.Message}"); }
        }

        // ---- ② 其余 renderer（肢体 / 尾巴 / 眼睛底图）：按 Sprite.name 覆盖 ----
        //  这些没有"每帧写回"的问题，直接换 renderer 的 sprite 就够。
        foreach (var sr in playerObj.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr == null) continue;
            var cur = sr.sprite;
            if (cur == null) continue;
            if (IsOurs(cur)) continue;                     // 已经是我们的

            if (_map.TryGetValue(cur.name, out var repl) && repl != null)
            {
                sr.sprite = repl;
                n++;
            }
            else
            {
                _unmatched.Add(cur.name);
            }
        }

        // ---- ③ 一次性诊断：把没匹配上的 sprite 名字打出来 ----
        //  皮肤包文件名必须和游戏里 Sprite.name 完全一致；对不上就会"某几个部位还是原版"。
        if (!_diagDone)
        {
            _diagDone = true;
            try
            {
                var miss = new List<string>();
                foreach (var m in _unmatched)
                    if (!_map.ContainsKey(m)) miss.Add(m);

                Plugin.Log.LogInfo($"[STEMFishDLC] 皮肤匹配自检: 包内 {_map.Count} 张，本次替换 {n} 处，" +
                                   $"未匹配 {miss.Count} 个");
                if (miss.Count > 0)
                {
                    miss.Sort();
                    var show = miss.Count > 40 ? miss.GetRange(0, 40) : miss;
                    Plugin.Log.LogInfo("[STEMFishDLC] 未匹配的 sprite 名（皮肤包里没有同名 png）: " +
                                       string.Join(", ", show.ToArray()) +
                                       (miss.Count > 40 ? $" …共 {miss.Count} 个" : ""));
                }
            }
            catch { }
        }

        return n;
    }

    // 诊断用：见过的、包里没有的 sprite 名
    private static readonly HashSet<string> _unmatched = new HashSet<string>();
    private static bool _diagDone;

    // 已经是我们的贴图？（sprite 名字相同但引用不同就是原版的）
    private static bool IsOurs(Sprite s)
        => s != null && _bySelf.TryGetValue(s.name, out var mine) && ReferenceEquals(mine, s);

    // 换一个 sprite 字段；返回是否真的换了
    private static bool Swap(ref Sprite slot)
    {
        if (slot == null) return false;
        if (IsOurs(slot)) return false;
        if (_map.TryGetValue(slot.name, out var repl) && repl != null)
        {
            slot = repl;
            return true;
        }
        return false;
    }

    private static void LogOnce(string msg)
    {
        if (_lastError == msg) return;
        _lastError = msg;
        Plugin.Log.LogWarning(msg);
    }

    // 供"进世界/切场景"时立刻应用一次
    public static void ForceApply()
    {
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body != null) Apply(body.gameObject);
        }
        catch { }
    }
}
