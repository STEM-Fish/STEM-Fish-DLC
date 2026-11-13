using System;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  BodyScale - 把主角整体等比放大（身高变高，但不是纵向拉伸）
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
//  ⇒ 直接缩放 `Body.transform.localScale` 就是完美的等比放大：
//      · 各肢体 localPosition × k        → 骨架撑开（身高↑，肩宽也↑）
//      · 肢体的 SpriteRenderer 跟着 × k  → 贴图同比变大（不是拉伸）
//      · 肢体的 Collider2D / HingeJoint2D 也跟着 × k → 物理一致，不会穿地
//      · Body 根上的 BoxCollider2D 也 × k → 人整体抬高，脚仍然踩在地面上
//      · 穿戴物（Wearable 把贴图 SetParent 到肢体）自动跟着变大，不会错位
//  游戏自己的 `HandleVisuals` 里就有一行 `visualBodyOffset * transform.localScale`
//  —— 说明原版本来就考虑过"身体被缩放过"这种情况，这条路是安全的。
//
//  ⚠ 只放大玩家自己：Body.HandlePeriodicChecks 之类是全 Body 共用的，
//    所以这里按 PlayerCamera.main.body 认人；敌人 / 动物一律不动。
//
//  ⚠ 朝向：`Body.SwitchDir` 翻转左右靠的是 `transform.localScale.x = -x`
//    （它每帧还会把 y / z 写回 1）。所以这里必须**只改绝对值、保留符号**，
//    否则每次转身都会被我们掰回来（或者我们把朝向又翻回去）。
//    写入时机在 onBeforeRender 之前（EngineTick 的泵），所以那一帧看不出异常。
// ============================================================================

public static class BodyScale
{
    public static bool Enabled = true;
    public static float Scale = 1.15f;          // 1.0 = 原版大小

    private static int _appliedFor;             // 已经缩放过的 body 实例（换局/读档会换实例）

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Body", "ScaleEnabled", true,
                "把主角整体等比放大（身高、肩宽、贴图、碰撞箱一起放大，不是只拉高）").Value;

            float v = plugin.Config.Bind("Body", "HeightScale", 1.15f,
                "主角体型倍率。1.0 = 原版大小；1.15 ≈ 高一头；上限 1.6（再大容易卡窄洞）").Value;
            Scale = Mathf.Clamp(v, 1.0f, 1.6f);

            if (Mathf.Abs(Scale - v) > 0.001f)
                Plugin.Log.LogWarning($"[STEMFishDLC] HeightScale={v} 超出 1.0~1.6，已夹到 {Scale}");

            Plugin.Log.LogInfo($"[STEMFishDLC] 体型缩放: {(Enabled ? $"×{Scale:0.00}" : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 体型配置读取失败: {e.Message}");
        }
    }

    public static void Tick()
    {
        if (!Enabled) return;
        if (Scale <= 1.001f) return;

        Body body = null;
        try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
        if (body == null) return;

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
                Plugin.Log.LogInfo($"[STEMFishDLC] 主角体型已等比放大 ×{Scale:0.00}" +
                                   $"（骨架 / 贴图 / 碰撞箱同步；原尺寸 {Mathf.Abs(s.x):0.00}）");
            }
        }
        catch (Exception e)
        {
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                Plugin.Log.LogWarning($"[STEMFishDLC] 体型缩放失败: {e.Message}");
            }
        }
    }
    private static string _lastError;
}
