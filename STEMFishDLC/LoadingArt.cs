using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  LoadingArt - 替换主菜单 / 世界生成界面里的角色立绘
//  ---------------------------------------------------------------------------
//  这些角色不是世界里的 SpriteRenderer，而是 **UGUI Image**：
//    level0（主菜单）  CharSelect / Char1-3 / Experiment / ExperimentShine / Podium
//    level1（生成界面）GenCharacter（左下角坐着那只）/ GenPod / GenBackground / GenOver / GenSit
//
//  ★ v0.17.0 换了实现方式，v0.16.x 的两个毛病：
//    ① **尺寸错**：v0.16 直接拿 PNG 建 sprite（ppu 写死 100），
//       而原版 sprite 的 rect 是 1920×1080 / 320×180 这种大画布、真正的像素只有一小块
//       （back15：rect 1920×1080，贴图只有 609×459 且带偏移）。结果我们那张被当成
//       整张画布铺上去 → 角色被放大 3 倍、位置也偏了。
//       → 现在**照抄被替换 sprite 的 rect / pixelsPerUnit / pivot / border** 建新 sprite，
//         于是无论 PNG 画多大，屏幕上的尺寸和位置都和原版一模一样。
//    ② **闪烁**：v0.16 每 2 帧抢着改一次，而原版是逐帧动画，两边来回抢 → 一帧原版一帧我们的。
//       → 现在直接挂 **`Image.sprite` 的 setter 前缀**：谁把 sprite 赋进来都当场换成我们的，
//         从根上不打架（和 KrokMP 改 `TMP_Text.text` 是同一个套路，已验证可行）。
//
//  PNG 文件名 = 场景对象名 或 sprite 名（两种都认），放在
//    plugins/CustomSprites/<包名>/Loading/
//  导出的原图已经**按原版画布尺寸**铺好了（角色在画布里的位置 = 游戏里的位置），
//  照着画就能 1:1 对上。
// ============================================================================
public static class LoadingArt
{
    public static bool Enabled = true;

    private static readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
    private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
    private static readonly List<Image> _targets = new List<Image>();
    private static readonly HashSet<string> _logged = new HashSet<string>();
    private static readonly HashSet<int> _ours = new HashSet<int>();   // 我们自己造的 sprite，别二次处理
    private static readonly HashSet<string> _logged2 = new HashSet<string>();  // 替换日志去重
    private static readonly Dictionary<string, string> _alias = new Dictionary<string, string>();  // 对象名 → 已用过的 key
    private static readonly HashSet<string> _missLogged = new HashSet<string>();

    private static bool _loaded;
    private static bool _patched;
    private static int _frames;
    private static float _scanTimer;
    private static string _lastError;

    public static string Dir => Path.Combine(FishSkin.PackDir, "Loading");
    public static int SpriteCount => _tex.Count;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Skin", "LoadingArt", true,
                "替换主菜单/世界生成界面的角色立绘（PNG 放皮肤包的 Loading/ 下，文件名=对象名或 sprite 名）").Value;
        }
        catch { }

        if (!Enabled) { Plugin.Log.LogInfo("[STEMFishDLC] 加载界面立绘替换：关闭"); return; }

        Load();
        if (_tex.Count > 0) PatchImageSetter();
    }

    // ---------------------------------------------------------------------
    //  读 PNG（只读成 Texture2D，真正建 sprite 时再按目标 sprite 的尺寸算 PPU）
    // ---------------------------------------------------------------------
    private static void Load()
    {
        _loaded = true;
        _tex.Clear();
        _spriteCache.Clear();

        try
        {
            if (!Directory.Exists(Dir))
            {
                Plugin.Log.LogInfo($"[STEMFishDLC] 加载界面立绘：{Dir} 不存在（不替换）");
                return;
            }

            foreach (var png in Directory.GetFiles(Dir, "*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(png);
                if (name.EndsWith("~", StringComparison.Ordinal)) continue;

                try
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(File.ReadAllBytes(png)))
                    {
                        UnityEngine.Object.Destroy(tex);
                        continue;
                    }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.name = name;
                    _tex[name] = tex;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[STEMFishDLC] 立绘载入失败 {name}: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 立绘目录读取失败: {e.Message}");
        }

        if (_tex.Count > 0)
            Plugin.Log.LogInfo($"[STEMFishDLC] 加载界面立绘已载入 {_tex.Count} 张：{string.Join(", ", _tex.Keys)}");
    }

    // ---------------------------------------------------------------------
    //  核心：挂 Image.sprite 的 setter 前缀，把赋值当场换成我们的
    // ---------------------------------------------------------------------
    private static void PatchImageSetter()
    {
        if (_patched) return;
        try
        {
            var setter = AccessTools.PropertySetter(typeof(Image), "sprite");
            if (setter == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 Image.sprite 的 setter，立绘替换只靠每帧轮询");
                return;
            }
            var pre = typeof(LoadingArt).GetMethod("Sprite_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.loadingart").Patch(setter, prefix: new HarmonyMethod(pre));

            var info = Harmony.GetPatchInfo(setter);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] Image.sprite 钩子完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] Image.sprite 钩子失败: {e.Message}");
        }
    }

    private static void Sprite_Prefix(Image __instance, ref Sprite value)
    {
        if (!Enabled || value == null || __instance == null) return;
        if (_ours.Contains(value.GetInstanceID())) return;      // 已经是我们的图，别再套一层
        try
        {
            string key = null;
            var go = __instance.gameObject;

            if (_tex.ContainsKey(value.name)) key = value.name;          // ① sprite 名优先：眨眼帧各走各的
            else if (go != null && _tex.ContainsKey(go.name)) key = go.name;   // ② 对象名
            // ③ 同一套图的另一种命名：开场下坠时 GenCharacter 用的是 genexpnew，
            //    我们只画了 genexpnewalt（两张原图逐像素一样，都是 320×180 / 角色同一位置）。
            //    所以 "名字 + alt" 兜底，省的每张图都要维护两份。
            else if (_tex.ContainsKey(value.name + "alt")) key = value.name + "alt";
            else if (go != null) _alias.TryGetValue(go.name, out key);         // ④ 之前学到的别名

            if (key == null)
            {
                // 没命中：把名字打出来（同一个组合只报一次），方便补图
                string tag = (go != null ? go.name : "?") + "|" + value.name;
                if (_missLogged.Add(tag))
                    Plugin.Log.LogInfo($"[STEMFishDLC] 立绘未匹配：对象 '{go?.name}' sprite='{value.name}'");
                return;
            }

            // 同一个对象以后换成别的 sprite 名也能沿用这张图（舱体落地那一下就是这么漏的）
            if (go != null && key != go.name)
            {
                string old;
                if (!_alias.TryGetValue(go.name, out old) || old != key)
                    _alias[go.name] = key;
            }

            var sp = GetSprite(key, value);
            if (sp != null)
            {
                string tag = (go != null ? go.name : "?") + "→" + key;
                if (_logged2.Add(tag))
                    Plugin.Log.LogInfo($"[STEMFishDLC] 立绘替换生效：{tag}");
                value = sp;
            }
        }
        catch { }
    }

    // 用目标 sprite 的几何（rect / ppu / pivot / border）建我们的 sprite
    private static Sprite GetSprite(string key, Sprite template)
    {
        Texture2D tex;
        if (!_tex.TryGetValue(key, out tex) || tex == null) return null;

        float rw = template != null ? template.rect.width : tex.width;
        float rh = template != null ? template.rect.height : tex.height;
        float inPpu = (template != null && template.pixelsPerUnit > 0.01f) ? template.pixelsPerUnit : 100f;
        if (rw < 0.01f || rh < 0.01f) { rw = tex.width; rh = tex.height; inPpu = 100f; }

        string ck = $"{key}|{rw:0.##}|{rh:0.##}|{inPpu:0.##}";
        Sprite cached;
        if (_spriteCache.TryGetValue(ck, out cached) && cached != null) return cached;

        // 让 PNG 铺满与原版相同的世界/UI 尺寸
        float unitsW = rw / inPpu;
        float ppu = Mathf.Max(0.01f, tex.width / Mathf.Max(0.0001f, unitsW));

        var pivot = template != null
            ? new Vector2(template.pivot.x / rw, template.pivot.y / rh)
            : new Vector2(0.5f, 0.5f);
        var border = template != null ? template.border : Vector4.zero;

        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, ppu,
                               0, SpriteMeshType.FullRect, border);
        sp.name = key;
        _spriteCache[ck] = sp;
        try { _ours.Add(sp.GetInstanceID()); } catch { }
        return sp;
    }

    // ---------------------------------------------------------------------
    //  兜底：钩子挂上之前就已经赋好的 Image
    // ---------------------------------------------------------------------
    public static void Tick()
    {
        if (!Enabled || !_loaded || _tex.Count == 0) return;

        _frames++;
        if (_frames % 4 != 0) return;

        try
        {
            _scanTimer -= Time.unscaledDeltaTime;
            if (_scanTimer <= 0f || _targets.Count == 0)
            {
                _scanTimer = 2f;
                Rescan();
            }

            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var img = _targets[i];
                if (img == null) { _targets.RemoveAt(i); continue; }
                if (!img.gameObject.activeInHierarchy) continue;

                var cur = img.sprite;
                if (cur == null) continue;
                if (_ours.Contains(cur.GetInstanceID())) continue;   // 已经是我们的了

                string key = _tex.ContainsKey(img.gameObject.name) ? img.gameObject.name
                           : (_tex.ContainsKey(cur.name) ? cur.name : null);
                if (key == null) continue;

                var want = GetSprite(key, cur);
                if (want != null && cur != want) img.sprite = want;
            }
        }
        catch (Exception e)
        {
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                Plugin.Log.LogWarning($"[STEMFishDLC] 立绘替换异常: {e.Message}");
            }
        }
    }

    private static void Rescan()
    {
        _targets.Clear();

        Image[] all = null;
        try { all = Resources.FindObjectsOfTypeAll<Image>(); } catch { return; }
        if (all == null) return;

        for (int i = 0; i < all.Length; i++)
        {
            var img = all[i];
            if (img == null || img.gameObject == null) continue;

            var n = img.gameObject.name;
            var sn = img.sprite != null ? img.sprite.name : null;

            if (_tex.ContainsKey(n) || (sn != null && _tex.ContainsKey(sn)))
                _targets.Add(img);

            // 诊断：每个"可能是角色立绘"的对象名只报一次
            if (LooksLikeCharacterSlot(n) && _logged.Add(n))
            {
                string spr = img.sprite != null
                    ? $"{img.sprite.name} ({img.sprite.rect.width:0}x{img.sprite.rect.height:0} @ppu{img.sprite.pixelsPerUnit:0.##})"
                    : "null";
                string rt = "";
                try { rt = $" rect={img.rectTransform.rect.width:0}x{img.rectTransform.rect.height:0}"; } catch { }
                Plugin.Log.LogInfo($"[STEMFishDLC] 立绘候选：对象 '{n}' sprite='{spr}'{rt}");
            }
        }
    }

    private static bool LooksLikeCharacterSlot(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        var s = n.ToLowerInvariant();
        return s.Contains("char") || s.Contains("experiment") || s.Contains("gen") || s.Contains("pod");
    }
}
