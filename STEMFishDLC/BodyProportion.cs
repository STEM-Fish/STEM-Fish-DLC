using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  BodyProportion - 按设定身高 1.70m 放大主角的**视觉尺寸**
//  ---------------------------------------------------------------------------
//  ★ 0.19.0 为什么失败（缩放 Body.transform.localScale → 角色悬浮）
//  ---------------------------------------------------------------------------
//  Body 是 ragdoll：根上 BoxCollider2D + Rigidbody2D，15 个肢体各自也有
//  Rigidbody2D + Collider2D + HingeJoint2D。缩放父节点 = 把所有碰撞箱一起放大，
//  脚底基准没同步补偿 → 物理求解器把整个人撑在空中。
//
//  ★ 为什么也不能用 `Body.heightMultiplier` 拉骨架
//  ---------------------------------------------------------------------------
//  它确实能拉开肢体间距（`HandleVisuals` 里 `V *= heightMultiplier`），但那是
//  **每帧把 localPosition 直接写成拉伸后的值**。而肢体之间是 HingeJoint2D 铰接的，
//  joint 的 anchor 是各自刚体的局部坐标、不跟着变 —— 关节会拼命把 anchor 拉回重合，
//  和代码每帧的写入对抗 → 抖动 / 撕裂。（原版 Milky 是 0.8 往小缩、且美术也小一号，
//  所以看不出问题；我们往大放，照抄不了。）
//
//  ★ 这次的做法：只改"画多大"，碰撞 / 刚体 / 关节 / transform 全都不碰
//  ---------------------------------------------------------------------------
//  `FishSkin.Load` 的 PPU 算法：
//      ppu      = BasePpu * (tex.width / baseSize.x)
//      世界宽度 = tex.width / ppu = baseSize.x / BasePpu
//  ⇒ **贴图的世界尺寸只由 baseSizes.json 决定，和 PNG 像素数无关**
//    （把 PNG 画大 2 倍并不会让人变大，这个坑之前踩过）。
//
//  所以建 sprite 时把 ppu 除以 k：
//      ppu = BasePpu * (tex.width / baseSize.x) / k
//  ⇒ 所有部位贴图的世界尺寸 ×k，而位置 / 碰撞箱 / 刚体 / 关节**完全原样**。
//    物理零影响：不悬浮、不抖动、不卡窄洞。
//
//  代价：肢体中心没动，相邻贴图会重叠 (k-1)（默认 16.8%）——像素风 ragdoll 看起来
//  是"壮了一圈、轮廓大了一圈"，而不是"腿变长了"。这是不动物理能换来的上限；
//  真要改骨架长度，得同时重算每个 HingeJoint2D 的 anchor 与 collider 形状。
//
//  ★ 身高数字
//  ---------------------------------------------------------------------------
//  `PlayerCamera.Start`：`SetCharDetails(Random.Range(130,161) * heightMultiplier, ...)`
//  健康面板显示 `{height}cm | {age}y | #{id}`。设定身高固定 1.70m，所以这里把
//  `WoundView.cInfo[0]` 直接写成 170（存档里的 `SaveInfo.cHeight` 也是 170）。
// ============================================================================

public static class BodyProportion
{
    public static bool Enabled = true;

    // 设定身高 vs 原版基准（EXPie：Random.Range(130,161) × heightMultiplier 1.0）
    public static float HeightCm = 170f;
    public const float VanillaBaseCm = 145.5f;

    /// <summary>贴图世界尺寸倍率（= HeightCm / 145.5，默认 ≈1.168）</summary>
    public static float VisualScale = HeightCm / VanillaBaseCm;

    private static int _heightSetFor;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Body", "ScaleEnabled", true,
                "按设定身高放大主角外观（只改贴图显示大小，碰撞与物理完全不动）").Value;

            HeightCm = Mathf.Clamp(plugin.Config.Bind("Body", "HeightCm", 170f,
                "设定身高（cm）。原版 EXPie 是 Random.Range(130,161) 随机，中值 145.5 作基准").Value,
                100f, 250f);

            VisualScale = Mathf.Clamp(HeightCm / VanillaBaseCm, 0.5f, 1.9f);

            Plugin.Log.LogInfo($"[STEMFishDLC] 体型: {(Enabled
                ? $"{HeightCm:0}cm → 外观 ×{VisualScale:0.000}（基准 {VanillaBaseCm:0.#}cm；碰撞/物理不动）"
                : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 体型配置读取失败: {e.Message}");
        }
    }

    /// <summary>
    /// FishSkin 建 sprite 时用：把 PPU 除下来，世界尺寸就放大 VisualScale 倍。
    /// 只在载入皮肤时算一次，运行时不产生任何开销。
    /// </summary>
    public static float ApplyToPpu(float ppu)
    {
        if (!Enabled || VisualScale <= 0.001f) return ppu;
        if (ppu <= 0.0001f) return ppu;
        return ppu / VisualScale;
    }

    // ---------------------------------------------------------------------
    //  档案身高固定成设定值 + 血迹缩放跟着体型走
    // ---------------------------------------------------------------------
    public static void Tick()
    {
        if (!Enabled) return;

        Body body = null;
        try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
        if (body == null) return;

        // bloodScale 只被 WoundView.UpdateView 用来缩放伤口视图里的血渍，
        // 跟体型同比例视觉才对得上（不影响任何玩法数值）
        try
        {
            if (Mathf.Abs(body.bloodScale - VisualScale) > 0.001f)
                body.bloodScale = VisualScale;
        }
        catch { }

        try
        {
            var v = WoundView.view;
            if (v == null || v.cInfo == null || v.cInfo.Length < 4) return;

            int want = Mathf.RoundToInt(HeightCm);
            int id = body.GetInstanceID();
            if (_heightSetFor == id && v.cInfo[0] == want) return;
            if (v.cInfo[0] == want) { _heightSetFor = id; return; }

            v.cInfo[0] = want;
            v.SetCharDetails(v.cInfo[0], v.cInfo[1], v.cInfo[2], v.cInfo[3]);
            _heightSetFor = id;
            Plugin.Log.LogInfo($"[STEMFishDLC] 档案身高固定为 {want}cm（健康面板 / 存档 cHeight）");
        }
        catch (Exception e) { WarnOnce(e.Message); }
    }

    private static string _lastError;
    private static void WarnOnce(string msg)
    {
        if (_lastError == msg) return;
        _lastError = msg;
        Plugin.Log.LogWarning($"[STEMFishDLC] 体型处理失败: {msg}");
    }
}
