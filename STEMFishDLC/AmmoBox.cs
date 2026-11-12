using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  AmmoBox - 弹药箱（世界里的"使用型"消耗品，不是容器）
//  ---------------------------------------------------------------------------
//  行为对齐原版 foodbox：**右键使用 → 原地倒出一堆弹药 → 箱子消失**。
//    · 物品定义见 ItemRegistry：template=medkit、stripContainer=true、
//      useAction=AmmoBox.Use（注册时把 ItemInfo 设成 usable + onlyHoldInHands）
//    · 模板 medkit 自带的 Container 会在 Create 里被 DestroyImmediate 摘掉，
//      所以拖起来不会再弹出容器界面
//    · 外观是 textures/ammobox.png（24×16 军绿铁皮箱）
//
//  刷新：
//    ① `CrateScatter` 负责真正的地图生成（每层撒 N 个，见那个文件）
//    ② 这里额外把 id 塞进 ItemLootPool.pool，让它也能出现在**商人货架**和
//       **打碎物资箱的掉落表**里（BuildingEntity.itemCategoriesToAdd → pool[category]）。
//       ⚠ 池子**不会**让物资箱里多出物品 —— 那个池只被
//       TraderScript.GenerateSingleItemList 和 BuildingEntity 的掉落逻辑消费，
//       上一版"完全不会自然生成"就是因为误以为它是容器内容表。
//
//  ⚠ 上一版是"容器"（克隆 medkit 保留 Container + 延迟填充）。那条路已废弃：
//    Container 组件的物品必须拖起来松手才打开，和 foodbox 的右键直觉不一致。
// ============================================================================

public static class AmmoBox
{
    public const string BoxId = "ammobox";

    // 战利品池里的权重（重复次数）
    public static int LootWeight = 10;

    private static bool _poolInjected;

    public static void Init(Plugin plugin)
    {
        try
        {
            LootWeight = Mathf.Clamp(plugin.Config.Bind("Loot", "AmmoBoxWeight", 10,
                "弹药箱在容器里的出现权重（1=和普通物品一样，10≈大幅提高，上限 30）").Value, 1, 30);
            // v0.18.9：原版弹药箱容器注入已按作者要求移除（只保留我们自己撒的弹药箱）
            try
            {
                plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Loot", "Mdrz9MagInCrates"));
                plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Loot", "Mdrz9MagPerCrateMin"));
                plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Loot", "Mdrz9MagPerCrateMax"));
            }
            catch { }

            Plugin.Log.LogInfo($"[STEMFishDLC] 弹药箱就绪（使用型，掉落权重 {LootWeight}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 弹药箱配置读取失败: {e.Message}");
        }
    }

    // 战利品池注入（必须在 ItemLootPool.InitializePool 之后；用 Tick 懒执行）
    // 同时兼管右键输入 —— 原版没有任何右键动作，这里不会跟游戏抢键位。
    public static void Tick()
    {
        HandleRightClick();
        if (_poolInjected) return;
        try
        {
            var pool = ItemLootPool.pool;
            if (pool == null || pool.Count == 0) return;      // 还没初始化

            int added = 0;
            foreach (var key in new[] { "container", "medical", "utility", "military" })
            {
                if (!pool.TryGetValue(key, out var list) || list == null) continue;
                if (Weight(list)) added++;
            }

            // 一个都没塞进去（池子名字和预期不同）→ 兜底：塞进"元素最多"的那个池
            if (added == 0)
            {
                string bestKey = null;
                int bestCount = 0;
                foreach (var kv in pool)
                    if (kv.Value != null && kv.Value.Count > bestCount) { bestCount = kv.Value.Count; bestKey = kv.Key; }
                if (bestKey != null)
                {
                    Weight(pool[bestKey]);
                    added = 1;
                    Plugin.Log.LogInfo($"[STEMFishDLC] 弹药箱已注入战利品池 '{bestKey}'（{bestCount} 项，权重 {LootWeight}）");
                }
            }
            else
            {
                Plugin.Log.LogInfo($"[STEMFishDLC] 弹药箱已注入 {added} 个战利品池（每个重复 {LootWeight} 次）");
            }

            if (added > 0) _poolInjected = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 弹药箱池注入失败: {e.Message}");
            _poolInjected = true;                    // 不重复刷日志
        }
    }

    private static bool Weight(System.Collections.Generic.List<string> list)
    {
        if (list == null) return false;
        while (list.Remove(BoxId)) { }
        for (int i = 0; i < LootWeight; i++) list.Add(BoxId);
        return true;
    }

    // ==========================================================================
    //  右键：直接打开手里的弹药箱
    //  ---------------------------------------------------------------------------
    //  反汇编确认：原版**没有任何右键动作**（全程序集只有 AdaptiveButton 用
    //  Input.GetMouseButton，而且只用了左键）。usable 物品的标准用法是
    //  「拖到玩家身上松手」→ PlayerCamera.TryPerformRadialAction → Body.UseItem。
    //  那条路保留着（useAction 已注册），这里额外补一个更顺手的右键。
    //
    //  TickAll 可能一帧被多个驱动源调用，用 frameCount 去重，否则会连开两次。
    // ==========================================================================
    private static int _lastClickFrame = -1;

    private static void HandleRightClick()
    {
        try
        {
            if (!Input.GetMouseButtonDown(1)) return;
            if (_lastClickFrame == Time.frameCount) return;
            _lastClickFrame = Time.frameCount;

            var pc = PlayerCamera.main;
            if (pc == null || pc.body == null) return;
            if (Plugin.IsPaused) return;
            if (pc.dragItem != null) return;          // 正在拖东西 → 右键是取消，别抢

            var body = pc.body;
            var item = body.GetItem(0);               // 主手
            if (item == null || item.id != BoxId) return;

            Use(body, item);
        }
        catch { }
    }

    // ==========================================================================
    //  右键使用：原地倒出弹药，箱子消失
    //  ---------------------------------------------------------------------------
    //  签名必须匹配 ItemInfo.Use（void (Body, Item)）。
    //  拿在手上时用**身体坐标**而不是 item 坐标 —— 手部锚点可能悬空，
    //  生成的弹药会顺着重力飞出去。
    // ==========================================================================
    public static void Use(Body body, Item item)
    {
        if (item == null) return;

        Vector2 pos;
        try
        {
            bool held = body != null && body.HoldingItem(item);
            pos = held ? (Vector2)body.transform.position : (Vector2)item.transform.position;
        }
        catch { pos = (Vector2)item.transform.position; }

        try
        {
            int mdrz9Mag = 0, otherMag = 0, rounds = 0;

            // 7.62×51 主武器弹匣：3~4 个（满）—— 保证开箱一定有 MDRZ9 的匣
            int mags = UnityEngine.Random.Range(3, 5);
            for (int i = 0; i < mags; i++)
                if (SpawnAt(AmmoCompat.Mdrz9AmmoType, pos, true)) mdrz9Mag++;

            // 其他口径的弹匣 1~3 个（不让箱子只出一种东西）
            int others = UnityEngine.Random.Range(1, 4);
            for (int i = 0; i < others; i++)
            {
                int t = UnityEngine.Random.value < 0.55f ? 1 : (UnityEngine.Random.value < 0.6f ? 0 : 2);
                if (t == AmmoCompat.Mdrz9AmmoType) t = 1;
                if (SpawnAt(t, pos, true)) otherMag++;
            }

            // 散装 7.62：6~12 发
            int loose = UnityEngine.Random.Range(6, 13);
            for (int i = 0; i < loose; i++)
                if (SpawnAt(AmmoCompat.Mdrz9AmmoType, pos, false)) rounds++;

            UnityEngine.Object.Destroy(item.gameObject);

            Plugin.Log.LogInfo($"[STEMFishDLC] 弹药箱已打开：MDRZ9 弹匣×{mdrz9Mag} · 其它弹匣×{otherMag} · 散弹×{rounds} @ {pos.x:0},{pos.y:0}");
            FishVoice.SayLine(FishVoice.RandomLine("ammobox", "箱子开了，捡吧。"));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 打开弹药箱失败: {e.Message}");
        }
    }

    private static bool SpawnAt(int ammoType, Vector2 center, bool magazine)
    {
        try
        {
            var p = center + UnityEngine.Random.insideUnitCircle * 0.7f;
            var it = magazine
                ? AmmoCompat.SpawnMagazine(ammoType, p, int.MaxValue)   // int.MaxValue = 灌满
                : AmmoCompat.SpawnRound(ammoType, p);
            return it != null;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成弹药失败: {e.Message}");
            return false;
        }
    }
}
