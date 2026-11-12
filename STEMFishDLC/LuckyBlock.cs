using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  LuckyBlock - 「幸运方块」（只在急速下坠里刷）
//  ---------------------------------------------------------------------------
//  外观：金色方块 + 问号（textures/luckyblock.png，占位图，作者可换）。
//  交互：**右键**（靠近它按右键）或者**打碎**（几枪就打烂），两种都能触发。
//  逻辑：克隆原版 `foodbox` 的 prefab（它本身是 BuildingEntity，天然可被攻击、
//        有生命值、会破碎），把 id 改成 luckyblock、掉落表清空，
//        破碎那一帧由 `BuildingEntity.Update` 的 prefix 截住 → 抽效果 + 销毁。
//
//  效果表（加权）：
//    无限火力 22 / 兴奋剂 18 / 止血 14 / 修复皮肉 16 / 接骨+取弹片 12 /
//    刷怪 11（thornbackyoung 5、spikecrawler 5、thornbackelder 1）/
//    冲击波 5 / 清场 2
// ============================================================================

public static class LuckyBlock
{
    public const string Id = "luckyblock";
    public const string Prefab = "foodbox";

    public static bool Enabled = true;
    public static int PerLayer = 10;
    public static bool ToastOn = true;
    public static float BlastRadius = 13f;      // 冲击波半径
    public static float BlastPower = 36f;       // 冲击波中心速度

    private class Block
    {
        public BuildingEntity be;
        public float born;
    }

    private static readonly List<Block> _blocks = new List<Block>();
    private static int _depth = -9999;
    private static WorldGeneration _world;
    private static bool _done;         // 本层是否已经整层摆好
    private static float _timer;       // 换层后的等待倒计时
    private static Sprite _sprite;
    private static Body _lastBody;

    // 效果计时
    private static float _ammoUntil;
    private static float _stimUntil;
    private static float _stimOrigSpeed = -1f;

    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Lucky", "Enabled", true,
                "启用「幸运方块」（只在急速下坠里刷）").Value;
            var perLayerEntry = plugin.Config.Bind("Lucky", "PerLayer", 70,
                "每层生成几个幸运方块");
            PerLayer = Mathf.Clamp(perLayerEntry.Value, 0, 200);

            // v0.15.6：急速下坠要"到处都是" → 默认 30 → 70。
            // 老 cfg 里存的是旧默认（10 / 30），改默认值不会生效，做一次性迁移。
            try
            {
                var marker = plugin.Config.Bind("Lucky", "DensityMigrated2", false,
                    "内部标记：幸运方块密度是否已升到 70，别手动改");
                if (!marker.Value && (PerLayer == 10 || PerLayer == 30))
                {
                    perLayerEntry.Value = 70;
                    PerLayer = 70;
                    Plugin.Log.LogInfo("[STEMFishDLC] 幸运方块密度：30 → 70（一次性迁移）");
                }
                marker.Value = true;
            }
            catch { }
            ToastOn = plugin.Config.Bind("Lucky", "Toast", true,
                "抽到效果时在屏幕上弹出提示").Value;
            BlastRadius = Mathf.Clamp(plugin.Config.Bind("Lucky", "BlastRadius", 13f,
                "冲击波半径（世界单位）").Value, 3f, 30f);
            BlastPower = Mathf.Clamp(plugin.Config.Bind("Lucky", "BlastPower", 36f,
                "冲击波中心推力（给目标设置的速度，越大飞得越远）").Value, 5f, 90f);
        }
        catch { }

        if (!Enabled) return;
        try
        {
            var upd = AccessTools.Method(typeof(BuildingEntity), "Update");
            if (upd != null)
            {
                var pre = typeof(LuckyBlock).GetMethod("Break_Prefix", BindingFlags.NonPublic | BindingFlags.Static);
                new Harmony("com.stemfish.dlc.luckybreak").Patch(upd, prefix: new HarmonyMethod(pre));
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 BuildingEntity.Update，幸运方块无法被打碎");

            var fire = AccessTools.Method(typeof(GunScript), "Fire");
            if (fire != null)
            {
                var post = typeof(LuckyBlock).GetMethod("Fire_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
                new Harmony("com.stemfish.dlc.luckyammo").Patch(fire, postfix: new HarmonyMethod(post));
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 幸运方块 hook 失败: {e.Message}");
        }
    }

    public static bool Active
    {
        get { try { var c = RunConfig.Current; return c != null && c.code == "FREEFALL"; } catch { return false; } }
    }

    // ==========================================================================
    //  打碎触发
    // ==========================================================================
    private static bool Break_Prefix(BuildingEntity __instance)
    {
        try
        {
            if (__instance == null || __instance.id != Id) return true;
            if (__instance.health >= 0.5f) return true;

            Trigger(__instance);
            return false;      // 跳过原版的掉落/销毁，我们自己处理
        }
        catch { return true; }
    }

    // 无限火力：打完不扣弹（把弹匣按满）
    private static void Fire_Postfix(GunScript __instance)
    {
        try
        {
            if (Time.time >= _ammoUntil || __instance == null) return;
            var pc = PlayerCamera.main;
            if (pc == null || pc.body == null) return;
            if (!__instance.transform.IsChildOf(pc.body.transform)) return;

            __instance.hasMag = true;
            if (__instance.roundsInMag < __instance.magCapacity)
                __instance.roundsInMag = __instance.magCapacity;
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
        if (body == null) { _blocks.Clear(); return; }
        _lastBody = body;

        Cleanup();
        HandleRightClick(body);

        // 兴奋剂到期恢复速度
        if (_stimUntil > 0f && Time.time >= _stimUntil)
        {
            _stimUntil = 0f;
            try { if (_stimOrigSpeed > 0f) body.maxSpeed = _stimOrigSpeed; } catch { }
            _stimOrigSpeed = -1f;
        }

        if (Active && PerLayer > 0) LayerSpawn(body);
    }

    private static void Cleanup()
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
            if (_blocks[i].be == null) _blocks.RemoveAt(i);
    }

    private static void HandleRightClick(Body body)
    {
        if (!Input.GetMouseButtonDown(1)) return;
        if (Plugin.IsPaused) return;
        try { if (PlayerCamera.main != null && PlayerCamera.main.dragItem != null) return; } catch { }

        Vector2 wp;
        try
        {
            var cam = Camera.main;
            if (cam == null) return;
            var sp = cam.ScreenToWorldPoint(Input.mousePosition);
            wp = new Vector2(sp.x, sp.y);
        }
        catch { return; }

        Block best = null;
        float bestD = 2.6f;
        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            if (b.be == null) continue;
            float d = Vector2.Distance((Vector2)b.be.transform.position, wp);
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best == null) return;

        Trigger(best.be);
    }

    // ==========================================================================
    //  抽效果
    // ==========================================================================
    private static void Trigger(BuildingEntity be)
    {
        if (be == null) return;

        Vector2 pos = be.transform.position;
        _fxPos = pos;
        _fxPosValid = true;
        _blocks.RemoveAll(b => b.be == null || b.be == be);

        try { UnityEngine.Object.Destroy(be.gameObject); } catch { }

        var body = _lastBody;
        if (body == null) { try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { } }

        int roll = UnityEngine.Random.Range(0, 100);

        try
        {
            if (roll < 22)                 DoAmmo(body);
            else if (roll < 40)            DoStim(body);
            else if (roll < 54)            DoBleed(body);
            else if (roll < 70)            DoTissue(body);
            else if (roll < 82)            DoMend(body);
            else if (roll < 87)            DoSpawn(pos, "thornbackyoung", "……出来一个小的。");
            else if (roll < 92)            DoSpawnCrawler(pos);
            else if (roll < 93)            DoSpawn(pos, "thornbackelder", "……不。那不是小的。");
            else if (roll < 98)            DoBlast(pos);
            else                           DoClear(pos);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 幸运方块效果失败: {e.Message}");
        }
    }

    // ---- 效果 ----
    private static void DoAmmo(Body body)
    {
        _ammoUntil = Mathf.Max(_ammoUntil, Time.time + 25f);
        Danmaku("【无限火力】25 秒不用换弹", new Color(1f, 0.85f, 0.35f));
    }

    private static void DoStim(Body body)
    {
        if (body == null) return;
        // 不做药物系统（避免过量/戒断/心跳异常）：只给体能和速度，25 秒后精确还原
        if (_stimUntil <= 0f)
        {
            _stimOrigSpeed = body.maxSpeed;
            body.maxSpeed *= 1.15f;
        }
        _stimUntil = Mathf.Max(_stimUntil, Time.time + 25f);
        try
        {
            body.stamina = 100f;
            var limbs = body.limbs;
            if (limbs != null)
                for (int i = 0; i < limbs.Length; i++)
                    if (limbs[i] != null) limbs[i].pain *= 0.5f;
        }
        catch { }
        Danmaku("【兴奋剂】体力拉满 · 行动提速（无副作用）", new Color(0.55f, 0.95f, 0.65f));
    }

    private static void DoBleed(Body body)
    {
        if (body == null) return;
        try
        {
            var limbs = body.limbs;
            if (limbs != null)
                for (int i = 0; i < limbs.Length; i++)
                    if (limbs[i] != null) limbs[i].bleedAmount = 0f;
        }
        catch { }
        Danmaku("【止血】所有伤口停止流血", new Color(0.95f, 0.45f, 0.45f));
    }

    private static void DoTissue(Body body)
    {
        if (body == null) return;
        try
        {
            var limbs = body.limbs;
            if (limbs != null)
                for (int i = 0; i < limbs.Length; i++)
                {
                    var l = limbs[i];
                    if (l == null) continue;
                    l.skinHealth = Mathf.Min(100f, l.skinHealth + 25f);
                    l.muscleHealth = Mathf.Min(100f, l.muscleHealth + 25f);
                    l.pain *= 0.6f;
                }
        }
        catch { }
        Danmaku("【再生】全身皮肉修复了一部分", new Color(0.55f, 0.85f, 0.95f));
    }

    private static void DoMend(Body body)
    {
        if (body == null) return;
        try
        {
            var limbs = body.limbs;
            if (limbs != null)
                for (int i = 0; i < limbs.Length; i++)
                {
                    var l = limbs[i];
                    if (l == null) continue;
                    l.broken = false;
                    l.dislocated = false;
                    l.shrapnel = 0;
                    l.infected = false;
                    l.infectionAmount = 0f;
                }
        }
        catch { }
        Danmaku("【重塑】骨折接上 · 弹片消失 · 感染清除", new Color(0.80f, 0.75f, 1f));
    }

    private static void DoSpawn(Vector2 pos, string id, string msg)
    {
        try
        {
            var p = pos + UnityEngine.Random.insideUnitCircle * 2.5f;
            Utils.Create(id, p, 0f);
        }
        catch { }
        Danmaku(msg + "（惊喜）", new Color(1f, 0.55f, 0.35f));
    }

    private static void DoSpawnCrawler(Vector2 pos)
    {
        try
        {
            var p = pos + UnityEngine.Random.insideUnitCircle * 2.5f;
            SpikeCrawler.SpawnGo(p);
        }
        catch { }
        Danmaku("……它已经在瞄你了。（惊喜）", new Color(1f, 0.55f, 0.35f));
    }

    // ==========================================================================
    //  冲击波：只炸飞，不造成任何伤害
    //  ---------------------------------------------------------------------------
    //  ★ v0.15.1 重写。旧版为什么"没感觉"：
    //    ① 半径只有 7，边缘系数 k 线性衰减 → 稍远一点推力就没了；
    //    ② 只对 `bodyType == Dynamic` 的刚体 AddForce —— 而**站着的人形角色**
    //       （Body）每帧都被移动逻辑重写 velocity，冲量一帧就没了；敌人
    //       （SpiderHandler）也是每 FixedUpdate 自己 AddForce，同样被覆盖。
    //       所以以前真正会被掀飞的只有"已经布娃娃"的身体和地上的杂物。
    //    ③ AddForce 走 Impulse 会除以质量，重的东西几乎不动。
    //
    //  现在的做法：分三类目标，而且**直接写 velocity**
    //    · Body（玩家 / NPC）→ 先 `Ragdoll()` 踹成布娃娃（此时肢体 rb 被唤醒，
    //      移动逻辑不再接管），再 `SetVelocity()` 给全身肢体速度；
    //    · SpiderHandler（蛛类）→ 先给 `stunTime`，眩晕期间它不再自己施力，
    //      速度才拉得住；
    //    · 其它刚体（掉落物 / 建筑碎块）→ 直接给 velocity，不除质量。
    // ==========================================================================
    private static void DoBlast(Vector2 pos)
    {
        float Radius = BlastRadius;    // 旧版固定 7 —— 现在覆盖一整个房间
        float Power = BlastPower;      // 中心速度（世界单位/秒）

        // 视觉：原版爆炸粒子 + 向外扩散的强光 + 镜头震动
        try
        {
            UnityEngine.Object.Instantiate(Resources.Load("Special/ExplosionParticle"),
                                           (Vector3)pos, Quaternion.identity);
        }
        catch { }
        try { LightFx.Spawn((Vector3)pos, new Color(1f, 0.93f, 0.78f), 1.6f, 5.5f, 0.40f, 0.95f, 5f); }
        catch { }
        try
        {
            var pc = PlayerCamera.main;
            if (pc != null && pc.shaker != null) pc.shaker.Shake(90f);
        }
        catch { }

        int pushed = 0;
        try
        {
            var hits = Physics2D.OverlapCircleAll(pos, Radius);
            var seen = new HashSet<int>();

            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i];
                if (c == null) continue;

                var d = (Vector2)c.transform.position - pos;
                if (d.sqrMagnitude < 0.0004f) d = UnityEngine.Random.insideUnitCircle.normalized;
                float dist = Mathf.Max(0.5f, d.magnitude);
                if (dist > Radius) continue;

                var dir = d / dist;
                float k = 1f - dist / Radius;              // 1 = 正中
                float p = Mathf.Lerp(Power * 0.25f, Power, k * k);

                // ① 人形：强制布娃娃，再给全身肢体速度
                var b = c.GetComponentInParent<Body>();
                if (b != null)
                {
                    if (!seen.Add(b.GetInstanceID())) continue;
                    try { b.Ragdoll(); } catch { }
                    try { b.SetVelocity(dir * p); } catch { }
                    pushed++;
                    continue;
                }

                // ② 蛛类：眩晕 + 直接改速度
                var sh = c.GetComponentInParent<SpiderHandler>();
                if (sh != null)
                {
                    if (!seen.Add(sh.GetInstanceID())) continue;
                    try { sh.stunTime = Mathf.Max(sh.stunTime, 0.5f + 1.5f * k); } catch { }
                    var rbs = sh.GetComponent<Rigidbody2D>();
                    if (rbs != null && rbs.bodyType != RigidbodyType2D.Static)
                        rbs.velocity = dir * p;
                    pushed++;
                    continue;
                }

                // ③ 其它刚体（掉落物 / 建筑碎块）：直接给速度，不除以质量
                var rb = c.attachedRigidbody;
                if (rb != null && rb.bodyType != RigidbodyType2D.Static && seen.Add(rb.GetInstanceID()))
                {
                    rb.velocity = dir * p;
                    pushed++;
                }
            }
        }
        catch { }

        Danmaku($"【冲击波】{pushed} 个目标被掀飞 —— 只有推力，没有伤害", new Color(1f, 0.95f, 0.5f));
    }

    // 清场：把方块下方 10×10 内的建筑和掉落清掉（地形本身不动）
    private static void DoClear(Vector2 pos)
    {
        int n = 0;
        try
        {
            var center = pos + new Vector2(0f, -5f);
            var half = new Vector2(5f, 5f);

            var builds = UnityEngine.Object.FindObjectsOfType<BuildingEntity>();
            for (int i = 0; i < builds.Length; i++)
            {
                var b = builds[i];
                if (b == null || b.id == Id) continue;
                var p = (Vector2)b.transform.position;
                if (Mathf.Abs(p.x - center.x) > half.x || Mathf.Abs(p.y - center.y) > half.y) continue;
                UnityEngine.Object.Destroy(b.gameObject);
                n++;
            }

            for (int i = Item.allItems.Count - 1; i >= 0; i--)
            {
                var it = Item.allItems[i];
                if (it == null || it.container != null) continue;
                var p = (Vector2)it.transform.position;
                if (Mathf.Abs(p.x - center.x) > half.x || Mathf.Abs(p.y - center.y) > half.y) continue;
                UnityEngine.Object.Destroy(it.gameObject);
                n++;
            }
        }
        catch { }
        Danmaku($"【塌方】下方一片都空了（{n} 个物体）", new Color(0.7f, 0.7f, 0.7f));
    }

    // ==========================================================================
    //  生成
    //  ---------------------------------------------------------------------------
    //  ★ v0.15.3：和弹药箱一样改成「开局整层一次性摆好」，不再边探索边刷。
    //    旧版每 2.5 秒在玩家附近放一个 → 10 个全挤在出生点，往下走就没有了。
    // ==========================================================================
    private static void LayerSpawn(Body body)
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
            _timer = 2f;
            return;
        }

        if (_done) return;

        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _done = true;

        ScatterAll(w, depth);
    }

    private static void ScatterAll(WorldGeneration w, int depth)
    {
        int want = Mathf.Clamp(PerLayer, 0, 200);
        var spots = new List<Vector2>();
        Vector2 playerPos = Vector2.zero;
        try { playerPos = PlayerCamera.main.body.transform.position; } catch { }
        int ok = 0;

        for (int i = 0; i < want * 120 && ok < want; i++)
        {
            Vector2 pos;
            if (!SpotFinder.FindBlockFloor(w, 1, 6f, out pos)) continue;
            if ((pos - playerPos).sqrMagnitude < 100f) continue;   // 别在脚边凭空出现

            float r2 = 6f * 6f;
            bool near = false;
            for (int k = 0; k < spots.Count; k++)
                if ((spots[k] - pos).sqrMagnitude < r2) { near = true; break; }
            if (near) continue;

            if (Spawn(pos) == null) continue;
            spots.Add(pos);
            ok++;
        }

        Plugin.Log.LogInfo($"[STEMFishDLC] 幸运方块：第 {depth} 层 {ok}/{want} 个" +
                           (ok == 0 ? "（没找到合法落点）" : ""));
    }

    public static GameObject Spawn(Vector2 pos)
    {
        try
        {
            var go = Utils.Create(Prefab, pos, 0f);
            if (go == null) return null;

            var be = go.GetComponent<BuildingEntity>();
            if (be == null) { UnityEngine.Object.Destroy(go); return null; }

            be.id = Id;
            be.fullName = "幸运方块";
            be.description = "金色，会晃。敲一下试试。";
            be.skipDescriptionSet = true;
            be.health = 2f;                       // 两下就打碎
            be.cantHit = false;
            be.itemsDropOnDestroy = new ItemDrop[0];
            be.alwaysDrop = new ItemDrop[0];
            be.itemCategoriesToAdd = new string[0];
            be.dropChanceMultiplier = 0f;

            if (_sprite == null) _sprite = FishAssets.GetSprite("luckyblock");
            if (_sprite != null)
            {
                var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
                for (int i = 0; i < srs.Length; i++)
                    if (srs[i] != null) srs[i].sprite = _sprite;
            }

            _blocks.Add(new Block { be = be, born = Time.time });
            return go;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 生成幸运方块失败: {e.Message}");
            return null;
        }
    }

    // ==========================================================================
    //  弹幕提示（跟在幸运方块被摧毁的位置上飘）
    //  ---------------------------------------------------------------------------
    //  ★ v0.15.4：以前是一个固定在右下角的屏幕空间 toast，看起来和方块毫无关系。
    //    现在把方块的世界坐标投影到屏幕，文字从那个点往上飘、淡出 —— 玩家一眼
    //    就能把"我砸的是这个方块"和"抽到了什么"对上。
    // ==========================================================================
    private static GameObject _toastRoot;
    private static readonly List<TextMeshProUGUI> _toasts = new List<TextMeshProUGUI>();
    private static readonly List<float> _toastLife = new List<float>();
    private static readonly List<float> _toastAge = new List<float>();
    private static readonly List<Vector3> _toastWorld = new List<Vector3>();

    // 当前这次触发的位置（Trigger 里设，所有效果复用）
    private static Vector2 _fxPos;
    private static bool _fxPosValid;

    private static void Danmaku(string text, Color color)
    {
        Plugin.Log.LogInfo($"[STEMFishDLC] 幸运方块: {text}");
        if (!ToastOn) return;

        try
        {
            EnsureToast();
            if (_toastRoot == null) return;

            TextMeshProUGUI t = null;
            int idx = -1;
            for (int i = 0; i < _toasts.Count; i++)
                if (_toastLife[i] <= 0f) { t = _toasts[i]; idx = i; break; }

            if (t == null && _toasts.Count < 6)
            {
                t = MakeToast(_toasts.Count);
                _toasts.Add(t);
                _toastLife.Add(0f);
                _toastAge.Add(0f);
                _toastWorld.Add(Vector3.zero);
                idx = _toasts.Count - 1;
            }
            if (t == null) { t = _toasts[0]; idx = 0; }

            _toastLife[idx] = 3.2f;
            _toastAge[idx] = 0f;
            _toastWorld[idx] = _fxPosValid ? (Vector3)_fxPos : Vector3.zero;
            t.text = text;
            t.color = color;
            t.gameObject.SetActive(true);

            PlaceToast(idx);        // 先摆到位，别有一帧闪在角落
        }
        catch { }
    }

    private static void PlaceToast(int i)
    {
        var t = _toasts[i];
        if (t == null || t.rectTransform == null) return;

        var cam = Camera.main;
        if (cam == null) { t.gameObject.SetActive(false); return; }

        // 从方块上方 0.9 单位开始，飘 0.45 单位/秒
        var wp = _toastWorld[i] + new Vector3(0f, 0.9f + _toastAge[i] * 0.45f, 0f);
        var sp = cam.WorldToScreenPoint(wp);
        if (sp.z <= 0f) { t.gameObject.SetActive(false); return; }

        if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
        t.rectTransform.anchoredPosition = new Vector2(sp.x, sp.y);
    }

    private static void EnsureToast()
    {
        if (_toastRoot != null) return;
        _toastRoot = new GameObject("STEMFishDLC_LuckyToast");
        UnityEngine.Object.DontDestroyOnLoad(_toastRoot);

        var canvas = _toastRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 29000;      // HUD 之上、暂停菜单之下

        // 每帧把世界坐标投影成屏幕坐标 + 淡出
        _toastRoot.AddComponent<ToastMover>();
    }

    private static TextMeshProUGUI MakeToast(int i)
    {
        var go = new GameObject("Toast" + i, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_toastRoot.transform, false);
        rt.anchorMin = rt.anchorMax = Vector2.zero;    // 左下角为原点 → anchoredPosition 就是屏幕像素
        rt.pivot = new Vector2(0.5f, 0f);              // 以文字下方中点为锚
        rt.sizeDelta = new Vector2(760f, 30f);

        var t = go.AddComponent<TextMeshProUGUI>();
        try { if (DlcPanel.Font != null) t.font = DlcPanel.Font; } catch { }
        t.fontSize = 20;
        t.alignment = TextAlignmentOptions.Center;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        t.text = "";
        return t;
    }

    public class ToastMover : MonoBehaviour
    {
        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < _toasts.Count; i++)
            {
                var t = _toasts[i];
                if (t == null) continue;
                if (_toastLife[i] <= 0f) { if (t.gameObject.activeSelf) t.gameObject.SetActive(false); continue; }

                _toastLife[i] -= dt;
                _toastAge[i] += dt;

                if (_toastLife[i] <= 0f) { t.gameObject.SetActive(false); continue; }

                PlaceToast(i);

                var c = t.color;
                float fadeOut = Mathf.Clamp01(_toastLife[i] / 0.7f);
                float fadeIn = Mathf.Clamp01(_toastAge[i] / 0.12f);
                c.a = fadeOut * fadeIn;
                t.color = c;
            }
        }
    }
}
