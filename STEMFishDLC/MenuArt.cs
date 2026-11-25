using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  MenuArt - 主菜单立绘的「配套美术」自动适配
//  ---------------------------------------------------------------------------
//  背景：
//    主菜单那只坐着的 Experiment（back15 系列）悬停时会有一圈白色轮廓 +
//    「下到洞穴中」文字。它们都由场景里的 `MenuExperimentBlink` 驱动：
//        Sprite[] sprites          ← 眨眼三帧（主立绘）
//        Sprite[] outlineSprites   ← **轮廓三帧**（按旧造型手绘的白影）
//        Sprite[] shineSprites     ← 背后的红光
//        Sprite[] reserveCharSprites ← 另外两个角色（Milky / Dune）的 9 张图
//        Image    outline          ← 轮廓用的那个 Image
//        Image    shine            ← 光晕用的那个 Image
//        RectTransform text        ← 「下到洞穴中」
//        Vector2[] textOffset      ← 每个角色各自的文字偏移（Start 里加到 localPosition）
//    作者换了 back15 的美术之后，轮廓贴图还是旧姿势，文字位置也还是按旧造型摆的。
//
//  做法：
//    ① **现场生成白色轮廓**：取当前显示的立绘 → 降采样 → alpha 二值化 →
//       分离式最大值滤波（先横后纵）做膨胀 → 填纯白。Pivot / PPU 照抄主立绘，
//       所以贴上去与角色严丝合缝。三个眨眼帧共用一张（轮廓形状不随眨眼变）。
//    ② 把生成的轮廓写进 `outlineSprites` 的**每一项**，并把 `outline.sprite` 直接设上
//       （Start 已经跑过的话，光改数组不会立刻生效）。
//    ③ 文字 / 点击框按**新造型的 alpha 包围盒**重新摆：原版 UI 里这两个位置与角色
//       包围盒是固定关系（从实测坐标反推出来，见下面三个常量），所以换成任何造型
//       都会自动对到新姿势上；`[Skin] MenuTextOffsetX/Y` 可以微调。
//
//  配置：`[Skin] MenuAutoLayout` / `MenuOutlineRadius` / `MenuTextOffsetX/Y`
// ============================================================================

public static class MenuArt
{
    public static bool Enabled = true;
    public static int OutlineRadius = 7;
    public static float TextOffsetX = 0f;
    public static float TextOffsetY = 0f;

    // 原版 UI 里文字/点击框相对"角色包围盒"的固定关系（由实测坐标反推，误差 < 1px）：
    //   Text      中心 = (角色中心 x + 120,  角色顶部 y + 43)
    //   Clickable 中心 = (角色中心 x + 132,  角色中心 y)
    //   推导依据（原版 back15 实测 bbox 中心 (-499,-171)、顶边 78）→ 文字 (-379,121)、
    //   点击框 (-367,-171)，与场景里的 (-380,121) / (-367.21,-171.82) 吻合。
    private const float TextGapRight = 120f;
    private const float TextGapTop = 56f;
    private const float ClickGapRight = 132f;

    private static Image _img;
    private static Sprite _outline;
    private static string _outlineKey;      // 生成 _outline 时用的立绘 key（back15 / genexpnewalt …）
    private static Component _blink;        // 当前的 MenuExperimentBlink（进出游戏会被重建）
    private static Image _outlineImg;       // 它里面的 outline Image
    private static int _boundId;            // 已经绑定过的 _blink 实例 ID
    private static readonly HashSet<string> _logged = new HashSet<string>();
    private static readonly HashSet<int> _moved = new HashSet<int>();

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Skin", "MenuAutoLayout", true,
                "主菜单立绘：自动生成悬停轮廓，并按新美术重摆文字/点击框").Value;
            var widthEntry = plugin.Config.Bind("Skin", "MenuOutlineRadius", 5,
                "悬停描边的线宽（以 1920 宽的源图计，单位像素；原版是 4~5）");
            OutlineRadius = Mathf.Clamp(widthEntry.Value, 1, 30);

            // v0.18.6：0.18.4/0.18.5 写过默认 7，而 7 是"填实心"时代的数值，现在偏粗一半。
            // 一次性迁到 5（带标记，作者以后自己改回 7 不会被覆盖）。
            try
            {
                var marker = plugin.Config.Bind("Skin", "OutlineMigrated", false,
                    "内部标记：描边线宽是否已从旧默认 7 改成 5，别手动改");
                if (!marker.Value && OutlineRadius == 7)
                {
                    widthEntry.Value = 5;
                    OutlineRadius = 5;
                    Plugin.Log.LogInfo("[STEMFishDLC] 悬停描边线宽：7 → 5（一次性迁移）");
                }
                marker.Value = true;
            }
            catch { }
            TextOffsetX = plugin.Config.Bind("Skin", "MenuTextOffsetX", 0f,
                "主菜单「下到洞穴中」文字的水平微调（UI 像素，正数向右）").Value;
            TextOffsetY = plugin.Config.Bind("Skin", "MenuTextOffsetY", 0f,
                "主菜单「下到洞穴中」文字的垂直微调（UI 像素，正数向上）").Value;
        }
        catch { }
    }

    // 选角换了立绘（sprite 名字还叫 back15）→ 清掉轮廓/文字缓存，让 Tick 下一帧按新图重建。
    // ★ 不清 _img：换的是同一个 Image，重新扫描可能命中别处同名的 Experiment。
    public static void Invalidate()
    {
        _outline = null;
        _outlineKey = null;
        _boundId = 0;
        _blink = null;
        _outlineImg = null;
        _moved.Clear();
        _logged.Clear();
    }

    /// <summary>主菜单正在显示的那只立绘（没有就现找）</summary>
    public static Image CurrentImage
    {
        get
        {
            if (_img == null) FindExperiment();
            return _img;
        }
    }

    public static void Tick()
    {
        if (!Enabled) return;

        try
        {
            if (_img == null) FindExperiment();
            if (_img == null) return;

            var sp = _img.sprite;
            if (sp == null) return;

            // ★ 动态轮廓/重摆文字只服务 SF-07 的替换美术。
            //   选原版角色时 outline 用原版 outlineSprites（Start 里按 chr 配好），
            //   文字用原版 textOffset——两者都是原版手调的，零偏移。
            //   我们的生成版只在「当前角色是 SF-07」时接管。
            if (CharacterSelect.Current != CharacterSelect.Sf07) return;

            string key = NormalizeKey(sp.name);

            // 轮廓本身只跟"显示的是哪张立绘"有关，换图才重建
            if (_outline == null || _outlineKey != key)
            {
                // ★ 选 SF-07 时，必须等 LoadingArt 把 sprite 换成我们的图再画轮廓。
                //   否则用的是原版 EXPie 的 alpha —— 形状对不上，看起来就是「轮廓偏移」。
                if (CharacterSelect.Current == CharacterSelect.Sf07 && !LoadingArt.IsOurs(sp))
                    return;

                _outline = BuildOutline(sp, OutlineRadius);
                _outlineKey = key;
                _boundId = 0;                 // 新轮廓 → 必须重新绑定
                if (_outline != null) _logged.Clear();
            }
            if (_outline == null) return;

            // ★ 关键：**每帧校验绑定是否还在**。
            //   进出一次游戏会重建主菜单，新的 MenuExperimentBlink 里 outlineSprites 又是原版
            //   experimentOverlay。以前用 `key == _key` 提前 return，新实例永远得不到我们的轮廓
            //   → "刚进游戏很好，进一次游戏回来就恢复原状"。
            EnsureBound(_img, key);
        }
        catch (Exception e)
        {
            if (_logged.Add("err")) Plugin.Log.LogWarning($"[STEMFishDLC] 菜单立绘适配失败: {e.Message}");
        }
    }

    // back15 / back15blink1 / back15blink2 共用一张轮廓（轮廓形状不随眨眼变）
    private static string NormalizeKey(string k)
    {
        if (string.IsNullOrEmpty(k)) return k;
        // 同一角色的三帧归成一个 key（眨眼帧不重建轮廓）：
        //   back15 / back15blink1 / back15blink2          → back15
        //   back15milky / back15milkyblink1               → back15milky
        //   back15dune / back15duneblink1                 → back15dune
        // ★ 以前只要 StartsWith("back15") 就归成 back15，导致切到 Milky/Dune
        //   时 key 不变、轮廓不重建（形状不切 + 位置偏移）。
        if (!k.StartsWith("back15", StringComparison.Ordinal)) return k;
        int bi = k.IndexOf("blink", StringComparison.Ordinal);
        return bi > 0 ? k.Substring(0, bi) : k;
    }

    private static void FindExperiment()
    {
        PreRunScript pre = null;
        try { pre = PreRunScript.instance; } catch { }
        if (pre == null) return;

        Transform root = null;
        try { root = pre.mainCanvas != null ? pre.mainCanvas.transform : pre.transform; } catch { }
        if (root == null) return;

        var gs = root.GetComponentsInChildren<Image>(true);
        Image fallback = null;
        for (int i = 0; i < gs.Length; i++)
        {
            var g = gs[i];
            if (g == null || g.gameObject == null) continue;
            if (g.gameObject.name != "Experiment") continue;
            if (g.sprite == null) continue;

            bool hasBlink = false;
            foreach (var c in g.GetComponents<Component>())
                if (c != null && c.GetType().Name == "MenuExperimentBlink") { hasBlink = true; break; }
            if (!hasBlink) { if (fallback == null) fallback = g; continue; }

            _img = g;
            _blink = null;            // 新实例 → 重新找组件
            _outlineImg = null;
            _boundId = 0;
            _logged.Clear();
            _moved.Clear();
            Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：找到 Experiment（{g.GetType().Name}，sprite={g.sprite.name}）");
            return;
        }

        if (fallback != null)
        {
            _img = fallback;
            _blink = null;
            _outlineImg = null;
            _boundId = 0;
            _logged.Clear();
            _moved.Clear();
        }
    }

    // 找到（并缓存）MenuExperimentBlink，然后确认它上面还是我们的轮廓
    private static void EnsureBound(Image img, string key)
    {
        if (_blink == null || _blink.gameObject == null)
        {
            _blink = null;
            _outlineImg = null;
            _boundId = 0;

            var comps = img.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                if (c.GetType().Name != "MenuExperimentBlink") continue;
                _blink = c;
                break;
            }
            if (_blink == null) { FallbackBind(img); _boundId = -1; return; }

            var f = Field(_blink.GetType(), "outline");
            _outlineImg = f != null ? f.GetValue(_blink) as Image : null;
        }

        int bid = _blink.GetInstanceID();
        if (_boundId == bid)
        {
            // 已就位：outline 上挂的还是我们生成的描边
            if (_outlineImg != null && _outlineImg.sprite == _outline) return;
            // 没有 outline 字段（老版本游戏）→ 已经兜底过，别每帧重刷
            if (_outlineImg == null) return;
        }

        BindBlink(_blink.GetType(), _blink);

        if (_logged.Add("rebind_" + bid))
            Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：已绑定轮廓到 MenuExperimentBlink#{bid}" +
                               (_outlineKey != null ? $"（立绘 {_outlineKey}）" : ""));

        // 重新取一次 outline Image：可能连这个 Image 都被重建了（旧引用已销毁），
        // 不刷新的话下一帧又会判定"没绑上"，变成每帧重刷。
        var fOut = Field(_blink.GetType(), "outline");
        _outlineImg = fOut != null ? fOut.GetValue(_blink) as Image : null;
        _boundId = bid;
        LayoutFromArt(img, key);
    }

    private static FieldInfo Field(Type t, string name)
    {
        try { return t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
        catch { return null; }
    }

    // ======================================================================
    //  ① 生成白色轮廓贴图
    // ======================================================================
    private static Sprite BuildOutline(Sprite src, int thickness)
    {
        try
        {
            var st = src.texture;
            if (st == null) return null;

            int fullW = st.width, fullH = st.height;
            if (fullW < 4 || fullH < 4) return null;

            // 原版轮廓是 1920×1080 里 4~5px 的细线，必须**全分辨率**取 alpha，
            // 否则细线（电缆、耳机线那种 1~2px 的东西）在半分辨率下会整条消失。
            // 只有超大贴图才降采样，控制一次性耗时。
            const int MaxDim = 2048;
            float scale = 1f;
            if (fullW > MaxDim || fullH > MaxDim)
                scale = MaxDim / (float)Mathf.Max(fullW, fullH);
            int w = Mathf.Max(4, Mathf.RoundToInt(fullW * scale));
            int h = Mathf.Max(4, Mathf.RoundToInt(fullH * scale));
            int r = Mathf.Max(1, Mathf.RoundToInt(thickness * scale));

            var full = ReadPixels(st, w, h);
            if (full == null) return null;

            var px = full.GetPixels32();
            int n = w * h;
            var mask = new byte[n];
            for (int i = 0; i < n; i++) mask[i] = px[i].a > 0 ? (byte)1 : (byte)0;

            // 分离式最大值滤波（先横后纵）＝ 方形膨胀；再减去原剪影，得到"外圈描边带"
            var tmp = new byte[n];
            var dil = new byte[n];

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte v = 0;
                    int x0 = Mathf.Max(0, x - r), x1 = Mathf.Min(w - 1, x + r);
                    for (int k = x0; k <= x1; k++) if (mask[row + k] != 0) { v = 1; break; }
                    tmp[row + x] = v;
                }
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    byte v = 0;
                    int y0 = Mathf.Max(0, y - r), y1 = Mathf.Min(h - 1, y + r);
                    for (int k = y0; k <= y1; k++) if (tmp[k * w + x] != 0) { v = 1; break; }
                    dil[y * w + x] = v;
                }
            }

            // ★ 只保留「膨胀后 − 原剪影」的那一圈：原版 experimentOverlay 就是一条
            //   4~5 像素宽的描边（实测：不透明像素只占 bbox 的 4%，水平扫描每段 4~5px）。
            //   以前把整个膨胀结果填白 → 悬停时变成一坨实心白影。
            var outPx = new Color32[n];
            for (int i = 0; i < n; i++)
                outPx[i] = (dil[i] != 0 && mask[i] == 0)
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(255, 255, 255, 0);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels32(outPx);
            tex.Apply(false, false);
            try { tex.name = src.name + "_outline"; } catch { }
            UnityEngine.Object.Destroy(full);

            // 几何照抄主立绘：同一逻辑尺寸 → 贴上去严丝合缝
            float cx = src.rect.width > 0.01f ? src.rect.width : fullW;
            float cy = src.rect.height > 0.01f ? src.rect.height : fullH;
            var pivot = new Vector2(src.pivot.x / cx, src.pivot.y / cy);
            var sp = Sprite.Create(tex, new Rect(0, 0, w, h),
                                   pivot, src.pixelsPerUnit * (w / (float)fullW), 0, SpriteMeshType.FullRect, src.border);
            // ★ 名字绝不能和皮肤包的 PNG 名相同：LoadingArt 的 Image.sprite 钩子会按名字匹配，
            //   同名会把轮廓又换成主立绘（那样轮廓就"消失"了）。
            sp.name = "menuoutline_" + SafeName(src);
            Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：已生成白色描边 {w}×{h}（源 {fullW}×{fullH}，线宽 {r}px）");
            return sp;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成轮廓失败: {e.Message}");
            return null;
        }
    }

    private static string SafeName(Sprite s)
    {
        try { return s.name; } catch { return "menu"; }
    }

    // 把任意贴图（可能是压缩/图集）读成可读的 Texture2D
    private static Texture2D ReadPixels(Texture2D src, int w, int h)
    {
        try
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 轮廓读像素失败: {e.Message}");
            return null;
        }
    }

    // ======================================================================
    //  ② 把轮廓塞进 MenuExperimentBlink
    // ======================================================================
    //  注意：这里会被**每次进出游戏后**重复调用（主菜单是重新实例化的），
    //  所以既不能假设"已经做过就跳过"，也不能每次都刷日志。
    private static void FallbackBind(Image img)
    {
        try
        {
            int n = ReplaceLooseSpriteFields(img, _outline);
            if (n == 0 && _logged.Add("probe")) Probe(img.gameObject);
        }
        catch (Exception e)
        {
            if (_logged.Add("bind_err")) Plugin.Log.LogWarning($"[STEMFishDLC] 轮廓绑定失败: {e.Message}");
        }
    }

    private static int BindBlink(Type type, Component c)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        int n = 0;

        // ★ 不改 outlineSprites 数组：那是原版三个角色的轮廓，被我们覆盖后，
        //   ApplyMenuPortrait 切回原版角色时会拿到错的图。只改 outline Image 自己的 sprite。
        //   （原版 Start 重跑时会把 outline 设回 outlineSprites[0]，我们的 Invalidate
        //    会让 Tick 重新绑定，不怕。）

        // --- Image outline：Start 已经跑过的话，光改数组不会立刻生效 ---
        var fImg = type.GetField("outline", F);
        var oimg = fImg != null ? fImg.GetValue(c) as Image : null;
        if (oimg != null)
        {
            if (_logged.Add("outlineImg"))
            {
                var cur = oimg.sprite;
                string info = cur != null ? $"{cur.name} {cur.rect.width:0}×{cur.rect.height:0}" : "null";
                string rect = "";
                try { rect = $" RT={oimg.rectTransform.rect.width:0}×{oimg.rectTransform.rect.height:0}"; } catch { }
                Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：轮廓 Image（原 {info}{rect}）已换上生成轮廓");
            }
            oimg.sprite = _outline;
            // ★ 不再把主图的 RectTransform 抄给轮廓。
            //   轮廓和主图的父级/锚点不是同一套，抄 anchoredPosition 必然错位；
            //   而且这个错位会一直留着（切回原版角色时 MenuArt 不再接管，没人改回去），
            //   只有重开游戏重建场景才恢复 —— 正是"选过 SF-07 后轮廓整体上移"的现象。
            //   原版轮廓本来就和主图对齐，我们生成的 sprite 几何也照抄主图，所以不用改 RT。
            n++;
        }

        return n;
    }

    private static int ReplaceLooseSpriteFields(Image img, Sprite outline)
    {
        int n = 0;
        var comps = img.gameObject.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null) continue;
            var type = c.GetType();
            if (type.Namespace != null && type.Namespace.StartsWith("UnityEngine")) continue;

            var fs = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int k = 0; k < fs.Length; k++)
            {
                var f = fs[k];
                if (f.FieldType != typeof(Sprite)) continue;
                if (f.Name == "m_Sprite") continue;
                try
                {
                    var cur = f.GetValue(c) as Sprite;
                    if (cur == null || cur == img.sprite) continue;
                    f.SetValue(c, outline);
                    n++;
                    if (_logged.Add(type.Name + "." + f.Name))
                        Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：轮廓字段 {type.Name}.{f.Name}（旧 {cur.name}）已替换");
                }
                catch { }
            }
        }
        return n;
    }

    // 找不到时的诊断：把组件、Sprite/Texture 字段全列出来
    private static void Probe(GameObject go)
    {
        try
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 菜单立绘：没找到轮廓字段，输出诊断——");
            var comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var type = c.GetType();
                Plugin.Log.LogWarning($"[STEMFishDLC]   组件 {i}: {type.FullName}");
                var fs = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int k = 0; k < fs.Length; k++)
                {
                    var f = fs[k];
                    object v = null;
                    try { v = f.GetValue(c); } catch { }
                    if (f.FieldType == typeof(Sprite))
                    {
                        var s = v as Sprite;
                        Plugin.Log.LogWarning($"[STEMFishDLC]      Sprite {f.Name} = {(s != null ? s.name + $" {s.rect.width:0}×{s.rect.height:0}" : "null")}");
                    }
                    else if (f.FieldType == typeof(Sprite[]))
                    {
                        var a = v as Sprite[];
                        Plugin.Log.LogWarning($"[STEMFishDLC]      Sprite[] {f.Name} = {(a != null ? a.Length + " 项" : "null")}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 菜单立绘诊断失败: {e.Message}");
        }
    }

    // ======================================================================
    //  ③ 文字 / 点击框按新造型的 alpha 包围盒重新摆
    // ======================================================================
    private static void LayoutFromArt(Image img, string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith("back15")) return;
            var sp = img.sprite;
            if (sp == null) return;

            var box = AlphaBox(sp);
            if (box == Vector4.zero) return;

            var rt = img.rectTransform;
            float W = rt.rect.width, H = rt.rect.height;

            // 包围盒 → 以 RectTransform 中心为原点的 UI 坐标
            float cx = (box.x + box.z) * 0.5f * W - W * 0.5f;
            float cy = (box.y + box.w) * 0.5f * H - H * 0.5f;
            float top = box.w * H - H * 0.5f;

            // 原版实测关系（见类头注释）
            var textPos = new Vector2(cx + TextGapRight + TextOffsetX, top + TextGapTop + TextOffsetY);
            var clickPos = new Vector2(cx + ClickGapRight, cy);

            // 文字优先用组件自己的 text 字段（那才是 MenuExperimentBlink 认的那个）
            bool textDone = SetBlinkText(img, textPos);
            if (!textDone) SetChildLocal(rt, "Text", textPos);
            SetChildLocal(rt, "Clickable", clickPos);

            if (_logged.Add(key + "_layout"))
                Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：按新造型重摆 → 文字 {textPos} / 点击框 {clickPos}" +
                                   $"（角色 bbox 中心 ({cx:0},{cy:0})，顶部 {top:0}，UI {W:0}×{H:0}）");
        }
        catch (Exception e)
        {
            if (_logged.Add("layout_err")) Plugin.Log.LogWarning($"[STEMFishDLC] 菜单立绘重摆失败: {e.Message}");
        }
    }

    private static bool SetBlinkText(Image img, Vector2 pos)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var comps = img.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var type = c.GetType();
                if (type.Name != "MenuExperimentBlink") continue;

                var fText = type.GetField("text", F);
                var trt = fText != null ? fText.GetValue(c) as RectTransform : null;
                if (trt == null) continue;

                if (_moved.Add(trt.GetInstanceID()))
                {
                    // v0.23：按文字框实际高度再抬半个字高，让**字底边**落在 top+gap 之上，
                    // 上限 30px（两行预留框别把字顶到屏幕外）
                    float lift = 0f;
                    try { lift = Mathf.Clamp(trt.rect.height * 0.5f, 0f, 30f); } catch { }
                    var final = new Vector2(pos.x, pos.y + lift);

                    // Start 是 localPosition += textOffset[chr]，所以我们也用 localPosition
                    trt.localPosition = new Vector3(final.x, final.y, trt.localPosition.z);

                    // 顺手把 textOffset 也改成同一位置（Start 若重跑，量不会再次叠加）
                    try
                    {
                        var fOff = type.GetField("textOffset", F);
                        var offs = fOff != null ? fOff.GetValue(c) as Vector2[] : null;
                        var fChr = type.GetField("chr", F);
                        int chr = fChr != null ? Convert.ToInt32(fChr.GetValue(c)) : 0;
                        if (offs != null && offs.Length > 0 && chr >= 0 && chr < offs.Length)
                            offs[chr] = final;
                    }
                    catch { }

                    Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：文字（组件 text 字段）已移到 {final}（抬升 {lift:0}）");
                }
                return true;
            }
        }
        catch { }
        return false;
    }

    private static void SetChildLocal(RectTransform parent, string childName, Vector2 pos)
    {
        try
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var ch = parent.GetChild(i);
                if (ch == null || ch.name != childName) continue;

                var crt = ch as RectTransform;
                if (crt == null) continue;
                if (!_moved.Add(crt.GetInstanceID())) return;   // 只搬一次

                crt.localPosition = new Vector3(pos.x, pos.y, crt.localPosition.z);
                Plugin.Log.LogInfo($"[STEMFishDLC] 菜单立绘：{childName} 已移到 {pos}");
                return;
            }
        }
        catch { }
    }

    // 当前立绘的 alpha 包围盒（归一化，y 从下）
    private static Vector4 AlphaBox(Sprite sp)
    {
        try
        {
            var tex = sp.texture;
            if (tex == null) return Vector4.zero;

            const int BW = 160, BH = 90;
            var rt = RenderTexture.GetTemporary(BW, BH, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var small = new Texture2D(BW, BH, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, BW, BH), 0, 0);
            small.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = small.GetPixels32();
            int minX = BW, minY = BH, maxX = -1, maxY = -1;
            for (int y = 0; y < BH; y++)
                for (int x = 0; x < BW; x++)
                    if (px[y * BW + x].a > 24)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            UnityEngine.Object.Destroy(small);
            if (maxX < 0) return Vector4.zero;

            return new Vector4(minX / (float)BW, minY / (float)BH,
                               (maxX + 1) / (float)BW, (maxY + 1) / (float)BH);
        }
        catch { return Vector4.zero; }
    }
}
