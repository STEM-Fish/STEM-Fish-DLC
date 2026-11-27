using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  FishAssets - 运行期贴图加载（把 PNG 变成 Sprite）
//  ---------------------------------------------------------------------------
//  游戏没有向 Resources 注册资源的公开入口，所以自定义物品的图标/外观走：
//   PNG(plugins/STEMFishDLC/textures/*.png) → Texture2D.LoadImage → Sprite.Create
//
//  ★ 尺寸对齐（反汇编 Wearable.CreateSprites 确认）：
//    穿戴贴图挂在肢体 Transform 下，localPosition=0 / localScale=1 / rotation=identity，
//    也就是说**贴图的世界大小完全由 Sprite 自己的 pixelsPerUnit + pivot 决定**。
//    所以自定义穿戴图必须用"原版那张贴图"的 PPU 与轴心，才能跟人物完美贴合。
//    → GetSpriteLike(id, reference) 从参照 Sprite 继承 PPU / 归一化 pivot。
// ============================================================================

public static class FishAssets
{
    // 无参照时的兜底：32x32 图标 @ 16 px/unit ≈ 2x2 世界单位
    public const float DefaultPixelsPerUnit = 16f;

    public static string TextureDir => Path.Combine(Paths.PluginPath, "STEMFishDLC", "textures");

    private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
    private static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>();

    // 只按默认 PPU 取（无参照场景）
    public static Sprite GetSprite(string id) => GetSpriteLike(id, null);

    // ==========================================================================
    //  枪械用：世界宽度 = 参照枪 × scale，轴心可指定（默认沿用参照归一化轴心）
    //  ---------------------------------------------------------------------------
    //  ppu = 参照PPU × (本图宽/参照宽) / scale
    //    scale = 1   → 与参照枪（原版 rifle）在世界里一样长
    //    scale = 1.4 → 放大 40%
    //  pivot 用归一化坐标（0,0 = 左下角）：把枪的握把放到轴心上，手就会握在握把处
    // ==========================================================================
    public static Sprite GetGunSprite(string id, Sprite reference, float scale, float pivotX, float pivotY, float dropPx = 0f)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var tex = LoadTexture(id);
        if (tex == null) return null;

        float ppu = DefaultPixelsPerUnit;
        Vector2 pivot = new Vector2(0.5f, 0.5f);
        if (reference != null)
        {
            float refW = reference.rect.width;
            if (refW > 0f && reference.pixelsPerUnit > 0f)
                ppu = reference.pixelsPerUnit * (tex.width / refW);
            var r = reference.rect;
            if (r.width > 0f && r.height > 0f)
                pivot = new Vector2(reference.pivot.x / r.width, reference.pivot.y / r.height);
        }
        if (pivotX >= 0f && pivotY >= 0f) pivot = new Vector2(pivotX, pivotY);
        // 整体下移：轴心往上抬 = 贴图相对手的位置往下（像素单位，跟着贴图分辨率走）
        if (Mathf.Abs(dropPx) > 0.001f) pivot.y += dropPx / tex.height;
        ppu /= Mathf.Clamp(scale <= 0f ? 1f : scale, 0.2f, 4f);

        string key = $"{id}|gun|{ppu:0.###}|{pivot.x:0.####},{pivot.y:0.####}";
        if (_spriteCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, ppu, 0, SpriteMeshType.FullRect);
            sprite.name = id + "_gun";
            _spriteCache[key] = sprite;
            Plugin.Log.LogInfo($"[STEMFishDLC] 枪械贴图 {id}.png ({tex.width}x{tex.height}) → ppu={ppu:0.##} 轴心=({pivot.x:0.###},{pivot.y:0.###}) 世界宽={tex.width / ppu:0.##}u（参照 {reference?.name} 宽 {reference?.rect.width:0}u@ppu{reference?.pixelsPerUnit:0.#}）");
            return sprite;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 枪械 Sprite 创建失败 {id}: {e.Message}");
            _spriteCache[key] = null;
            return null;
        }
    }

    // ==========================================================================
    //  按参照贴图的**世界尺寸**创建（美术像素密度不同也能对齐）
    //  ppu = 参照PPU × (本图宽 / 参照图宽) / scale → 世界宽度 = 参照 × scale
    //    scale = 1   → 和原版同尺寸
    //    scale = 1.6 → 世界里放大 60%（MDRZ9 的 7.62 弹匣比 5.56 弹匣大一圈）
    // ==========================================================================
    public static Sprite GetSpriteMatchSize(string id, Sprite reference, float scale = 1f)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var tex = LoadTexture(id);
        if (tex == null) return null;

        float ppu = DefaultPixelsPerUnit;
        Vector2 pivot = new Vector2(0.5f, 0.5f);
        if (reference != null)
        {
            float refW = reference.rect.width;
            if (refW > 0f && reference.pixelsPerUnit > 0f)
                ppu = reference.pixelsPerUnit * (tex.width / refW);
            var r = reference.rect;
            if (r.width > 0f && r.height > 0f)
                pivot = new Vector2(reference.pivot.x / r.width, reference.pivot.y / r.height);
        }
        scale = Mathf.Clamp(scale <= 0f ? 1f : scale, 0.2f, 4f);
        ppu /= scale;

        string key = $"{id}|match|{ppu:0.###}|{pivot.x:0.####},{pivot.y:0.####}|{scale:0.##}";
        if (_spriteCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, ppu, 0, SpriteMeshType.FullRect);
            sprite.name = id + "_match";
            _spriteCache[key] = sprite;
            Plugin.Log.LogInfo($"[STEMFishDLC] 贴图 {id}.png ({tex.width}x{tex.height}) 匹配世界尺寸 ×{scale:0.##} → ppu={ppu:0.##}（参照 {reference?.name}）");
            return sprite;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] Sprite 创建失败 {id}: {e.Message}");
            _spriteCache[key] = null;
            return null;
        }
    }

    // ==========================================================================
    //  按参照 Sprite 的 PPU / 轴心创建我们的贴图
    //  reference = null 时用 DefaultPixelsPerUnit + 居中轴心
    // ==========================================================================
    public static Sprite GetSpriteLike(string id, Sprite reference)
    {
        if (string.IsNullOrEmpty(id)) return null;

        float ppu = DefaultPixelsPerUnit;
        Vector2 pivot = new Vector2(0.5f, 0.5f);

        if (reference != null)
        {
            if (reference.pixelsPerUnit > 0f) ppu = reference.pixelsPerUnit;
            var r = reference.rect;
            if (r.width > 0f && r.height > 0f)
                pivot = new Vector2(reference.pivot.x / r.width, reference.pivot.y / r.height);
        }

        string key = $"{id}|{ppu:0.###}|{pivot.x:0.####},{pivot.y:0.####}";
        if (_spriteCache.TryGetValue(key, out var cached)) return cached;

        var tex = LoadTexture(id);
        if (tex == null)
        {
            _spriteCache[key] = null;
            return null;
        }

        try
        {
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                pivot, ppu, 0, SpriteMeshType.FullRect);
            sprite.name = $"{id}_{ppu:0.#}";
            _spriteCache[key] = sprite;
            Plugin.Log.LogInfo($"[STEMFishDLC] 贴图 {id}.png ({tex.width}x{tex.height}) → ppu={ppu:0.##}, pivot=({pivot.x:0.##},{pivot.y:0.##})");
            return sprite;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] Sprite 创建失败 {id}: {e.Message}");
            _spriteCache[key] = null;
            return null;
        }
    }

    // ==========================================================================
    //  读 PNG → Texture2D（缓存，像素风用 Point 过滤）
    // ==========================================================================
    private static Texture2D LoadTexture(string id)
    {
        if (_texCache.TryGetValue(id, out var cached))
            return cached != null && cached ? cached : null;

        try
        {
            string path = Path.Combine(TextureDir, id + ".png");
            if (!File.Exists(path))
            {
                _texCache[id] = null;
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                _texCache[id] = null;
                return null;
            }
            tex.filterMode = FilterMode.Point;      // 像素风：不做平滑
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = id;
            _texCache[id] = tex;
            return tex;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 贴图加载失败 {id}: {e.Message}");
            _texCache[id] = null;
            return null;
        }
    }

    public static bool HasTexture(string id) =>
        !string.IsNullOrEmpty(id) && File.Exists(Path.Combine(TextureDir, id + ".png"));

    // 清缓存（热重载时用）
    public static void ClearCache()
    {
        _spriteCache.Clear();
        _texCache.Clear();
    }
}
