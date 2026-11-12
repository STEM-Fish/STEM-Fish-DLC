using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SpriteTint - 运行时改贴图（去色 / 压暗 / 调色）
//  ---------------------------------------------------------------------------
//  场景：想给"黑色双肩包"用原版 bigpack 的图，但饱和度降到 0 再压暗。
//  直接 SpriteRenderer.color 只能整体乘色，做不到去色，所以必须重建贴图。
//
//  ★ 难点：Resources 里的贴图默认不可读（没勾 Read/Write），GetPixels32 会失败。
//    解法：RenderTexture 中转 —— Graphics.Blit 把贴图画进 RT，RT 永远是 CPU 可读的。
//    然后 ReadPixels 拿出像素，在 CPU 上算灰度，再建一张新 Texture2D。
//
//  返回的 Sprite 沿用了源 Sprite 的 rect / pivot / pixelsPerUnit，
//  所以换上去以后在世界里的大小和锚点完全一致（穿戴时也贴合）。
// ============================================================================

public static class SpriteTint
{
    public static Sprite Darken(Sprite src, float darken = 0.5f, float keepSaturation = 0f)
    {
        if (src == null) return null;
        try
        {
            var tex = src.texture;
            if (tex == null) return null;

            int tw = tex.width, th = tex.height;
            if (tw <= 0 || th <= 0) return null;

            // ① 通过 RenderTexture 拿到像素（绕过 Read/Write 限制）
            var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;

                var px = readable.GetPixels32();
                UnityEngine.Object.Destroy(readable);

                // ② 在 CPU 上去色 + 压暗
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    if (c.a == 0) continue;
                    float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    float r = Mathf.Lerp(lum, c.r, keepSaturation) * darken;
                    float g = Mathf.Lerp(lum, c.g, keepSaturation) * darken;
                    float b = Mathf.Lerp(lum, c.b, keepSaturation) * darken;
                    px[i] = new Color32((byte)Mathf.Clamp(r, 0, 255),
                                        (byte)Mathf.Clamp(g, 0, 255),
                                        (byte)Mathf.Clamp(b, 0, 255), c.a);
                }

                // ③ 只裁出 sprite 用到的那一块
                var rect = src.textureRect;
                int rw = Mathf.Max(1, Mathf.RoundToInt(rect.width));
                int rh = Mathf.Max(1, Mathf.RoundToInt(rect.height));
                int rx = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, Mathf.Max(0, tw - rw));
                int ry = Mathf.Clamp(Mathf.RoundToInt(rect.y), 0, Mathf.Max(0, th - rh));

                var dst = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
                dst.filterMode = FilterMode.Point;
                dst.wrapMode = TextureWrapMode.Clamp;
                dst.name = src.name + "_dark";
                // SetPixels32(Color32[], miplevel) 只接受整张贴图；这里自己按块拷贝
                var cropped = new Color32[rw * rh];
                for (int y = 0; y < rh; y++)
                    Array.Copy(px, (ry + y) * tw + rx, cropped, y * rw, rw);
                dst.SetPixels32(cropped);
                dst.Apply();

                // ④ 轴心换算：把"纹理像素"换算成"裁剪块内的归一化坐标"
                Vector2 pivot = new Vector2(0.5f, 0.5f);
                try
                {
                    float px0 = src.pivot.x - src.rect.x;   // 相对裁剪块
                    float py0 = src.pivot.y - src.rect.y;
                    pivot = new Vector2(Mathf.Clamp01(px0 / rw), Mathf.Clamp01(py0 / rh));
                }
                catch { }

                var sp = Sprite.Create(dst, new Rect(0, 0, rw, rh), pivot,
                    src.pixelsPerUnit > 0f ? src.pixelsPerUnit : 16f, 0, SpriteMeshType.FullRect);
                sp.name = src.name + "_dark";
                return sp;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 贴图改色失败 {(src != null ? src.name : "null")}: {e.Message}");
            return null;
        }
    }
}
