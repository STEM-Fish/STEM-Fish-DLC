using System;
using System.Collections.Generic;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  EntityWatchdog - 「血量已经负了但还活着」的看门狗
//  ---------------------------------------------------------------------------
//  为什么需要：原版所有可破坏物（箱子 / 动物 / 敌人）的死亡判定都只有一处 ——
//  `BuildingEntity.Update` 开头的 `if (health < 0.5f) { 碎裂 }`。也就是说
//  **只要 Update 没跑，血量再负也不会死**。
//  而 Update 不跑只有三种可能：GameObject 被 SetActive(false)、组件 enabled=false、
//  或者 timeScale=0。这三种都不是我们干的（我们对 BuildingEntity 只挂了两个
//  **只读**的 prefix：HypeMode 记击杀、LuckyBlock 拦自己的方块）。
//
//  所以这里每隔几秒扫一遍场上的 BuildingEntity：
//    · 血量 < 0.5 且连着两次扫描都还在 → 记一条日志（带 activeSelf / enabled / 距离）
//    · 不主动杀掉它 —— 万一是原版的正常中间态，别把箱子/剧情物件弄坏
//  这样下次复现时日志能直接告诉我们"到底是谁把它冻住了"。
// ============================================================================

public static class EntityWatchdog
{
    public static bool Enabled = true;
    private const float ScanInterval = 2f;
    private const float StuckAfter = 4f;        // 连续这么久还没死才报

    private static float _timer;
    private static readonly Dictionary<int, float> _seen = new Dictionary<int, float>();
    private static readonly HashSet<int> _reported = new HashSet<int>();

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("General", "EntityWatchdog", true,
                "诊断：发现「血量已负但没死」的实体时写一条日志（不主动杀，只记录）").Value;
        }
        catch { }
    }

    public static void Tick()
    {
        if (!Enabled) return;

        _timer -= Time.unscaledDeltaTime;
        if (_timer > 0f) return;
        _timer = ScanInterval;

        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<BuildingEntity>();
            if (all == null) return;

            var alive = new HashSet<int>();
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;

                int key = b.GetInstanceID();
                alive.Add(key);

                if (b.health >= 0.5f) { _seen.Remove(key); _reported.Remove(key); continue; }
                if (b.id == LuckyBlock.Id) continue;      // 我们自己的方块由 prefix 接管

                float first;
                if (!_seen.TryGetValue(key, out first)) { _seen[key] = Time.unscaledTime; continue; }

                if (Time.unscaledTime - first < StuckAfter) continue;
                if (!_reported.Add(key)) continue;

                string where = "?";
                float dist = -1f;
                try
                {
                    var pos = b.transform.position;
                    var pc = PlayerCamera.main;
                    if (pc != null && pc.body != null) dist = Vector2.Distance(pos, pc.body.transform.position);
                    where = pos.ToString();
                }
                catch { }

                Plugin.Log.LogWarning(
                    $"[STEMFishDLC] 异常实体：id='{b.id}' health={b.health:0.##} " +
                    $"activeSelf={b.gameObject.activeSelf} activeInHierarchy={b.gameObject.activeInHierarchy} " +
                    $"enabled={b.enabled} timeScale={Time.timeScale:0.##} dist={(dist >= 0f ? dist.ToString("0.#") : "?")} pos={where}");
            }

            // 清掉已经销毁/恢复的记录
            if (_seen.Count > 64 || _reported.Count > 64)
            {
                var dead = new List<int>();
                foreach (var kv in _seen) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (var k in dead) { _seen.Remove(k); _reported.Remove(k); }
            }
        }
        catch { }
    }
}
