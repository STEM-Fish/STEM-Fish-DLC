using System;
using System.Collections.Generic;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  SpikeCrawler - 「尖刺爬行者」
//  ---------------------------------------------------------------------------
//  设计：远程消耗型敌人。它**不贴身**，而是维持在一个"刚好能打到你"的距离上，
//        绕着玩家侧移、找角度，然后朝你射尖刺。逼你用手上的枪去解决它。
//
//  实现思路（尽量复用原版管线）：
//    · 外形/身体/死亡掉落全部继承 `thornbackyoung`（原版有这个 prefab，
//      WorldPlaceEntities 里就在用），所以会被打得流血、会掉物品、会被系统统计。
//    · 行为改用我们自己的：每帧把 `SpiderHandler.target` 改写到"保持距离"的点上。
//      原因：SpiderHandler.Update 每帧都会自己算 target（追人/乱走），
//      但它在 FixedUpdate 里才真正朝 target 施加推力 —— 我们在 Update 之后
//      覆盖 target，等于接管了它的移动意图，同时保留了 IK 腿的动画。
//    · 近战伤害清零：它只远程，不咬人。
//    · 尖刺 = 自建 GameObject（Sprite + 分段扫掠 raycast 判定），
//      命中玩家肢体后：掉皮肤/肌肉血、加出血、按概率塞弹片（Limb.shrapnel）。
// ============================================================================

public static class SpikeCrawler
{
    public const string PrefabId = "thornbackyoung";
    public const string SpikeSprite = "spike";

    // ---- 可调 ----
    public static bool Enabled = true;
    public static string LayerCfg = "1,2";       // 只在哪些层出现（biomeDepth，0 基；急速下坠不看这个）
    public static float Density = 0.5f;          // 投放密度（原版 DistributeEntities 的 perChunk 系数）
    public static float KeepDistance = 9f;       // 想维持的距离
    public static float FireMin = 4f;            // 最近开火距离（再近就只后退）
    public static float FireMax = 22f;
    public static float FireInterval = 2.8f;     // 开火间隔（秒）
    public static float SpikeSpeed = 62f;        // 世界单位/秒（Init 里会被 cfg 覆盖）
    public static float SpikeDamage = 9f;        // 皮肤伤害
    public static float SpikeBleed = 2.5f;
    public static float ShrapnelChance = 0.35f;

    private class Inst
    {
        public GameObject go;
        public SpiderHandler sh;
        public float cd;
        public int orbit;       // +1 / -1
        public float orbitT;
    }

    private static readonly List<Inst> _list = new List<Inst>();
    private static readonly List<int> _layers = new List<int>();   // 允许出现的 biomeDepth
    private static int _depth = -9999;
    private static WorldGeneration _world;
    private static bool _done;                  // 本层是否已经投放
    private static int _tries;                  // 投放失败重试次数
    private static float _pending = -1f;        // >0 = 正在等"地形实例化"的倒计时
    private static Sprite _spikeSprite;

    // ==========================================================================
    //  初始化
    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Creature", "SpikeCrawler", true,
                "启用新敌人「尖刺爬行者」（远程，会保持距离射尖刺）").Value;
            LayerCfg = plugin.Config.Bind("Creature", "SpikeCrawlerLayers", "1,2",
                "只在哪些层出现（biomeDepth，0 基：0=第一层 1=深层岩石 2=沙漠），逗号分隔；急速下坠不看这个").Value;
            Density = Mathf.Clamp(plugin.Config.Bind("Creature", "SpikeCrawlerDensity", 0.5f,
                "投放密度（原版 DistributeEntities 的 perChunk 系数，实际数量 ≈ 区块数 × 这个值）").Value, 0.05f, 4f);
            KeepDistance = Mathf.Clamp(plugin.Config.Bind("Creature", "SpikeCrawlerKeepDist", 9f,
                "它想和你保持的距离（世界单位）").Value, 3f, 20f);
            FireInterval = Mathf.Clamp(plugin.Config.Bind("Creature", "SpikeCrawlerFireInterval", 2.8f,
                "开火间隔（秒）；越小越凶").Value, 0.6f, 12f);
            SpikeDamage = Mathf.Clamp(plugin.Config.Bind("Creature", "SpikeCrawlerDamage", 9f,
                "单根尖刺的皮肤伤害（0~100 制）").Value, 1f, 40f);
            SpikeBleed = Mathf.Clamp(plugin.Config.Bind("Creature", "SpikeCrawlerBleed", 2.5f,
                "单根尖刺附加的出血量").Value, 0f, 20f);
            ShrapnelChance = Mathf.Clamp01(plugin.Config.Bind("Creature", "SpikeCrawlerShrapnel", 0.35f,
                "命中时塞进弹片的概率（0~1）").Value);
            SpikeSpeed = Mathf.Clamp(plugin.Config.Bind("Creature", "SpikeCrawlerSpikeSpeed2", 62f,
                "尖刺飞行速度（世界单位/秒；越大越有子弹感）").Value, 15f, 160f);
        }
        catch { }

        // ★ 旧键 `SpikeCrawlerSpikeSpeed` 默认 24 太慢（看着像飘过去的飞镖），
        //   已经写进用户 cfg 了，改默认值不会生效 → 换新键，顺手把旧键删掉。
        // ★ 旧键 `SpikeCrawlerPerLayer` 语义已变（"每层几只" → "每 10 米一只"），
        //   留着一个不再生效的键只会误导 → 删掉
        try
        {
            plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Creature", "SpikeCrawlerSpikeSpeed"));
            plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Creature", "SpikeCrawlerPerLayer"));
            plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Creature", "SpikeCrawlerPerBand"));
            plugin.Config.Remove(new BepInEx.Configuration.ConfigDefinition("Creature", "SpikeCrawlerBandHeight"));
        }
        catch { }

        ParseLayers();
    }

    private static void ParseLayers()
    {
        _layers.Clear();
        try
        {
            foreach (var raw in (LayerCfg ?? "").Split(','))
            {
                int v;
                if (int.TryParse(raw.Trim(), out v) && !_layers.Contains(v)) _layers.Add(v);
            }
        }
        catch { }
    }

    // ==========================================================================
    //  每帧
    // ==========================================================================
    public static void Tick()
    {
        if (!Enabled) return;

        Body body = null;
        try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
        if (body == null) { if (_list.Count > 0) _list.Clear(); return; }

        if (Enabled) EnsureSpawn(body);
        UpdateAll(body);
    }

    private static void UpdateAll(Body body)
    {
        var pc = body.transform.position;

        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var inst = _list[i];
            if (inst.go == null || inst.sh == null) { _list.RemoveAt(i); continue; }

            try
            {
                var self = inst.go.transform.position;
                float dist = Vector2.Distance(self, pc);

                // 太远（>140）就先不接管它的移动意图（省点开销），但**不能从列表里删**
                // —— 它可能是开局整层撒下来的，一旦删掉就再也拿不回来，行为会退化成
                //   普通 thornback（贴脸咬人），"保持距离射尖刺"就没了。
                if (dist > 140f) continue;

                // ---- 保持距离：把 target 改写到"玩家周围的一个点" ----
                var away = ((Vector2)self - (Vector2)pc);
                if (away.sqrMagnitude < 0.01f) away = UnityEngine.Random.insideUnitCircle.normalized;
                away.Normalize();

                // 侧移（绕圈），每隔几秒换个方向
                inst.orbitT -= Time.deltaTime;
                if (inst.orbitT <= 0f)
                {
                    inst.orbitT = UnityEngine.Random.Range(2.5f, 5f);
                    inst.orbit = UnityEngine.Random.value < 0.5f ? -1 : 1;
                }
                var perp = new Vector2(-away.y, away.x) * inst.orbit;

                var want = (Vector2)pc + away * KeepDistance + perp * 2.2f;
                inst.sh.target = want;
                inst.sh.aggro = 1f;             // 别让它切回"闲逛"
                inst.sh.seeDistance = Mathf.Max(inst.sh.seeDistance, FireMax + 4f);

                // ---- 开火 ----
                inst.cd -= Time.deltaTime;
                if (inst.cd > 0f) continue;
                if (dist < FireMin || dist > FireMax) continue;

                var aim = ((Vector2)pc + new Vector2(0f, 0.2f)) - (Vector2)self;
                if (aim.sqrMagnitude < 0.01f) continue;
                aim.Normalize();

                if (!HasLineOfSight(self, aim, dist)) continue;

                Fire(inst.go.transform, PickAimPoint(body, self, dist));
                inst.cd = FireInterval * UnityEngine.Random.Range(0.85f, 1.25f);
            }
            catch { }
        }
    }

    // ==========================================================================
    //  瞄哪？
    //  ---------------------------------------------------------------------------
    //  ★ v0.15.1 之前固定瞄 `body.transform.position + (0,0.2)`。而 Body 的
    //    transform 就在**头**上，所以每一根尖刺都往脑袋飞；命中体如果是 Body
    //    的整体碰撞体（不是某个 Limb），旧代码又 fallback 到 `limbs[0]`，
    //    结果伤害全落在头/同一个部位 —— 头盔护甲自然"感觉不到"。
    //  现在：一半概率随机挑一个肢体、一半概率在躯干范围里随机，再加提前量。
    // ==========================================================================
    private static Vector2 PickAimPoint(Body body, Vector3 self, float dist)
    {
        Vector2 aim = (Vector2)body.transform.position + new Vector2(0f, 0.1f);
        try
        {
            var limbs = body.limbs;
            if (limbs != null && limbs.Length > 0 && UnityEngine.Random.value < 0.55f)
            {
                var l = limbs[UnityEngine.Random.Range(0, limbs.Length)];
                if (l != null) aim = l.transform.position;
            }
            else
            {
                aim += new Vector2(UnityEngine.Random.Range(-0.45f, 0.45f),
                                   UnityEngine.Random.Range(-1.05f, 0.30f));
            }

            // 提前量：按玩家当前速度预判，飞得快才敢预判（慢速弹预判会变成"总打空"）
            var pv = body.rb != null ? body.rb.velocity : Vector2.zero;
            float flight = dist / Mathf.Max(8f, SpikeSpeed);
            aim += pv * flight * 0.85f;
        }
        catch { }
        return aim;
    }

    private static bool HasLineOfSight(Vector3 from, Vector2 dir, float dist)
    {
        try
        {
            var hit = Physics2D.Raycast((Vector2)from + dir * 0.8f, dir, Mathf.Max(1f, dist - 1f));
            if (hit.collider == null) return true;                     // 空 == 通
            var b = hit.collider.GetComponentInParent<Body>();
            var pc = PlayerCamera.main;
            return b != null && pc != null && b == pc.body;            // 先打到玩家 = 通
        }
        catch { return true; }
    }

    // ==========================================================================
    //  发射尖刺
    // ==========================================================================
    private static void Fire(Transform fromT, Vector2 aimPoint)
    {
        try
        {
            if (_spikeSprite == null) _spikeSprite = FishAssets.GetSprite(SpikeSprite);
            if (_spikeSprite == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 缺少 textures/spike.png，尖刺无法发射");
                return;
            }

            Vector3 from = fromT.position;
            var dir = aimPoint - (Vector2)from;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            var go = new GameObject("STEMFish_Spike");
            go.transform.position = from + (Vector3)(dir * 1.1f);
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            go.transform.rotation = Quaternion.Euler(0f, 0f, ang - 90f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _spikeSprite;
            sr.sortingOrder = 260;
            go.transform.localScale = Vector3.one * 0.85f;

            var p = go.AddComponent<SpikeProjectile>();
            p.vel = dir * SpikeSpeed;
            p.damage = SpikeDamage;
            p.bleed = SpikeBleed;
            p.shrapnelChance = ShrapnelChance;
            p.owner = fromT;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 发射尖刺失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  投放（v0.15.6：完全走原版管线）
    //  ---------------------------------------------------------------------------
    //  要求："生成逻辑与原版生物完全一致" —— 所以不再自己找落点，而是直接调用
    //  `WorldGeneration.DistributeEntities()`，也就是原版世界生成时投放
    //  shadecrawler / 树 / 水晶用的那个函数（InfestedModifier.Initialize 就是这么写的）：
    //
    //      w.DistributeEntities(Resources.Load("shadecrawler") as GameObject,
    //                           1.7f, 1.8f,          // perChunk 数量范围
    //                           2f,                  // 落点离地高度
    //                           180f, 0f,            // 随机旋转 / 高度抖动
    //                           false, false,        // 不埋进土里 / 不随机翻转
    //                           null,                // 方块检查委托
    //                           true,                // isTrap
    //                           Vector2.zero, false);
    //
    //  它内部做的事：整层随机取点 → 排除落在 Ground 里的点 → 向下 raycast 找地面
    //  → 用**命中点**加减 spawnYOffset → Instantiate → 设置 BuildingEntity.blockPlacedOn。
    //  我们只是把 "shadecrawler" 换成 "thornbackyoung"，然后把生成出来的实例
    //  改造成爬行者（近战清零 + 染色 + 接进我们的 AI 列表）。
    //
    //  层限制：正常模式只在 SpikeCrawlerLayers（默认 1,2 = 深层岩石/沙漠）投放；
    //         急速下坠不限制（和原版一样哪层都刷）；教程场景完全不刷。
    // ==========================================================================
    private static void EnsureSpawn(Body body)
    {
        WorldGeneration w;
        try { w = WorldGeneration.world; } catch { return; }
        if (w == null) { _world = null; return; }

        int depth;
        try { depth = w.biomeDepth; } catch { return; }

        if (w != _world || depth != _depth)
        {
            _world = w;
            _depth = depth;
            _done = false;
            _tries = 0;
            _pending = 2f;
            return;
        }

        if (_done) return;

        _pending -= Time.deltaTime;
        if (_pending > 0f) return;

        // 教程：不刷
        if (IsTutorial(w))
        {
            _done = true;
            Plugin.Log.LogInfo("[STEMFishDLC] 尖刺爬行者：教程场景，不投放");
            return;
        }

        // 层限制（急速下坠不限）
        bool freefall = IsFreefall();
        if (!freefall && _layers.Count > 0 && !_layers.Contains(depth))
        {
            _done = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 尖刺爬行者：第 {depth} 层不在投放名单（{LayerCfg}），跳过");
            return;
        }

        int n = SpawnVanilla(w);

        // 原版投放是"整层随机试 count 次"，偶尔会一个合法点都没有（尤其小层）→ 重试两次
        if (n <= 0 && _tries < 2)
        {
            _tries++;
            _pending = 3f;
            return;
        }
        _done = true;

        string title = "";
        try
        {
            if (w.biomeTitles != null && depth >= 0 && depth < w.biomeTitles.Length)
                title = "「" + w.biomeTitles[depth] + "」";
        }
        catch { }
        Plugin.Log.LogInfo($"[STEMFishDLC] 尖刺爬行者：第 {depth} 层{title}投放 {n} 只" +
                           (freefall ? "（急速下坠 · 不限层）" : ""));
    }

    private static bool IsTutorial(WorldGeneration w)
    {
        try { return w.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial; }
        catch { return false; }
    }

    private static bool IsFreefall()
    {
        try { var c = RunConfig.Current; return c != null && c.code == "FREEFALL"; }
        catch { return false; }
    }

    // 调原版投放管线，返回"新增并改造成功"的数量
    private static int SpawnVanilla(WorldGeneration w)
    {
        GameObject prefab = null;
        try { prefab = Resources.Load(PrefabId) as GameObject; } catch { }
        if (prefab == null)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 找不到 prefab {PrefabId}，尖刺爬行者无法投放");
            return 0;
        }

        // 先记下场上已有的蛛类，投放完做差集 —— DistributeEntities 是同步 Instantiate
        var before = new HashSet<int>();
        try
        {
            var olds = UnityEngine.Object.FindObjectsOfType<SpiderHandler>();
            for (int i = 0; i < olds.Length; i++)
                if (olds[i] != null) before.Add(olds[i].GetInstanceID());
        }
        catch { }

        try
        {
            w.DistributeEntities(prefab,
                                 Density * 0.85f, Density * 1.15f,
                                 1.5f, 0f, 0.25f,
                                 false, false,
                                 null, false,
                                 Vector2.zero, false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] DistributeEntities 调用失败: {e.Message}");
            return 0;
        }

        int n = 0;
        try
        {
            var now = UnityEngine.Object.FindObjectsOfType<SpiderHandler>();
            for (int i = 0; i < now.Length; i++)
            {
                var sh = now[i];
                if (sh == null || before.Contains(sh.GetInstanceID())) continue;
                if (Register(sh.gameObject, sh)) n++;
            }
        }
        catch { }
        return n;
    }

    // 控制台：spawn spikecrawler player 也会走到这里（SpawnBridge 里拦的）
    public static bool Spawn(Vector2 pos) => SpawnGo(pos) != null;

    public static GameObject SpawnGo(Vector2 pos)
    {
        try
        {
            var go = Utils.Create(PrefabId, pos, 0f);
            if (go == null) return null;

            var sh = go.GetComponent<SpiderHandler>();
            if (sh == null) { UnityEngine.Object.Destroy(go); return null; }

            if (!Register(go, sh)) return null;
            return go;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成尖刺爬行者失败: {e.Message}");
            return null;
        }
    }

    // 把一个现成的 thornbackyoung 改造成尖刺爬行者（原版投放出来的、控制台刷的、
    // 幸运方块送的都走这里，保证行为一致）
    private static bool Register(GameObject go, SpiderHandler sh)
    {
        if (go == null || sh == null) return false;
        try
        {
            // 只远程：近战伤害全清零
            sh.aggro = 1f;
            sh.seeDistance = 28f;
            sh.biteSkinDamage = 0f;
            sh.biteMuscleDamage = 0f;
            sh.bitePain = 0f;
            sh.bleedDamage = 0f;
            sh.venomAmount = 0f;
            sh.infectionChance = 0f;
            sh.breakBone = false;
            sh.doDeathExplode = false;
            sh.burrowWalls = false;
            sh.happinessLoss = 0f;

            // 外观：整只（含腿）染成暗橙红
            go.transform.localScale *= 1.05f;
            go.AddComponent<CrawlerTint>().Bind(sh);

            _list.Add(new Inst
            {
                go = go,
                sh = sh,
                cd = UnityEngine.Random.Range(1.2f, 3f),
                orbit = 1,
                orbitT = UnityEngine.Random.Range(1f, 4f),
            });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 改造尖刺爬行者失败: {e.Message}");
            return false;
        }
    }

    // 调试：立刻在玩家附近刷一只
    public static void SpawnAtPlayer()
    {
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;
            var p = (Vector2)body.transform.position + new Vector2(7f, 2f);
            if (Spawn(p)) Plugin.Log.LogInfo("[STEMFishDLC] 已在玩家附近生成尖刺爬行者");
        }
        catch { }
    }
}

// ============================================================================
//  尖刺投射物
//  ---------------------------------------------------------------------------
//  不用 Rigidbody2D / Collider2D —— 每帧手动推进 + 判定，行为完全可控。
//
//  ★ v0.15.1 两个关键修复：
//    ① **分段扫掠**：以前每帧直接 `position += vel*dt` 再 OverlapCircle 一次。
//       速度提到 60+ 之后一帧走 1 个单位，细碰撞体（肢体）会被整帧跳过 →
//       偶尔"穿过去"。现在把这一帧的位移切成 ≤0.6 的小段，每段用
//       CircleCastAll 扫，永远不会漏。
//    ② **别再只打头**：命中体是 Body 的整体 BoxCollider 时（不是某个 Limb），
//       旧代码 fallback 到 `limbs[0]`（就是头）。现在按命中点找**最近的肢体**。
//       顺带护甲按该肢体算 —— 打腿/打手/打头护甲各不相同才有意义。
// ============================================================================
public class SpikeProjectile : MonoBehaviour
{
    public Vector2 vel;
    public float damage = 9f;
    public float bleed = 2.5f;
    public float shrapnelChance = 0.35f;
    public float life = 3f;
    public Transform owner;         // 发射者：扫掠时跳过它自己的碰撞体

    private const float Radius = 0.26f;

    private SpriteRenderer _streak;

    // 拉一条"拖影"：同一张尖刺贴图压扁拉长挂在尾部，纯 SpriteRenderer，
    // 不依赖 TrailRenderer 的材质（URP 下经常变粉）
    private void Start()
    {
        try
        {
            var sr = GetComponent<SpriteRenderer>();
            if (sr == null || sr.sprite == null) return;

            var go = new GameObject("streak");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.0f, 0f);
            go.transform.localScale = new Vector3(0.55f, 2.0f, 1f);

            _streak = go.AddComponent<SpriteRenderer>();
            _streak.sprite = sr.sprite;
            _streak.color = new Color(1f, 0.62f, 0.32f, 0.45f);
            _streak.sortingOrder = 258;
        }
        catch { }
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        life -= dt;
        if (life <= 0f) { Destroy(gameObject); return; }

        float speed = vel.magnitude;
        if (speed < 0.01f) return;

        var dir = vel / speed;
        float total = speed * dt;
        Vector2 start = transform.position;

        int segs = Mathf.Clamp(Mathf.CeilToInt(total / 0.6f), 1, 24);
        float segLen = total / segs;

        for (int s = 0; s < segs; s++)
        {
            Vector2 from = start + dir * (segLen * s);
            if (Step(from, dir, segLen)) return;
            if (this == null || gameObject == null) return;
        }

        transform.position = start + dir * total;
    }

    // 返回 true = 这一根尖刺的旅程结束（命中了 / 撞墙了）
    private bool Step(Vector2 from, Vector2 dir, float len)
    {
        RaycastHit2D[] hits = null;
        try { hits = Physics2D.CircleCastAll(from, Radius, dir, len); } catch { }
        if (hits == null || hits.Length == 0) return false;

        var pc = PlayerCamera.main;
        var myBody = pc != null ? pc.body : null;

        // CircleCastAll 已按距离从近到远
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            var col = h.collider;
            if (col == null) continue;

            // 别被发射者自己的碰撞体挡下来（尖刺出生点就在它身上）
            if (owner != null && col.transform.IsChildOf(owner)) continue;

            var limb = col.GetComponentInParent<Limb>();
            var body = col.GetComponentInParent<Body>();

            if (limb != null || body != null)
            {
                var victim = limb != null ? limb.body : body;
                if (victim == null) continue;
                if (myBody != null && victim != myBody) continue;   // 别的怪：穿过去

                Hit(h.point, limb, victim);
                Destroy(gameObject);
                return true;
            }

            // 地上的掉落物挡不住尖刺（不然一颗子弹打飞一堆杂物）
            if (col.GetComponentInParent<Item>() != null) continue;

            // 墙 / 地面 / 建筑
            Destroy(gameObject);
            return true;
        }
        return false;
    }

    private static Limb NearestLimb(Body body, Vector2 point)
    {
        try
        {
            var limbs = body.limbs;
            if (limbs == null || limbs.Length == 0) return null;

            Limb best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < limbs.Length; i++)
            {
                var l = limbs[i];
                if (l == null) continue;
                float d = ((Vector2)l.transform.position - point).sqrMagnitude;
                if (d < bestD) { bestD = d; best = l; }
            }
            return best;
        }
        catch { return null; }
    }

    private void Hit(Vector2 point, Limb limb, Body victim)
    {
        try
        {
            if (limb == null) limb = NearestLimb(victim, point);
            if (limb == null) return;

            // 护甲：和原版同一条公式（1 + Σ wearableArmor），伤害除以它
            float armor = 1f;
            try { armor = Mathf.Max(1f, limb.GetArmorReduction()); } catch { }

            float dmg = damage / armor;
            limb.skinHealth = Mathf.Max(0f, limb.skinHealth - dmg);
            limb.muscleHealth = Mathf.Max(0f, limb.muscleHealth - dmg * 0.35f);
            limb.pain += dmg * 0.8f;

            // 穿透：护甲越厚，带进去的出血/弹片越少（1.0 护甲 = 满穿透）
            float pen = Mathf.Clamp01(1.25f / armor);
            if (bleed > 0f) limb.bleedAmount += bleed * pen;
            if (shrapnelChance > 0f && pen > 0.35f &&
                UnityEngine.Random.value < shrapnelChance * pen)
                limb.shrapnel += 1;
        }
        catch { }
    }
}

// ============================================================================
//  把整只生物（含腿）持续染成暗橙红
//  ---------------------------------------------------------------------------
//  ★ v0.15.1：找到"腿一直是绿的"的真正原因了。
//    thornback 的腿**不是 SpriteRenderer**，而是 `IKHandle` + `LineRenderer`
//    （`IKHandle.Start()` 里 GetComponent<LineRenderer>()，每帧只更新顶点）。
//    旧版只遍历 SpriteRenderer → 腿从头到尾没被碰过一次，当然是原版绿。
//
//  现在两条路都走：
//    · SpriteRenderer → 顶点色乘法（身体本来就靠这个，是对的）；
//    · LineRenderer  → 顶点色（startColor/endColor）+ **材质贴图重着色**。
//      为什么要动贴图：如果那条线的贴图本身是绿的，顶点色只会得到"暗绿"
//      （绿 × 橙 ≈ 深绿），跟 UI 图标那次踩的坑一模一样。
//      所以把贴图里偏绿的像素按亮度映射成橙红，非绿像素（白/黑/透明）原样保留。
//
//  刷新节奏：前 15 秒每 0.05 秒刷一次（运行时生成的肢体还在陆续出现），
//  之后每 2 秒刷一次兜底（成本很低：一只十几个 renderer）。
// ============================================================================
public class CrawlerTint : MonoBehaviour
{
    private static readonly Color BodyTint = new Color(0.98f, 0.42f, 0.24f, 1f);   // 暗橙红
    private static readonly Color LegTint = new Color(1.00f, 0.46f, 0.22f, 1f);

    private readonly Dictionary<int, Color> _origSr = new Dictionary<int, Color>();
    private readonly Dictionary<int, Color> _origStart = new Dictionary<int, Color>();
    private readonly Dictionary<int, Color> _origEnd = new Dictionary<int, Color>();
    private readonly Dictionary<int, Material> _mats = new Dictionary<int, Material>();

    private SpiderHandler _sh;
    private float _elapsed;
    private float _next;
    private static bool _diag;

    public void Bind(SpiderHandler sh) { _sh = sh; }

    private void LateUpdate()
    {
        _elapsed += Time.deltaTime;
        if (_elapsed < _next) return;
        _next = _elapsed + (_elapsed < 15f ? 0.05f : 2f);

        try { Paint(); } catch { }
    }

    private void Paint()
    {
        // ---- 身体 / 头 / 前肢：SpriteRenderer ----
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (sr == null) continue;

            int key = sr.GetInstanceID();
            Color c;
            if (!_origSr.TryGetValue(key, out c))
            {
                c = sr.color;
                _origSr[key] = c;
            }
            // ★ 必须用记住的原色，每帧拿当前色再乘一次会越乘越黑
            sr.color = new Color(c.r * BodyTint.r, c.g * BodyTint.g, c.b * BodyTint.b, c.a);
        }

        // ---- 腿：LineRenderer（IKHandle 的）----
        if (_sh != null && _sh.legs != null)
        {
            for (int i = 0; i < _sh.legs.Length; i++)
            {
                var h = _sh.legs[i];
                if (h == null) continue;
                var lr = h.lineRenderer;
                if (lr != null) PaintLine(lr);
            }
        }

        // 兜底：万一还有别的 LineRenderer（尾巴之类）
        var lrs = GetComponentsInChildren<LineRenderer>(true);
        for (int i = 0; i < lrs.Length; i++)
            if (lrs[i] != null) PaintLine(lrs[i]);

        // 一次性诊断：如果腿还是绿的，这行日志能说明"到底找没找到 LineRenderer"
        if (!_diag)
        {
            _diag = true;
            int legCount = _sh != null && _sh.legs != null ? _sh.legs.Length : -1;
            var probe = lrs.Length > 0 ? lrs[0] : null;
            string matInfo = "无";
            try
            {
                var m = probe != null ? probe.sharedMaterial : null;
                if (m != null)
                    matInfo = $"{m.name} tex={(m.mainTexture != null ? m.mainTexture.name : "null")} " +
                              $"color={(m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "n/a")}";
            }
            catch { }
            Plugin.Log.LogInfo($"[STEMFishDLC] 爬行者染色: SpriteRenderer={srs.Length} " +
                               $"legs={legCount} LineRenderer={lrs.Length} 材质={matInfo}");
        }
    }

    private void PaintLine(LineRenderer lr)
    {
        int key = lr.GetInstanceID();

        Color cs, ce;
        if (!_origStart.TryGetValue(key, out cs)) { cs = lr.startColor; _origStart[key] = cs; }
        if (!_origEnd.TryGetValue(key, out ce)) { ce = lr.endColor; _origEnd[key] = ce; }

        float a0 = cs.a <= 0.02f ? 1f : cs.a;
        float a1 = ce.a <= 0.02f ? a0 : ce.a;

        // 顶点色只决定"两端"的颜色；prefab 里若配了多段 colorGradient，
        // 中间那些键还是绿的 → 直接整条渐变替换掉（腿是实心线，不需要花活）
        lr.startColor = new Color(LegTint.r, LegTint.g, LegTint.b, a0);
        lr.endColor = new Color(LegTint.r * 0.72f, LegTint.g * 0.72f, LegTint.b * 0.72f, a1);
        try
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(LegTint.r, LegTint.g, LegTint.b), 0f),
                    new GradientColorKey(new Color(LegTint.r * 0.72f, LegTint.g * 0.72f, LegTint.b * 0.72f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(a0, 0f),
                    new GradientAlphaKey(a1, 1f),
                });
            lr.colorGradient = g;
        }
        catch { }

        // ---- 材质：贴图偏绿 / 材质 _Color 偏绿，两种都要处理 ----
        var mat = lr.sharedMaterial;
        if (mat == null) return;

        Material inst;
        if (!_mats.TryGetValue(key, out inst) || inst == null)
        {
            var repainted = LegTexture.Repaint(mat.mainTexture as Texture2D);

            bool need = repainted != null;
            try
            {
                if (mat.HasProperty("_Color") && LegTexture.IsGreenish(mat.GetColor("_Color")))
                    need = true;
            }
            catch { }

            if (!need) return;          // 材质本来就不绿 → 只靠顶点色就够了

            inst = new Material(mat);
            if (repainted != null) inst.mainTexture = repainted;
            try { if (inst.HasProperty("_Color")) inst.SetColor("_Color", Color.white); } catch { }
            _mats[key] = inst;
            lr.sharedMaterial = inst;
        }
    }

    private void OnDestroy()
    {
        foreach (var kv in _mats)
            if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
        _mats.Clear();
    }
}

// ============================================================================
//  腿部贴图重着色：偏绿像素 → 橙红（按亮度映射，保留线条的明暗渐变）
//  ---------------------------------------------------------------------------
//  每种贴图只处理一次，结果静态缓存（同一层所有爬行者共用）。
// ============================================================================
public static class LegTexture
{
    private static readonly Color Bright = new Color(1.00f, 0.52f, 0.26f);
    private static readonly Color Dark = new Color(0.45f, 0.14f, 0.06f);

    private static readonly Dictionary<int, Texture2D> _cache = new Dictionary<int, Texture2D>();

    public static Texture2D Repaint(Texture2D src)
    {
        if (src == null) return null;
        if (src == Texture2D.whiteTexture) return null;

        int id = src.GetInstanceID();
        if (_cache.ContainsKey(id)) return _cache[id];      // 可能缓存了 null（不用再试）

        Texture2D made = null;
        try
        {
            int w = src.width, h = src.height;
            if (w <= 0 || h <= 0 || w > 512 || h > 512) { _cache[id] = null; return null; }

            Color32[] px = ReadPixels(src, w, h);
            if (px == null) { _cache[id] = null; return null; }

            bool changed = false;
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                if (c.a < 6) continue;
                if (!IsGreenish(c)) continue;

                float lum = Mathf.Clamp01(c.g / 255f);
                var t = Color.Lerp(Dark, Bright, Mathf.Pow(lum, 0.7f));
                px[i] = new Color32((byte)(t.r * 255f), (byte)(t.g * 255f), (byte)(t.b * 255f), c.a);
                changed = true;
            }

            if (!changed) { _cache[id] = null; return null; }   // 本来就不是绿的 → 不动

            made = new Texture2D(w, h, TextureFormat.RGBA32, false);
            made.name = src.name + "_crawler";
            made.filterMode = src.filterMode;
            made.wrapMode = TextureWrapMode.Clamp;
            made.SetPixels32(px);
            made.Apply(false, false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 腿部贴图重着色失败: {e.Message}");
            _cache[id] = null;
            return null;
        }

        _cache[id] = made;
        return made;
    }

    private static Color32[] ReadPixels(Texture2D tex, int w, int h)
    {
        try { if (tex.isReadable) return tex.GetPixels32(); } catch { }

        RenderTexture rt = null;
        Texture2D tmp = null;
        var prev = RenderTexture.active;
        try
        {
            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tmp.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
            tmp.Apply(false, false);
            return tmp.GetPixels32();
        }
        catch { return null; }
        finally
        {
            RenderTexture.active = prev;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (tmp != null) UnityEngine.Object.Destroy(tmp);
        }
    }

    private static bool IsGreenish(Color32 c)
    {
        float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
        if (g <= 0.08f) return false;
        if (g < r * 1.20f) return false;
        if (g < b * 1.20f) return false;
        return true;
    }

    public static bool IsGreenish(Color c)
    {
        if (c.a < 0.02f) return false;
        if (c.g <= 0.08f) return false;
        if (c.g < c.r * 1.20f) return false;
        if (c.g < c.b * 1.20f) return false;
        return true;
    }
}
