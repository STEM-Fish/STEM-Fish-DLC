using System;
using System.Collections.Generic;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  ItemCollider - 把自定义物品的碰撞箱收到贴图的"不透明范围"上
//  ---------------------------------------------------------------------------
//  我们的物品都是克隆原版 prefab（rifle / riflemagazine / carapace / riothelmet），
//  碰撞箱是原版那张贴图的矩形，和我们的美术尺寸/长宽比都不一样：
//    · 枪：原版 rifle 的箱子比我们高很多 → 悬停/拾取范围飘在枪管外面
//    · 护甲：原版 carapace 的箱子和我们画的贴图不符
//
//  反汇编确认：物品身上没有脚本缓存 Collider2D（Item 完全不碰碰撞箱），
//  只有 Body.DoPickupCheck / PlayerCamera.HandleWorldUI 用碰撞箱做光标命中，
//  所以直接改 BoxCollider2D 的 size/offset 是安全的。
//
//  做法：读贴图的 alpha，求不透明像素的包围盒（像素坐标），
//        再按 sprite 的 pivot / pixelsPerUnit 换算成局部坐标写进碰撞箱。
// ============================================================================

public static class ItemCollider
{
    private const byte AlphaThreshold = 32;

    private static readonly Dictionary<string, Rect> _bboxCache = new Dictionary<string, Rect>();

    public static void Fit(GameObject go, Sprite sprite, string key)
    {
        if (go == null || sprite == null) return;
        try
        {
            var tex = sprite.texture;
            if (tex == null) return;

            string ck = key + "|" + tex.width + "x" + tex.height;
            if (!_bboxCache.TryGetValue(ck, out var bbox))
            {
                bbox = ComputeOpaqueBounds(tex);
                _bboxCache[ck] = bbox;
            }
            if (bbox.width < 1f || bbox.height < 1f)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] {key}: 贴图没有不透明像素，跳过碰撞箱调整");
                return;
            }

            float ppu = Mathf.Max(0.001f, sprite.pixelsPerUnit);
            var rect = sprite.rect;
            // sprite 轴心（像素，相对 rect 左下角）
            Vector2 pivotPx = new Vector2(sprite.pivot.x - rect.x, sprite.pivot.y - rect.y);

            // 不透明范围换算到局部坐标（局部原点 = 轴心）
            float left = (bbox.xMin - pivotPx.x) / ppu;
            float right = (bbox.xMax - pivotPx.x) / ppu;
            float bottom = (bbox.yMin - pivotPx.y) / ppu;
            float top = (bbox.yMax - pivotPx.y) / ppu;

            Vector2 size = new Vector2(right - left, top - bottom);
            Vector2 offset = new Vector2((left + right) * 0.5f, (bottom + top) * 0.5f);

            int n = 0;
            foreach (var col in go.GetComponentsInChildren<Collider2D>(true))
            {
                if (col == null) continue;
                if (col is BoxCollider2D box)
                {
                    box.size = size;
                    box.offset = offset;
                    n++;
                }
                else if (col is CircleCollider2D cir)
                {
                    cir.radius = Mathf.Max(size.x, size.y) * 0.5f;
                    cir.offset = offset;
                    n++;
                }
                else if (col is CapsuleCollider2D cap)
                {
                    cap.size = size;
                    cap.offset = offset;
                    n++;
                }
                else if (col is PolygonCollider2D poly)
                {
                    float hw = size.x * 0.5f, hh = size.y * 0.5f;
                    poly.pathCount = 1;
                    poly.SetPath(0, new[]
                    {
                        new Vector2(-hw, -hh), new Vector2(-hw, hh),
                        new Vector2(hw, hh), new Vector2(hw, -hh),
                    });
                    poly.offset = offset;
                    n++;
                }
            }

            if (n > 0)
                Plugin.Log.LogInfo($"[STEMFishDLC] 碰撞箱 {key}: {size.x:0.###} x {size.y:0.###} u，偏移 ({offset.x:0.###}, {offset.y:0.###})（改了 {n} 个）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 碰撞箱调整失败 {key}: {e.Message}");
        }
    }

    // 不透明像素包围盒（像素坐标，左下为原点，和 sprite.rect 同空间）
    private static Rect ComputeOpaqueBounds(Texture2D tex)
    {
        try
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x].a < AlphaThreshold) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return new Rect(0, 0, 0, 0);
            return new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读取贴图 alpha 失败 {tex.name}: {e.Message}");
            return new Rect(0, 0, 0, 0);
        }
    }
}
