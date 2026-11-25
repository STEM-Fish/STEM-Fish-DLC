using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  CrateScatter - 把弹药箱撒到地图上（每一层撒一次）
//  ---------------------------------------------------------------------------
//  为什么要自己撒：反汇编确认过，世界里的"物资箱"用的是
//    BuildingEntity.Update → itemCategoriesToAdd → ItemLootPool.pool[category]
//  而 ItemLootPool.pool 只被 `Item.SetupItems` 建一次（按 ItemInfo.category 分组），
//  并且 **唯一消费它的是商人 GenerateSingleItemList** + BuildingEntity 的掉落表。
//  换句话说：往池子里塞东西只会影响"商人卖什么 / 打碎箱子掉什么"，
//  并不会让物资箱里多出我们的物品 —— 上一版"完全不会自然生成"就是这个原因。
//
//  这里改成直白的做法：**边探索边撒**。换层后每隔几秒试着在玩家附近找一个
//  合法落点放一个，直到本层配额用完 —— 这样弹药箱会自然分布在玩家走过的区域，
//  而不是全部堆在出生点（也不可能"一次性撒整张图"：没实例化的区块放下去会掉进虚空）。
//
//  ★ v0.15.2 修了"一个都不生成"：
//    旧版是在**整张地图范围**随机取点，然后 `OverlapCircle(pos, 0.5)` +
//    `Raycast(down, 4)` 判合法。地下绝大部分体积是实心岩层 → 随机点几乎全在墙里，
//    偶尔落在空洞又大多离地 >4 → 两条判定全灭，日志里一直是 0/14。
//    现在统一走 `SpotFinder`（向下搜 12 单位、落在射线命中点），和原版
//    `DistributeEntities` 的做法一致。
// ============================================================================

public static class CrateScatter
{
    public static bool Enabled = true;
    public static int CountPerLayer = 14;      // 每层撒几个
    public static float MinSpacing = 10f;      // 两个弹药箱之间的最小距离
    public static float Margin = 8f;           // 离地图边缘留多少

    private static WorldGeneration _world;
    private static int _depth = -9999;
    private static float _wait = -1f;
    private static bool _done;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Loot", "ScatterAmmoBoxes", true,
                "每层在世界里撒几个弹药箱（关掉就只能靠打碎物资箱/商人碰运气）").Value;
            var ammoEntry = plugin.Config.Bind("Loot", "AmmoBoxPerLayer", 17,
                "每层撒多少个弹药箱（0 = 不撒，上限 60）");
            CountPerLayer = Mathf.Clamp(ammoEntry.Value, 0, 60);

            // v0.18.11：默认 14 → 17（老 cfg 里存的 14 一次性迁移）
            try
            {
                var mk = plugin.Config.Bind("Loot", "AmmoCountMigrated", false,
                    "内部标记：弹药箱每层数量是否已从 14 升到 17，别手动改");
                if (!mk.Value && CountPerLayer == 14)
                {
                    ammoEntry.Value = 17;
                    CountPerLayer = 17;
                    Plugin.Log.LogInfo("[STEMFishDLC] 弹药箱每层数量：14 → 17（一次性迁移）");
                }
                mk.Value = true;
            }
            catch { }
            MinSpacing = Mathf.Clamp(plugin.Config.Bind("Loot", "AmmoBoxMinSpacing", 10f,
                "两个弹药箱之间的最小距离（世界单位），防止挤成一堆").Value, 2f, 40f);
            Margin = Mathf.Clamp(plugin.Config.Bind("Loot", "AmmoBoxMargin", 8f,
                "离地图边缘留多少距离，太靠边容易卡在边界里").Value, 2f, 40f);

            // 旧键（v0.15.2 的"离玩家最近/最远距离"，现在改成整层一次性撒，没用了）
            try
            {
                plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Loot", "AmmoBoxMinDist"));
                plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Loot", "AmmoBoxMaxDist"));
                plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Loot", "AmmoBoxDropSearch"));
            }
            catch { }

            Plugin.Log.LogInfo($"[STEMFishDLC] 弹药箱投放: {(Enabled ? $"每层 {CountPerLayer} 个（整层一次性撒好）" : "关闭")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 弹药箱投放配置读取失败: {e.Message}");
        }
    }

    public static void Tick()
    {
        if (!Enabled || CountPerLayer <= 0) return;

        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }
        if (w == null) { _world = null; _depth = -9999; _done = false; return; }

        // 教程保持原版：不往教学关里撒弹药箱
        if (CharacterSelect.IsTutorial()) return;

        // 弹药箱也是 SF-07 的补给，原版角色不刷
        if (CharacterSelect.Current != CharacterSelect.Sf07) return;

        var pc = PlayerCamera.main;
        if (pc == null || pc.body == null) return;      // 等玩家出生（世界生成完成的天然信号）
        try { if (!w.worldExists) return; } catch { return; }

        int depth;
        try { depth = w.biomeDepth; } catch { return; }

        // 换层：等 2 秒（方块数据就绪 + 免得和世界生成抢资源），然后**一次性撒完整层**
        if (w != _world || depth != _depth)
        {
            _world = w;
            _depth = depth;
            _done = false;
            _wait = 2f;
            return;
        }

        if (_done) return;

        _wait -= Time.deltaTime;
        if (_wait > 0f) return;
        _done = true;

        ScatterAll(w, depth);
    }

    // ==========================================================================
    //  整层一次性撒
    //  ---------------------------------------------------------------------------
    //  ★ 为什么不再"边探索边撒"：那样 14 个会在开局 35 秒内全部堆在出生点附近，
    //    往下走就一个都遇不到（作者实测）。改成开局按**方块坐标**整层随机撒，
    //    和原版物资箱一样是"地图生成时就摆好"的。
    //    · 只用方块数组判合法，不依赖物理 —— 那些区块可能还没实例化；
    //    · 物品刚生成时先冻结几秒，等碰撞体就绪再放开，避免穿过地板掉下去。
    // ==========================================================================
    private static void ScatterAll(WorldGeneration w, int depth)
    {
        int want = Mathf.Clamp(CountPerLayer, 0, 60);
        var spots = new System.Collections.Generic.List<Vector2>();
        Vector2 near = Vector2.zero;
        try { near = PlayerCamera.main.body.transform.position; } catch { }
        int ok = 0;
        int tries = want * 60;

        SpotFinder.ResetStats();

        for (int i = 0; i < tries && ok < want; i++)
        {
            Vector2 pos;
            if (!SpotFinder.FindBlockFloor(w, 1, Margin, out pos)) continue;
            if (TooClose(spots, pos)) continue;
            if ((pos - near).sqrMagnitude < 100f) continue;    // 别在脚边凭空出现（10 单位内不撒）

            try
            {
                var it = ItemRegistry.Create(AmmoBox.BoxId, pos, UnityEngine.Random.Range(0f, 360f));
                if (it == null) continue;
                FreezeOnSpawn.Attach(it.gameObject, 4f);
            }
            catch { continue; }

            spots.Add(pos);
            ok++;
        }

        Plugin.Log.LogInfo($"[STEMFishDLC] 弹药箱投放：第 {depth} 层 {ok}/{want} 个" +
                           (ok == 0 ? "（没找到合法落点）" : ""));
        Plugin.Log.LogInfo($"[STEMFishDLC] 落点统计：{SpotFinder.StatsText()}");
    }

    private static bool TooClose(System.Collections.Generic.List<Vector2> spots, Vector2 p)
    {
        float r2 = MinSpacing * MinSpacing;
        for (int i = 0; i < spots.Count; i++)
            if ((spots[i] - p).sqrMagnitude < r2) return true;
        return false;
    }
}
