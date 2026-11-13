using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  BodyScale - 把主角缩到设定身高 1.70m（整体等比，不是纵向拉伸）
//  ---------------------------------------------------------------------------
//  反汇编确认的玩家结构（正式版 1.0）：
//    Dune / Milky / 第三位        ← 预制体根（只有 Transform）
//      Body                       ← 物理骨架：BoxCollider2D + Rigidbody2D
//        Head / UpTorso / DownTorso / 四肢 ……  每个肢体都是
//        SpriteRenderer + Rigidbody2D + Collider2D + HingeJoint2D（真正的 ragdoll）
//      Anim                       ← 屏幕外（x = -10）的动画骨骼，Body.bodyAnimator
//      TalkText
//
//  `Body.HandleVisuals` 每帧做的是：把 `Anim` 骨骼的姿势换算到 Body 局部空间，
//  写进各肢体的 localPosition / eulerAngles —— 所以**肢体是长在 Body 下面的**。
//
//  ⇒ 直接缩放 `Body.transform.localScale` 就是干净的等比放大：
//      · 各肢体 localPosition × k        → 骨架撑开（身高↑，肩宽也↑）
//      · 肢体的 SpriteRenderer 跟着 × k  → 贴图同比变大（不是拉伸）
//      · 肢体的 Collider2D / HingeJoint2D 也跟着 × k → 物理一致，不会穿地
//      · Body 根上的 BoxCollider2D 也 × k → 人整体抬高，脚仍然踩在地面上
//      · 穿戴物（Wearable 把贴图 SetParent 到肢体）自动跟着变大，不会错位
//  游戏自己的 `HandleVisuals` 里就有一行 `visualBodyOffset * transform.localScale`
//  —— 说明原版本来就考虑过"身体被缩放过"这种情况，这条路是安全的。
//
//  ---------------------------------------------------------------------------
//  ★ 1.70m 是怎么换算出来的
//  ---------------------------------------------------------------------------
//  原版给角色随机身高的地方（`PlayerCamera.Start`，仅新开局）：
//      WoundView.SetCharDetails(Random.Range(130, 161) * body.heightMultiplier, ...)
//  然后健康面板显示成 `"{height}cm | {age}y | #{id}"`（`WoundView.SetCharDetails`）。
//  三个角色的 `heightMultiplier`：EXPie 1.0 / Milky 0.8 / 第三位 1.1。
//  也就是说**原版 EXPie 的"官方身高"是 130~161cm（中值 145.5）**，而这个数字
//  和画面上的像素高度是同一套美术（三套体型共用同一副骨架，靠 heightMultiplier 撑间距）。
//
//  我们的角色是固定 1.70m，所以：
//      k = 170 / 145.5 ≈ 1.168
//  同时把档案身高直接写成 170cm（`cInfo[0]`，健康面板/存档里的 cHeight 都吃这个），
//  这样"面板写的 170"和"画面看着像 170"是一致的。
//
//  ⚠ 为什么不用"读当前 cHeight 再除"的写法：cHeight 会被存进存档（`SaveInfo.cHeight`），
//    如果我们把 170 写回去，读档时再拿 170 当基准就会算出 k=1.0 → 体型缩回原版。
//    所以基准用**原版常量**，和存档无关。
//
//  ⚠ 朝向：`Body.SwitchDir` 翻转左右靠的是 `transform.localScale.x = -x`
//    （它每帧还会把 y / z 写回 1）。所以这里必须**只改绝对值、保留符号**，
//    否则每次转身都会被我们掰回来（或者我们把朝向又翻回去）。
// ============================================================================

public static class BodyScale
{
    public static bool Enabled = true;

    // ---- 设定数据 ----
    public static float HeightCm = 170f;              // 设定身高（原版 EXPie 是 130~161 随机）
    public const float VanillaHeightCm = 145.5f;      // Random.Range(130,161) × heightMultiplier(1.0) 的中值
    public static float Scale = HeightCm / VanillaHeightCm;   // ≈ 1.168

    private static int _appliedFor;                   // 已经处理过的 body 实例（换局/读档会换实例）
    private static int _heightSetFor;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Body", "ScaleEnabled", true,
                "把主角缩放到设定身高（整体等比：骨架、贴图、碰撞箱、关节一起变）").Value;

            HeightCm = Mathf.Clamp(plugin.Config.Bind("Body", "HeightCm", 170f,
                "设定身高（cm）。原版 EXPie 是 Random.Range(130,161) 随机，这里默认按设定固定 1.70m").Value,
                100f, 250f);

            Scale = Mathf.Clamp(HeightCm / VanillaHeightCm, 0.5f, 1.9f);

            Plugin.Log.LogInfo($"[STEMFishDLC] 体型: {(Enabled ? $"{HeightCm:0}cm → ×{Scale:0.000}" +
                $"（原版 EXPie 基准 {VanillaHeightCm:0.#}cm）" : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 体型配置读取失败: {e.Message}");
        }
    }

    public static void Tick()
    {
        if (!Enabled) return;

        Body body = null;
        try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
        if (body == null) return;

        ApplyScale(body);
        ApplyProfileHeight(body);
    }

    // ---------------------------------------------------------------------
    //  ① 骨架等比缩放
    // ---------------------------------------------------------------------
    private static void ApplyScale(Body body)
    {
        if (Scale <= 0.001f) return;
        try
        {
            var t = body.transform;
            var s = t.localScale;
            float sign = s.x < 0f ? -1f : 1f;          // 朝向（SwitchDir 用 ±x 表示）

            // 已经是目标尺寸就不动（每帧比较开销极小，且能自动纠正被 SwitchDir 写回的 y）
            if (Mathf.Abs(Mathf.Abs(s.x) - Scale) < 0.0005f && Mathf.Abs(s.y - Scale) < 0.0005f) return;

            t.localScale = new Vector3(sign * Scale, Scale, 1f);

            int id = body.GetInstanceID();
            if (_appliedFor != id)
            {
                _appliedFor = id;
                Plugin.Log.LogInfo($"[STEMFishDLC] 主角体型已按 {HeightCm:0}cm 等比缩放 ×{Scale:0.000}" +
                                   $"（骨架 / 贴图 / 碰撞箱同步；原尺寸 {Mathf.Abs(s.x):0.00}）");
            }
        }
        catch (Exception e) { WarnOnce(e.Message); }
    }

    // ---------------------------------------------------------------------
    //  ② 档案身高固定成设定值（健康面板 `{height}cm | {age}y | #id`）
    //  ---------------------------------------------------------------------
    //  cInfo = [height, age, id, ver]；改完要重新调 SetCharDetails 才会刷新文字。
    //  存进存档的是 SaveInfo.cHeight（读档时再经由 SetCharDetails 写回来），
    //  所以这里改过之后，读档也会保持 170。
    // ---------------------------------------------------------------------
    private static void ApplyProfileHeight(Body body)
    {
        try
        {
            var v = WoundView.view;
            if (v == null || v.cInfo == null || v.cInfo.Length < 4) return;

            int id = body.GetInstanceID();
            if (_heightSetFor == id && v.cInfo[0] == Mathf.RoundToInt(HeightCm)) return;

            int want = Mathf.RoundToInt(HeightCm);
            if (v.cInfo[0] != want)
            {
                v.cInfo[0] = want;
                v.SetCharDetails(v.cInfo[0], v.cInfo[1], v.cInfo[2], v.cInfo[3]);
                Plugin.Log.LogInfo($"[STEMFishDLC] 档案身高固定为 {want}cm（健康面板 / 存档 cHeight）");
            }
            _heightSetFor = id;
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
