using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SpotFinder - 在玩家周围找一个"能放东西"的落点
//  ---------------------------------------------------------------------------
//  为什么要有这个文件：v0.15.1 之前有三份几乎一样的落点搜索（SpawnDirector /
//  CrateScatter / SpikeCrawler / LuckyBlock），但只有 SpawnDirector 那份偶尔能用，
//  其它全都在"往下 raycast 4 单位"这一步就死掉了：
//
//    地下大部分地方是**实心岩层**，随机点在墙里 → OverlapCircle 直接判定失败；
//    而地表/大空洞是**开阔空气**，随机点在离地 5~10 单位的高处 → raycast 4
//    根本够不到地面。两种情况都被判成"非法"，于是日志里全是 0/14、0/10、0/2。
//
//  原版 `WorldGeneration.DistributeEntities` 是这么做的（反汇编确认）：
//    ① 整层随机取点；
//    ② `Physics2D.OverlapPoint(pos, LayerMask.GetMask("Ground")) == null`
//       —— 只看 Ground 层，而且只是"点"，不是半径 0.5 的圆；
//    ③ 从该点向下 raycast 最多 **CHUNKSIZE**（一整块区块）去找地面；
//    ④ 落点用**射线命中点**，不是原始随机点。
//
//  这里再稳一层：用**方块数组**判空气/实心（`GetBlock < 3` 是空气，和原版
//  放置检查里的判断完全一致），物理只用来找"脚下有没有地"。方块数据不受
//  "区块还没实例化"影响，比 OverlapCircle 靠谱得多。
// ============================================================================

public static class SpotFinder
{
    private static int _groundMask = -1;
    private static bool _maskLogged;

    // 原版 DistributeEntities 只对 "Ground" 层做射线（其它层是实体/触发器，
    // 落到它们头上会把东西放在半空）。层名取不到时退回"所有层"。
    private static int GroundMask
    {
        get
        {
            if (_groundMask < 0)
            {
                try { _groundMask = LayerMask.GetMask("Ground"); } catch { _groundMask = 0; }
            }
            if (!_maskLogged)
            {
                _maskLogged = true;
                Plugin.Log.LogInfo($"[STEMFishDLC] 落点搜索：Ground 层 mask={_groundMask}" +
                                   (_groundMask == 0 ? "（取不到，退回所有层）" : ""));
            }
            return _groundMask;
        }
    }

    /// <summary>
    /// 在以 origin 为中心的环带里找一个落点。
    /// </summary>
    /// <param name="minDist">最近距离</param>
    /// <param name="maxDist">最远距离</param>
    /// <param name="attempts">尝试次数</param>
    /// <param name="pos">找到的落点（脚底）</param>
    /// <param name="dropSearch">向下找地面的最大距离</param>
    public static bool Find(Vector3 origin, float minDist, float maxDist, int attempts,
                            out Vector2 pos, float dropSearch = 12f)
    {
        pos = origin;
        if (maxDist <= minDist) maxDist = minDist + 1f;

        // 先尽量挑"镜头外"的点（别让玩家看见东西凭空出现），
        // 挑不到再放宽 —— 宁可在视野边缘出现，也不能一个都不生成。
        if (Pass(origin, minDist, maxDist, attempts, dropSearch, true, out pos)) return true;
        return Pass(origin, minDist, maxDist, attempts, dropSearch, false, out pos);
    }

    private static bool Pass(Vector3 origin, float minDist, float maxDist, int attempts,
                             float dropSearch, bool avoidCamera, out Vector2 pos)
    {
        pos = origin;
        int gm = GroundMask;
        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }

        for (int i = 0; i < attempts; i++)
        {
            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float dist = UnityEngine.Random.Range(minDist, maxDist);
            var p = (Vector2)origin + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;

            // ① 起点必须是空气（方块数组，< 3 = 空气）
            if (w != null && BlockAt(w, p) >= 3) continue;

            // ② 向下找地面（长距离，地表的大空洞也能找到）
            RaycastHit2D hit = default;
            try
            {
                hit = gm != 0 ? Physics2D.Raycast(p, Vector2.down, dropSearch, gm)
                              : Physics2D.Raycast(p, Vector2.down, dropSearch);
            }
            catch { }
            if (hit.collider == null) continue;

            // ③ 落在命中点上方一点
            var spot = hit.point + new Vector2(0f, 0.55f);
            if (w != null && BlockAt(w, spot) >= 3) continue;

            // ④ 落点不能压在别的实体上（箱子/机器/人）；压在刚命中的地面上是正常的
            Collider2D c2 = null;
            try { c2 = Physics2D.OverlapCircle(spot, 0.30f); } catch { }
            if (c2 != null && c2 != hit.collider) continue;

            // ⑤ 别落到地图边界外
            if (w != null)
            {
                try
                {
                    if (Mathf.Abs(spot.x) > (float)w.halfWidth - 2f) continue;
                    if (Mathf.Abs(spot.y) > (float)w.halfHeight - 2f) continue;
                }
                catch { }
            }

            // ⑥ 不要在镜头里凭空出现
            if (avoidCamera && InView(spot)) continue;

            pos = spot;
            return true;
        }
        return false;
    }

    private static bool InView(Vector2 world)
    {
        try
        {
            var cam = Camera.main;
            if (cam == null) return false;
            var vp = cam.WorldToViewportPoint(world);
            if (vp.z <= 0f) return false;
            const float m = -0.02f;
            return vp.x > m && vp.x < 1f - m && vp.y > m && vp.y < 1f - m;
        }
        catch { return false; }
    }

    private static ushort BlockAt(WorldGeneration w, Vector2 p)
    {
        try { return w.GetBlock(p); } catch { return 0; }   // 取不到就当空气，交给物理判定
    }

    // ==========================================================================
    //  整层撒点（用于"地图加载时一起刷好"）
    //  ---------------------------------------------------------------------------
    //  只用**方块数组**判定，不做物理查询 —— 因为要在玩家还没走到那些区块时
    //  就把东西放好，这时候那些地方可能连碰撞体都还没实例化。
    //  判据和原版放置检查一致：这一格是空气（< 3）、脚下一格是实心、头顶一格是空气。
    //
    //  坐标换算：1 方块 = 1 世界单位；方块中心 = index - half + 0.5
    //  （和 WorldGeneration.BlockToWorldPos 完全一致）。
    // ==========================================================================
    public static bool FindBlockFloor(WorldGeneration w, float margin, out Vector2 pos)
        => FindBlockFloor(w, 1, margin, out pos);

    public static bool FindBlockFloor(WorldGeneration w, int attempts, float margin, out Vector2 pos)
    {
        pos = default;
        if (w == null) return false;

        float hw, hh;
        try { hw = w.halfWidth; hh = w.halfHeight; } catch { return false; }
        if (hw < 8f || hh < 8f) return false;

        float minX = -hw + margin, maxX = hw - margin;
        float minY = -hh + margin, maxY = hh - margin;

        for (int i = 0; i < attempts; i++)
        {
            float x = UnityEngine.Random.Range(minX, maxX);
            float y = UnityEngine.Random.Range(minY, maxY);
            if (TryCell(w, hw, hh, x, y, out pos)) return true;
        }
        return false;
    }

    // ---- 诊断计数：万一又是 0 个，日志能直接指出是哪一条判据卡的 ----
    public static int StatTried, StatSolid, StatNoFloor, StatNoHead, StatOk;

    public static void ResetStats() => StatTried = StatSolid = StatNoFloor = StatNoHead = StatOk = 0;

    public static string StatsText()
        => $"采样 {StatTried} · 实心 {StatSolid} · 没地板 {StatNoFloor} · 头顶挤 {StatNoHead} · 可用 {StatOk}";

    private static bool TryCell(WorldGeneration w, float hw, float hh, float x, float y, out Vector2 pos)
    {
        pos = default;
        StatTried++;

        int bx = Mathf.FloorToInt(x + hw);
        int by = Mathf.FloorToInt(y + hh);
        var center = new Vector2(bx - hw + 0.5f, by - hh + 0.5f);

        if (BlockAt(w, center) >= 3) { StatSolid++; return false; }   // 本格实心 → 换点

        // ★ v0.16.2：采样点常常悬在半空（地表层大半是天空），以前直接判"脚下不是地"，
        //   结果 840 次采样里 828 次被这么废掉（日志实证）。现在**向下扫**最多 14 格找落点。
        for (int d = 0; d < 14; d++)
        {
            var cell = center + new Vector2(0f, -d);
            if (BlockAt(w, cell) >= 3) break;                        // 钻进实心，说明这列没空腔
            if (BlockAt(w, cell + new Vector2(0f, -1f)) < 3) continue;   // 还没到底
            if (BlockAt(w, cell + new Vector2(0f, 1f)) >= 3) continue;   // 只有一格高，头顶太挤

            // 软过滤：区块已经实例化时，别压在箱子/机器上；没实例化时这里是 null，直接放过
            try
            {
                var c = Physics2D.OverlapCircle(cell, 0.30f);
                if (c != null && c.GetComponentInParent<BuildingEntity>() != null &&
                    c.GetComponentInParent<Item>() == null) continue;
            }
            catch { }

            StatOk++;
            pos = cell + new Vector2(0f, 0.05f);
            return true;
        }

        StatNoFloor++;
        return false;
    }
}
