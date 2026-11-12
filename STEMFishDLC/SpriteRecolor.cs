using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SpriteRecolor - 把游戏自带的绿色图标贴图复制一份改成天蓝（注入式，不改游戏文件）
//  ---------------------------------------------------------------------------
//  为什么必须动贴图，而不是只改 Image.color：
//    UGUI 的 Image.color 是**乘法**。原图像素是绿 (0, 1, 0.4)，
//    乘上天蓝 (0.30, 0.80, 1.00) 得到的是 (0, 0.80, 0.40) —— 还是绿的。
//    想让绿变蓝，只能改像素本身。
//
//  ★ v0.13.2 修了一个把图标画成实心块的致命 bug：
//    之前用 `Graphics.Blit(tex, rt, scale, offset)` 只抠 sprite 那一小块。
//    但 scale/offset 是透过材质里的 `_MainTex_ST` 传的，而**URP 会替换全局
//    blit 材质**，那个材质根本不读 ST → 每个图标抠出来的都是图集左上角
//    同一块内容（图标位置对、形状全错 = 作者截图里那种"实心块"）。
//    → 现在改成 **整张图集一次性原样 Blit（不传 scale/offset），再在内存里
//     按 textureRect 裁剪**。不依赖 ST，也就不会再被管线坑。
//    每个图集只读一次并缓存，后续全是内存拷贝。
//
//  ★ 另外两个必须处理的细节：
//    ① sprite 可能被 tight packing —— `rect`（逻辑尺寸）比 `textureRect`
//       （图集里的实际内容）大。新图必须按 `rect` 尺寸建、内容按
//       `textureRectOffset` 摆放，否则 Image 的九宫格/尺寸全乱。
//    ② `Sprite.Create` 必须带 `src.border`，不然 Sliced 的图标会整块拉伸。
// ============================================================================

public static class SpriteRecolor
{
    // 天蓝：和 HealthPanel 用同一套系数（保持亮度，只挪色相）
    private static readonly float SkyR = 0.30f, SkyG = 0.80f, SkyB = 1.00f;

    private static readonly Dictionary<Sprite, Sprite> _done = new Dictionary<Sprite, Sprite>();
    private static readonly List<Sprite> _pending = new List<Sprite>();
    private static readonly HashSet<Sprite> _queued = new HashSet<Sprite>();
    private static readonly List<Sprite> _tmp = new List<Sprite>();

    // 图集缓存：整张纹理的像素（只读一次）
    private class Atlas
    {
        public int W, H;
        public Color32[] Pixels;
        public bool FromGpu;
    }
    private static readonly Dictionary<Texture2D, Atlas> _atlases = new Dictionary<Texture2D, Atlas>();

    private static bool _reported;
    private static bool _diagOnce;

    public static bool Enabled = true;

    // 万一 GPU 读回方向是反的（不同图形 API 差异），把这个打开即可整体上下翻转。
    // 改动后需要重启（缓存里已经有处理好的图）。
    public static bool FlipY = false;

    public static int PendingCount => _pending.Count;
    public static int DoneCount => _done.Count;

    // 请求一个"蓝色版" sprite。没处理完就返回 null（调用方下一帧再问）
    public static Sprite Get(Sprite src)
    {
        if (!Enabled || src == null) return null;

        // 已是我们生成的副本 → 直接还回去，别再入队
        if (!string.IsNullOrEmpty(src.name) && src.name.EndsWith("_sky", System.StringComparison.Ordinal))
            return src;

        Sprite got;
        if (_done.TryGetValue(src, out got)) return got;      // 可能命中 null（处理失败，别再试）

        if (!_queued.Contains(src))
        {
            _queued.Add(src);
            _pending.Add(src);
        }
        return null;
    }

    // 每帧调用：处理 N 个待办
    public static void Pump(int budget = 8)
    {
        if (_pending.Count == 0) return;

        int n = Mathf.Min(budget, _pending.Count);
        _tmp.Clear();
        for (int i = 0; i < n; i++) _tmp.Add(_pending[i]);
        _pending.RemoveRange(0, n);

        for (int i = 0; i < _tmp.Count; i++)
        {
            var src = _tmp[i];
            Sprite made = null;
            try { made = Build(src); } catch (System.Exception e) { Warn($"[STEMFishDLC] 图标重着色异常 {src.name}: {e.Message}"); }
            _done[src] = made;                                // 失败也记 null，避免反复重试
        }
        _tmp.Clear();

        if (_pending.Count == 0 && !_reported)
        {
            _reported = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 图标重着色完成：{_done.Count} 个 sprite / {_atlases.Count} 张图集");
        }
    }

    public static void Reset()
    {
        _done.Clear();
        _pending.Clear();
        _queued.Clear();
        _atlases.Clear();
        _reported = false;
    }

    // ==========================================================================
    //  生成蓝色副本
    // ==========================================================================
    private static Sprite Build(Sprite src)
    {
        if (src == null) return null;

        var tex = src.texture;
        if (tex == null) return null;

        // 图集里被旋转/翻转过的 sprite 没法简单裁剪，跳过（UI 图集几乎不会出现）
        if (src.packingRotation != SpritePackingRotation.None) return null;

        var atlas = GetAtlas(tex);
        if (atlas == null) return null;

        Rect tr = src.textureRect;
        Rect rr = src.rect;

        int tw = Mathf.RoundToInt(tr.width);
        int th = Mathf.RoundToInt(tr.height);
        int rw = Mathf.RoundToInt(rr.width);
        int rh = Mathf.RoundToInt(rr.height);
        if (tw <= 0 || th <= 0 || rw <= 0 || rh <= 0) return null;
        if (rw > 4096 || rh > 4096) return null;

        int tx = Mathf.RoundToInt(tr.x);
        int ty = Mathf.RoundToInt(tr.y);

        // 内容在"逻辑矩形"里的偏移（tight packing 时 rect 比 textureRect 大）
        int offX = 0, offY = 0;
        try
        {
            offX = Mathf.RoundToInt(src.textureRectOffset.x);
            offY = Mathf.RoundToInt(src.textureRectOffset.y);
        }
        catch { }

        // 因为 sprite 是拆开重拼的，无法判别方向，这里直接用 Unity 约定：
        // GetPixels / textureRect / textureRectOffset 全部是**左下原点**，三者一致。
        var outPx = new Color32[rw * rh];
        var clear = new Color32(0, 0, 0, 0);
        for (int i = 0; i < outPx.Length; i++) outPx[i] = clear;

        bool changed = false;
        for (int y = 0; y < th; y++)
        {
            int srcRow = (ty + y) * atlas.W;
            int dstRow = (offY + y) * rw;
            if (offY + y < 0 || offY + y >= rh) continue;

            for (int x = 0; x < tw; x++)
            {
                int dx = offX + x;
                if (dx < 0 || dx >= rw) continue;

                int sx = tx + x;
                if (sx < 0 || sx >= atlas.W) continue;

                int si = srcRow + sx;
                if (si < 0 || si >= atlas.Pixels.Length) continue;

                var c = atlas.Pixels[si];
                if (c.a > 1 && IsGreenish(c))
                {
                    c = ToSky(c);
                    changed = true;
                }
                outPx[dstRow + dx] = c;
            }
        }

        if (!changed) return src;      // 本来就不是绿的 → 直接复用原图

        // 首次成功时打一条参数快照：以后图标若还有问题，从这行就能判断
        // 是 tight packing（rect≠textureRect）还是别的
        if (!_diagOnce)
        {
            _diagOnce = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 图标重着色示例: '{src.name}' " +
                               $"rect={rw}x{rh} textureRect={tw}x{th}@{tx},{ty} " +
                               $"offset={offX},{offY} border={src.border} " +
                               $"packed={src.packed} ppu={src.pixelsPerUnit:0.#}");
        }

        // 可选：整体上下翻转（应对不同图形 API 的读回方向差异）
        if (FlipY)
        {
            for (int y = 0; y < rh / 2; y++)
            {
                int a = y * rw, b = (rh - 1 - y) * rw;
                for (int x = 0; x < rw; x++)
                {
                    var t = outPx[a + x]; outPx[a + x] = outPx[b + x]; outPx[b + x] = t;
                }
            }
        }

        var outTex = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
        outTex.name = tex.name + "_sky";
        outTex.filterMode = tex.filterMode;
        outTex.wrapMode = TextureWrapMode.Clamp;
        outTex.SetPixels32(outPx);
        outTex.Apply(false, false);

        // 轴心按"逻辑矩形"归一化，和原 sprite 完全一致
        Vector2 pivot = new Vector2(0.5f, 0.5f);
        if (rr.width > 0f && rr.height > 0f)
            pivot = new Vector2(src.pivot.x / rr.width, src.pivot.y / rr.height);

        // border 也要带过去：Image 若是 Sliced（九宫格），少了 border 会整块拉伸变形
        var made = Sprite.Create(outTex, new Rect(0f, 0f, rw, rh), pivot,
                                 src.pixelsPerUnit, 0, SpriteMeshType.FullRect, src.border);
        made.name = src.name + "_sky";
        return made;
    }

    // ==========================================================================
    //  图集：整张读进内存（每个图集只做一次）
    //  ---------------------------------------------------------------------------
    //  关键：Blit 时**不传 scale/offset**。传了也没用 —— URP 会换掉全局 blit
    //  材质，而那个材质不读 _MainTex_ST，抠出来的永远是第一块区域。
    // ==========================================================================
    private static Atlas GetAtlas(Texture2D tex)
    {
        Atlas a;
        if (_atlases.TryGetValue(tex, out a)) return a;

        int w = tex.width, h = tex.height;
        if (w <= 0 || h <= 0 || (long)w * h > 4096L * 4096L)
        {
            Warn($"[STEMFishDLC] 图集 {tex.name} 尺寸异常 {w}x{h}，跳过重着色");
            _atlases[tex] = null;
            return null;
        }

        var sw = Stopwatch.StartNew();
        Color32[] px = null;
        bool fromGpu = false;

        // ① 可读纹理：直接拿，最可靠
        try
        {
            if (tex.isReadable) px = tex.GetPixels32();
        }
        catch { px = null; }

        // ② 不可读：整张 Blit 到 RT 再 ReadPixels
        if (px == null)
        {
            RenderTexture rt = null;
            Texture2D tmp = null;
            var prev = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(tex, rt);                     // 整张，不传 UV 参数
                RenderTexture.active = rt;
                tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                tmp.Apply(false, false);
                px = tmp.GetPixels32();
                fromGpu = true;
            }
            catch (System.Exception e)
            {
                Warn($"[STEMFishDLC] 读图集失败 {tex.name}: {e.Message}");
                px = null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
            }
        }

        sw.Stop();
        if (px == null || px.Length != w * h)
        {
            _atlases[tex] = null;
            return null;
        }

        a = new Atlas { W = w, H = h, Pixels = px, FromGpu = fromGpu };
        _atlases[tex] = a;
        Plugin.Log.LogInfo($"[STEMFishDLC] 图集读取 {tex.name} {w}x{h} " +
                           $"来源={(fromGpu ? "GPU(Blit)" : "CPU(可读)")} 耗时={sw.ElapsedMilliseconds}ms");
        return a;
    }

    // ==========================================================================
    //  颜色判定 / 映射（和 HealthPanel 完全一致，避免两处逻辑漂移）
    // ==========================================================================
    public static bool IsGreenish(Color32 c)
    {
        if (c.a < 6) return false;
        float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
        if (g <= 0.08f) return false;
        if (g < r * 1.25f) return false;
        if (g < b * 1.25f) return false;
        return true;
    }

    public static Color32 ToSky(Color32 c)
    {
        float lum = Mathf.Clamp01(c.g / 255f);
        return new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(lum * SkyR * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(lum * SkyG * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(lum * SkyB * 255f), 0, 255),
            c.a);
    }

    // HealthPanel 用的是 Color 版本，这里做一层转接
    public static bool IsGreenish(Color c)
    {
        if (c.a < 0.02f) return false;
        if (c.g <= 0.08f) return false;
        if (c.g < c.r * 1.25f) return false;
        if (c.g < c.b * 1.25f) return false;
        return true;
    }

    public static Color ToSky(Color c)
    {
        float lum = Mathf.Clamp01(c.g);
        return new Color(lum * SkyR, lum * SkyG, lum * SkyB, c.a);
    }

    private static string _lastWarn;
    private static void Warn(string msg)
    {
        if (_lastWarn == msg) return;
        _lastWarn = msg;
        Plugin.Log.LogWarning(msg);
    }
}
