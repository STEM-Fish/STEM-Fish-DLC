using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  ItemSettle - 把"拿不到"的自定义物品挪回能用脚踩到的地方
//  ---------------------------------------------------------------------------
//  现象：尸体旁边刷出来的弹药箱，经常右键没反应（拾取提示都不出）。
//
//  原因（反汇编 `CorpseScript.Start` + `Body.DoPickupCheck` 对出来的）：
//    ① 尸体掉落是 `Instantiate(Resources.Load(id), 尸体位置 + (随机x, +3))`
//       —— 统一往上抛 3 格。洞穴经常只有 3~4 格高，箱子直接生成在**岩层里**；
//    ② 我们的 `ItemRegistry.GetLoadPrefab` 为了让读档模板别乱掉，把模板的
//       `Rigidbody2D.simulated` 设成 false 再销毁。原版 `Instantiate` 会**连这个字段一起克隆**
//       → 尸体里掉出来的箱子继承 `simulated = false`，永远悬在半空，不会落地。
//    而 `Body.DoPickupCheck` 有两条硬判据：
//        · 玩家 → 物品的 Linecast 不能撞到 Ground（隔墙拿不到）
//        · 物品碰撞体中心 `OverlapPoint(Ground)` 必须为空（**埋在方块里就拿不到**）
//  于是"嵌在岩层里的箱子"和"卡在天花板里的箱子"都变成拿不到。
//
//  这里每 1.5 秒扫一遍世界里的自定义战利品（弹药箱 / 屏蔽器）：
//    · 中心在实心方块里 → 往上找到第一个空格，挪上去
//    · 还冻着（simulated = false）→ 放开，让它自己落地
//  只碰"世界里的、不是手持/容器里/穿戴中"的实例，其它一概不碰。
// ============================================================================

public static class ItemSettle
{
    public static bool Enabled = true;

    private static float _next;
    private const float Interval = 1.5f;
    private static int _lastLifted = -1;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("General", "SettleLootItems", true,
                "把掉进岩层/悬在半空的自定义物品挪回地面（尸体旁拿不到的弹药箱）").Value;
            Plugin.Log.LogInfo($"[STEMFishDLC] 战利品落点修补：{(Enabled ? "开" : "关")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 战利品落点修补配置读取失败: {e.Message}");
        }
    }

    private static bool IsOurLoot(string id)
        => id == AmmoBox.BoxId || id == Jammer.Id;

    public static void Tick()
    {
        if (!Enabled) return;
        if (Time.realtimeSinceStartup < _next) return;
        _next = Time.realtimeSinceStartup + Interval;

        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }
        if (w == null) return;

        var all = Item.allItems;
        if (all == null) return;

        int lifted = 0, thawed = 0;

        for (int i = 0; i < all.Count; i++)
        {
            var it = all[i];
            if (it == null) continue;

            string id = null;
            try { id = it.id; } catch { }
            if (!IsOurLoot(id)) continue;

            Transform tr = null;
            try { tr = it.transform; } catch { }
            if (tr == null) continue;
            if (tr.parent != null) continue;                 // 手持 / 容器内 / 已穿戴

            try { if (it.container != null) continue; } catch { }

            var rb = it.GetComponent<Rigidbody2D>();
            var pos = (Vector2)tr.position;

            // ① 埋在方块里 → 往上挪到第一个空格
            if (IsSolid(w, pos))
            {
                if (Lift(w, tr, pos))
                {
                    lifted++;
                    if (rb != null) { rb.simulated = true; rb.velocity = Vector2.zero; rb.angularVelocity = 0f; }
                }
                continue;
            }

            // ② 还冻着（读档模板克隆出来的 simulated=false）→ 放开
            if (rb != null && !rb.simulated)
            {
                rb.simulated = true;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                thawed++;
            }
        }

        if (lifted > 0 || thawed > 0)
        {
            int total = lifted + thawed;
            if (_lastLifted != total)
            {
                _lastLifted = total;
                Plugin.Log.LogInfo($"[STEMFishDLC] 战利品落点修补：挪出岩层 {lifted} 个 · 解冻 {thawed} 个");
            }
        }
    }

    private static bool IsSolid(WorldGeneration w, Vector2 p)
    {
        try { return w.GetBlock(p) >= 3; } catch { return false; }
    }

    // 从当前位置往上找第一个"不在方块里"的点（最多 12 格），放上去
    private static bool Lift(WorldGeneration w, Transform tr, Vector2 from)
    {
        try
        {
            for (float dy = 0.5f; dy <= 12f; dy += 0.5f)
            {
                var c = from + new Vector2(0f, dy);
                if (IsSolid(w, c)) continue;

                tr.position = new Vector3(c.x, c.y + 0.15f, tr.position.z);
                return true;
            }
        }
        catch { }
        return false;
    }
}
