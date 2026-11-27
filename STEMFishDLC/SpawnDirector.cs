using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SpawnDirector - 怪物数量：生成期倍率 + （可选）运行时动态投放
//  ---------------------------------------------------------------------------
//  反汇编结论：RunSettings 里**没有任何"敌人刷新率"设定项**，只有 basetrapdensity
//  和 layermodifierchance。所以"怪物变多"必须自己动手，有两条路：
//
//  ① 生成期倍率（所有模式都用）
//     原版所有实体（植物/箱子/陷阱/怪）都走同一个入口：
//       WorldGeneration.DistributeEntities(GameObject basObj, float minPerChunk,
//                                          float maxPerChunk, ...)
//     → prefix 里若是敌对生物，就把 minPerChunk / maxPerChunk 乘倍率。
//     纯数据放大，完全走原版生成管线，位置合法性由游戏自己保证。
//
//  ② 运行时动态投放（只有「急速下坠」开）
//     定时在玩家周围找合法点，Utils.Create(id, pos, 0) 直接 Instantiate。
//     合法性两条：OverlapCircle 为空（不在墙里）+ 向下 Raycast 有地面（不悬空）。
//
//  ⚠️ 动态投放不能默认开。实测密度 ×4 就已经"怪多得没法走路"，
//     所以四个常规模式 dynamicDensity = 0，只保留生成期倍率。
// ============================================================================

public static class SpawnDirector
{
    // ---- 可调 ----
    public static bool Enabled = true;
    public static float BaseInterval = 18f;     // 动态投放的基础间隔（秒）
    public static int MaxAliveBase = 3;         // 附近存活上限的基数
    public static float MaxAlivePerMul = 1.2f;  // 每 1.0 密度增加的存活上限
    public static float MinDist = 11f;          // 最近投放距离
    public static float MaxDist = 24f;          // 最远投放距离
    public static float GracePeriod = 25f;      // 进世界后的安全期（秒）
    public static string PoolCfg = "shadecrawler,wallbiter,cavetick";
    // 动态投放**专用**池：和生成期那个 Pool 分开。
    // ★ 蜱虫是通过世界里的触发器生成的（CaveTickSpawner），不是"撒"出来的；
    //   把它放进动态池就会出现"凭空刷一串蜱虫"，所以默认只放 wallbiter。
    public static string DynamicPoolCfg = "wallbiter";
    // 每层"已刷出"的怪物上限（不是"同时存活"）——
    // 不然玩家蹲在原地不动，怪就永远打不完（死一只补一只）。
    public static int BudgetPerLayer = 60;

    // ---- 当前模式 ----
    public static float SpawnMul = 1f;          // 生成期倍率（DistributeEntities）
    public static float Density = 0f;           // 动态密度（0 = 不动态投放）
    public static bool Dynamic = false;

    private static readonly List<string> _pool = new List<string>();
    private static readonly List<string> _poolLower = new List<string>();
    private static readonly List<string> _poolAllowed = new List<string>();   // _pool 去掉禁用项
    private static readonly List<string> _dynPool = new List<string>();       // 动态投放专用池
    private static readonly List<string> _banLower = new List<string>();      // 当前模式禁用的实体 id（小写）
    private static readonly Dictionary<string, int> _fails = new Dictionary<string, int>();
    private static readonly List<GameObject> _alive = new List<GameObject>();

    private static float _timer;
    private static float _graceLeft;
    private static int _spawnedLayer;          // 本层已刷出的数量
    private static int _lastDepth = -9999;
    private static WorldGeneration _lastWorld;
    private static bool _entered;
    private static int _loggedKey = -1;
    private static bool _patched;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Spawn", "Enabled", true,
                "怪物数量调整总开关（关掉就完全是原版生成量，动态投放也不工作）").Value;
            BaseInterval = Mathf.Clamp(plugin.Config.Bind("Spawn", "BaseInterval", 18f,
                "动态投放的基础间隔（秒）。实际间隔 = 这个值 ÷ 动态密度").Value, 3f, 180f);
            MaxAliveBase = Mathf.Clamp(plugin.Config.Bind("Spawn", "MaxAliveBase", 3,
                "动态投放：附近敌人存活上限的基数").Value, 0, 40);
            MaxAlivePerMul = Mathf.Clamp(plugin.Config.Bind("Spawn", "MaxAlivePerMul", 1.2f,
                "动态投放：密度每 +1.0 增加的存活上限").Value, 0f, 12f);
            MinDist = Mathf.Clamp(plugin.Config.Bind("Spawn", "MinDist", 11f,
                "动态投放：最近距离（世界单位），太小会刷在脸上").Value, 4f, 60f);
            MaxDist = Mathf.Clamp(plugin.Config.Bind("Spawn", "MaxDist", 24f,
                "动态投放：最远距离").Value, MinDist + 2f, 90f);
            GracePeriod = Mathf.Clamp(plugin.Config.Bind("Spawn", "GracePeriod", 25f,
                "刚进世界后的安全期（秒），这段时间不投放").Value, 0f, 300f);
            PoolCfg = plugin.Config.Bind("Spawn", "Pool", PoolCfg,
                "生成期倍率作用的实体 id，逗号分隔（shadecrawler/wallbiter/cavetick）").Value;
            BudgetPerLayer = Mathf.Clamp(plugin.Config.Bind("Spawn", "BudgetPerLayer", 60,
                "每层最多刷出多少只（累计，不是同时存活）。用完这层就不再补").Value, 1, 500);
            DynamicPoolCfg = plugin.Config.Bind("Spawn", "DynamicPool", "wallbiter,spikecrawler",
                "动态投放**只用**这些实体，逗号分隔。默认 wallbiter + 尖刺爬行者 —— " +
                "洞穴蜱虫是触发器生成的，放进这里会变成凭空刷一串").Value;

            // v0.15.2：把「尖刺爬行者」加进自然刷新池。老 cfg 里存的是 "wallbiter"，
            // 改默认值不会生效，所以做一次性迁移（标记写在 CrawlerInPool，避免用户
            // 之后手动删掉 spikecrawler 又被强行加回来）。
            try
            {
                var marker = plugin.Config.Bind("Spawn", "CrawlerInPool", false,
                    "内部标记（一次性迁移）：动态池是否已经加过 spikecrawler，别手动改");
                if (!marker.Value && (DynamicPoolCfg ?? "").Trim() == "wallbiter")
                {
                    plugin.Config.Bind("Spawn", "DynamicPool", "wallbiter,spikecrawler",
                        "动态投放**只用**这些实体，逗号分隔").Value = "wallbiter,spikecrawler";
                    DynamicPoolCfg = "wallbiter,spikecrawler";
                    Plugin.Log.LogInfo("[STEMFishDLC] 动态投放池已加入 spikecrawler（一次性迁移）");
                }
                marker.Value = true;
            }
            catch { }

            ParsePool();
            RebuildAllowed();
            PatchDistribute();
            PatchCaveTicks();
            PatchStructures();
            Plugin.Log.LogInfo($"[STEMFishDLC] 刷怪模块: {(Enabled ? "开" : "关")} | 池={string.Join("/", _pool)}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 刷怪配置读取失败: {e.Message}");
        }
    }

    private static void ParsePool()
    {
        _pool.Clear();
        _poolLower.Clear();
        try
        {
            foreach (var raw in (PoolCfg ?? "").Split(','))
            {
                var s = raw.Trim();
                if (s.Length == 0 || _pool.Contains(s)) continue;
                _pool.Add(s);
                // 预存小写：DistributeEntities 的 prefix 在世界生成期会被调用上百次，
                // 不能每次都 ToLowerInvariant 分配字符串
                _poolLower.Add(s.ToLowerInvariant());
            }
        }
        catch { }
        if (_pool.Count == 0) { _pool.Add("shadecrawler"); _poolLower.Add("shadecrawler"); }
    }

    // ==========================================================================
    //  ① 生成期倍率：所有实体投放的唯一入口
    // ==========================================================================
    private static void PatchDistribute()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(WorldGeneration), "DistributeEntities");
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 WorldGeneration.DistributeEntities，生成期倍率失效");
                return;
            }
            var pre = typeof(SpawnDirector).GetMethod("Distribute_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.spawnmul").Patch(m, prefix: new HarmonyMethod(pre));
            var info = Harmony.GetPatchInfo(m);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 生成期怪物倍率 hook 完成 | prefix={info?.Prefixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成期倍率 hook 失败: {e.Message}");
        }
    }

    // 只放大敌对生物；植物/箱子/陷阱/建筑原样
    // ★ 返回 false = 整个 DistributeEntities 跳过 —— 用来"这一局根本不刷这种怪"
    private static bool Distribute_Prefix(GameObject basObj, ref float minPerChunk, ref float maxPerChunk)
    {
        try
        {
            if (basObj == null) return true;

            // 原版角色 / 教程：一行都不改，原版刷多少就是多少
            // （这里只是"不修改参数"，不是拦截原版投放）
            if (CharacterSelect.Current != CharacterSelect.Sf07) return true;
            if (CharacterSelect.IsTutorial()) return true;

            // 禁用名单优先：急速下坠不刷黑暗潜行者 / 洞穴蜱虫
            if (IsBanned(basObj.name)) return false;

            if (!Enabled || SpawnMul <= 1.001f) return true;
            if (!IsHostile(basObj.name)) return true;

            minPerChunk *= SpawnMul;
            maxPerChunk *= SpawnMul;
        }
        catch { }
        return true;
    }

    private static bool IsBanned(string name)
    {
        if (_banLower.Count == 0 || string.IsNullOrEmpty(name)) return false;
        var n = name.ToLowerInvariant();
        for (int i = 0; i < _banLower.Count; i++)
        {
            var id = _banLower[i];
            if (!string.IsNullOrEmpty(id) && n.Contains(id)) return true;
        }
        return false;
    }

    private static bool IsHostile(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var n = name.ToLowerInvariant();
        for (int i = 0; i < _poolLower.Count; i++)
        {
            var id = _poolLower[i];
            if (!string.IsNullOrEmpty(id) && n.Contains(id)) return true;
        }
        return false;
    }

    // ==========================================================================
    //  ② 运行时动态投放
    // ==========================================================================
    public static void SetMode(float spawnMul, float dynamicDensity, bool dynamic, string[] bans = null)
    {
        SpawnMul = Mathf.Max(0f, spawnMul);
        Density = Mathf.Max(0f, dynamicDensity);
        Dynamic = dynamic && Density > 0.01f && Enabled;

        _banLower.Clear();
        if (bans != null)
            for (int i = 0; i < bans.Length; i++)
            {
                var b = bans[i];
                if (!string.IsNullOrEmpty(b)) _banLower.Add(b.Trim().ToLowerInvariant());
            }
        RebuildAllowed();

        _timer = 2f;
        _entered = false;
        _alive.Clear();

        int key = Mathf.RoundToInt(SpawnMul * 100f) * 1000 + Mathf.RoundToInt(Density * 10f);
        if (key == _loggedKey) return;
        _loggedKey = key;

        if (Dynamic)
        {
            float iv = BaseInterval / Mathf.Max(0.1f, Density);
            Plugin.Log.LogInfo($"[STEMFishDLC] 怪物: 生成×{SpawnMul:0.0} + 动态×{Density:0.0} " +
                               $"→ 间隔 {iv:0.0}s，附近上限 {MaxAlive(Density)} 只");
        }
        else
        {
            Plugin.Log.LogInfo($"[STEMFishDLC] 怪物: 仅生成期 ×{SpawnMul:0.0}（不动态投放）");
        }
    }

    /// <summary>
    /// 原版角色开局：把生成期倍率 / 禁用名单 / 动态投放全部归零，刷怪量改回原版。
    /// 由 StarterKit 的局入口调用 —— 上一局（SF-07）留下的 SpawnMul、bans
    /// 不会跨局复用，否则选原版角色也会按我们模式的倍率刷怪。
    /// </summary>
    public static void ResetToVanilla()
    {
        SpawnMul = 1f;
        Density = 0f;
        Dynamic = false;
        _banLower.Clear();
        RebuildAllowed();

        _timer = 2f;
        _entered = false;
        _spawnedLayer = 0;
        _lastWorld = null;
        _lastDepth = -9999;
        _alive.Clear();

        Plugin.Log.LogInfo("[STEMFishDLC] 刷怪参数已恢复原版（当前不是 SF-07）");
    }

    private static int MaxAlive(float density)
        => Mathf.Clamp(MaxAliveBase + Mathf.RoundToInt(density * MaxAlivePerMul), 1, 40);

    public static void Tick()
    {
        if (!Enabled || !Dynamic || Density <= 0.01f) return;
        if (!CharacterSelect.IsPlayerSf07()) return;      // 动态刷怪是 SF-07 的难度调整

        Body body = null;
        try
        {
            var pc = PlayerCamera.main;
            body = pc != null ? pc.body : null;
        }
        catch { }

        if (body == null)
        {
            if (_entered) { _entered = false; _alive.Clear(); }
            return;
        }

        if (!_entered)
        {
            _entered = true;
            _graceLeft = GracePeriod;
            _timer = Mathf.Max(1.5f, BaseInterval / Mathf.Max(0.1f, Density));
            Plugin.Log.LogInfo($"[STEMFishDLC] 动态投放启动：安全期 {GracePeriod:0}s");
        }

        if (_graceLeft > 0f) { _graceLeft -= Time.deltaTime; return; }
        if (Plugin.IsPaused) return;

        try { if (WorldGeneration.world == null) return; } catch { return; }

        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = Mathf.Max(1.2f, BaseInterval / Mathf.Max(0.1f, Density));

        // 换层 → 重置本层预算
        try
        {
            var w = WorldGeneration.world;
            int depth = w != null ? w.biomeDepth : 0;
            if (w != _lastWorld || depth != _lastDepth)
            {
                _lastWorld = w;
                _lastDepth = depth;
                _spawnedLayer = 0;
            }
        }
        catch { }

        if (_spawnedLayer >= BudgetPerLayer) return;                 // 本层额度用完了

        var origin = body.transform.position;
        // 仍然留一个"同时存活"的硬上限，防止一次刷太多把帧数拖死
        if (CountNear(origin, 60f) >= MaxAlive(Density) * 3) return;

        if (SpawnOne(origin)) _spawnedLayer++;
    }

    private static int CountNear(Vector3 p, float radius)
    {
        int n = 0;
        float r2 = radius * radius;
        for (int i = _alive.Count - 1; i >= 0; i--)
        {
            var go = _alive[i];
            if (go == null) { _alive.RemoveAt(i); continue; }
            if ((go.transform.position - p).sqrMagnitude <= r2) n++;
        }
        return n;
    }

    private static bool SpawnOne(Vector3 origin)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector2 pos;
            // SpotFinder：向下搜 12 单位并落在射线命中点。
            // 旧写法是"随机点 + 向下 3.5 单位"——地表的大空洞里几乎找不到地面，
            // 结果就是日志里一片 0。
            if (!SpotFinder.Find(origin, MinDist, MaxDist, 6, out pos)) continue;

            var id = PickId();
            if (id == null) return false;

            GameObject go = null;
            try
            {
                go = id == "spikecrawler" ? SpikeCrawler.SpawnGo(pos) : Utils.Create(id, pos, 0f);
            }
            catch (Exception e) { NoteFail(id, e.Message); continue; }

            if (go == null) { NoteFail(id, "返回 null"); continue; }

            _alive.Add(go);
            return true;
        }
        return false;
    }

    private static string PickId()
    {
        if (_dynPool.Count == 0) return null;
        if (_dynPool.Count == 1) return _dynPool[0];
        return _dynPool[UnityEngine.Random.Range(0, _dynPool.Count)];
    }

    // 动态池 = DynamicPoolCfg 去掉禁用项；一个都不剩就退回生成期池（同样去禁用项）
    private static void RebuildAllowed()
    {
        _poolAllowed.Clear();
        for (int i = 0; i < _pool.Count; i++)
            if (!IsBanned(_pool[i])) _poolAllowed.Add(_pool[i]);

        _dynPool.Clear();
        try
        {
            foreach (var raw in (DynamicPoolCfg ?? "").Split(','))
            {
                var s = raw.Trim();
                if (s.Length == 0 || IsBanned(s)) continue;
                if (!_dynPool.Contains(s)) _dynPool.Add(s);
            }
        }
        catch { }
        if (_dynPool.Count == 0) _dynPool.AddRange(_poolAllowed);
    }

    // Resources 里没有这个 id 时 Instantiate(null) 会抛；连续失败就把它从池子里摘掉
    private static void NoteFail(string id, string why)
    {
        int n;
        _fails.TryGetValue(id, out n);
        n++;
        _fails[id] = n;
        if (n == 2) Plugin.Log.LogWarning($"[STEMFishDLC] 投放 {id} 失败（{why}）");

        if (n >= 3 && _pool.Count > 1)
        {
            int idx = _pool.IndexOf(id);
            if (idx >= 0 && idx < _poolLower.Count) _poolLower.RemoveAt(idx);
            _pool.Remove(id);
            RebuildAllowed();
            Plugin.Log.LogWarning($"[STEMFishDLC] 已把 {id} 移出投放池（连续失败 {n} 次），剩余: {string.Join("/", _pool)}");
        }
    }

    // ==========================================================================
    //  ④ 结构生成诊断（只读，不改任何东西）
    //  ---------------------------------------------------------------------------
    //  Nexus 反馈「某些层完全没有商人和结构」。商人不是单独刷的，反汇编确认：
    //    每一层 WorldGenerateStructures 按 biomeDepth 分段调用 GenerateLifePods(密度)
    //      num = 区块面积 × 密度 × totalLootRarity
    //      totalLootRarity = runSettings["baselootdensity"] × lootRarityMultiplier
    //    每生成一个生命舱，再按 runSettings["traderchance"]% 决定放商人还是放箱子。
    //  所以「既没商人也没舱」只可能是：这段深度没匹配到分支、密度算成 0、
    //  或者预制体加载失败让协程中途抛异常。这里把前两项写进日志，
    //  用户回传日志就能判断是哪一种。
    // ==========================================================================
    private static void PatchStructures()
    {
        try
        {
            var m = AccessTools.Method(typeof(WorldGeneration), "GenerateLifePods", new[] { typeof(float) });
            if (m == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到 GenerateLifePods，结构诊断不可用"); return; }
            var post = typeof(SpawnDirector).GetMethod("LifePods_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.structdiag").Patch(m, postfix: new HarmonyMethod(post));
            Plugin.Log.LogInfo("[STEMFishDLC] 结构生成诊断 hook 完成");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 结构诊断 hook 失败: {e.Message}");
        }
    }

    private static void LifePods_Postfix(float __0)
    {
        try
        {
            var w = WorldGeneration.world;
            int depth = w != null ? w.biomeDepth : -999;

            float loot = -1f;
            try
            {
                var getter = AccessTools.Method(typeof(WorldGeneration), "get_totalLootRarity");
                if (getter != null && w != null) loot = Convert.ToSingle(getter.Invoke(w, null));
            }
            catch { }

            float trader = -1f;
            try
            {
                var rs = WorldGeneration.runSettings;
                if (rs != null && rs.ContainsKey("traderchance")) trader = Convert.ToSingle(rs["traderchance"]);
            }
            catch { }

            Plugin.Log.LogInfo($"[STEMFishDLC] 生命舱生成 | 层={depth} 密度={__0:0.0000} " +
                               $"掉落倍率={loot:0.###} 商人概率={trader:0.#}%");
        }
        catch { }
    }

    // ==========================================================================
    //  ③ 洞口蜱虫：它们不是 DistributeEntities 投放的
    //  ---------------------------------------------------------------------------
    //  CaveTickSpawner 是世界 prefab 里预先摆好的**触发器**：玩家踩进去 →
    //  OnTriggerEnter2D → 协程里生成一群 caveticks。
    //  所以"禁用它"只能拦这个触发点：命中禁用名单就把触发器本体删掉。
    // ==========================================================================
    private static void PatchCaveTicks()
    {
        try
        {
            var m = AccessTools.Method(typeof(CaveTickSpawner), "OnTriggerEnter2D");
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 CaveTickSpawner.OnTriggerEnter2D，蜱虫无法禁用");
                return;
            }
            var pre = typeof(SpawnDirector).GetMethod("CaveTick_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.cavetick").Patch(m, prefix: new HarmonyMethod(pre));
            Plugin.Log.LogInfo("[STEMFishDLC] 蜱虫生成器 hook 完成");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 蜱虫 hook 失败: {e.Message}");
        }
    }

    private static bool CaveTick_Prefix(CaveTickSpawner __instance)
    {
        try
        {
            if (!IsBanned("cavetick")) return true;
            if (__instance != null) UnityEngine.Object.Destroy(__instance.gameObject);
        }
        catch { }
        return false;      // 不执行原方法：不生成蜱虫、不放声音
    }
}
