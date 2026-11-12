using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  GunFx - 射击打击感
//  ---------------------------------------------------------------------------
//  反汇编确认的射击链路：
//    GunScript.Fire(bool suicide) → muzzleParticle.Play() → TurretScript.Shoot(FireInfo)
//    TurretScript.Shoot 内部：Physics2D.RaycastAll(pos, dir, 200, "Ground|Body|Limb|Descriptor")
//
//  ★ 这个游戏用 **URP 2D 光照**（Light2D）。Sprite 只能"自己亮"，不会照亮环境。
//    游戏自己开枪时（TurretScript.Update）：
//        fireLight.intensity = Mathf.Max(0, 80 - timeSinceFired * 300);
//    但那只在炮塔身上 —— 玩家开枪没有光照，所以我们补。
//
//  ★ 作者要求（v0.12.1）：
//    ① 亮的应该是**枪口到命中点这条线**，不是只有枪口
//    ② 枪口别做成闪光弹
//    ③ 命中点不要那么亮，改成**明亮金橙色的像素粒子掉落**
//
//  → 光照改成沿枪线撒一串**低强度**点光源（越远越弱），
//    命中改成像素方块粒子（有重力、会掉、带淡出），只有很弱的一点点光。
// ============================================================================

public static class GunFx
{
    public static bool Enabled = true;

    // 光点尺寸（世界单位）
    public static float MuzzleScale = 0.55f;
    public static float ImpactScale = 0.4f;
    public static bool ImpactBlood = true;
    public static bool Debug;

    // 2D 光照
    public static bool LightEnabled = true;
    public static float LightPower = 1f;     // 强度倍率
    public static float LightRange = 1f;     // 半径倍率（越大越"雾"）
    public static float FalloffOverride = -1f;       // 全局手动值（-1 = 按强度自动）
    // ★ v0.12.7：衰减指数回到"越软"的一端。
    //   URP 2D 的 falloffIntensity 取值 0~1（setter 里 Clamp 死了），
    //   **1.0 = 边缘最柔，0 = 边缘最硬（平顶亮斑）**。
    //   0.10~0.14 那一档正是最硬的一端，所以怎么调都像一块边缘齐整的亮斑。
    public static float TracerFalloff = 1f;          // 枪线：顶满，整条线完全化开
    public static float ImpactFalloff = 0.90f;       // 命中/枪口：留一点点核心，不然"打中了"没实感
    public static int TracerSegments = 1;    // >0 时启用枪线（细节由下面的参数控制）
    public static float TracerSpacing = 0.45f;  // 光点间距（世界单位，越小越连续）
    // 枪线**整条线**的目标亮度（参照 radGlow 0~1、营火 1~2）。
    // v0.12.7 消亮斑时一并压到 0.95，结果线太暗；falloff 已经转到最柔的一端，
    // 现在提亮只会让光晕更通透，不会回到"平顶亮斑"，所以放心给 2.0。
    public static float TracerPeak = 2.0f;
    public static float TracerRadius = 3.4f;    // 枪线单点半径（越大越连绵，但重叠也越多）

    // 像素粒子
    public static int SparkCount = 18;       // 命中时掉几粒
    public static float SparkSpeed = 1f;     // 粒子初速倍率

    private static Sprite _dotSmall;    // 0.14u  枪线/火花
    private static Sprite _dotMid;      // ImpactScale 命中主闪
    private static Sprite _flashBig;    // MuzzleScale 枪口
    private static Sprite _square;      // 像素方块（粒子用）

    private static GameObject _hitFlashPrefab;

    private static readonly int BodyLayer = LayerMask.NameToLayer("Body");
    private static readonly int LimbLayer = LayerMask.NameToLayer("Limb");

    private static int _shotLayerMask = -1;
    private static int _diagLeft;
    private static int _errLeft = 20;
    private static string _lastErr;
    private static bool _fireDiagDone;

    public static void Init(Plugin plugin)
    {
        try
        {
            var cfg = plugin.Config;

            // ★ 先把旧版遗留的 Fx 键清掉，再 Bind 同名键，否则会读到历史残留值。
            //   典型坑：用户 cfg 里躺着 TracerSpacing = 0.55 / TracerRadius = 2.8
            //   （v0.12.6 手写的，代码从来没读过）。一旦现在正式 Bind，就会用这两个
            //   旧值盖掉新默认 0.45 / 3.4 —— 正好都是"更不明显"的方向。
            MigrateLegacyFxKeys(cfg);

            Enabled = cfg.Bind("Fx", "Enabled", true, "射击打击感：枪口/枪线光效 + 命中粒子").Value;
            MuzzleScale = Mathf.Clamp(cfg.Bind("Fx", "MuzzleScale", 0.55f, "枪口闪光大小（世界单位）").Value, 0.1f, 4f);
            ImpactScale = Mathf.Clamp(cfg.Bind("Fx", "ImpactScale", 0.4f, "命中闪光大小（世界单位）").Value, 0.1f, 4f);
            ImpactBlood = cfg.Bind("Fx", "ImpactBlood", true, "命中生物时溅血").Value;
            Debug = cfg.Bind("Fx", "Debug", false, "打印特效生成日志（异常始终记录前 20 条）").Value;

            LightEnabled = cfg.Bind("Fx", "LightEnabled", true, "枪线 2D 光照（URP Light2D）").Value;
            LightPower = Mathf.Clamp(cfg.Bind("Fx", "LightPower", 1f,
                "光照强度倍率。1=默认；嫌亮/像亮斑就 0.5~0.7；嫌没气势就 1.3~1.8。（改完保存立即生效，不用重启）").Value, 0.05f, 6f);
            LightRange = Mathf.Clamp(cfg.Bind("Fx", "LightRange", 1f,
                "光照扩散倍率。1=默认；想更柔更「雾」就 1.4~2.0，想更收束就 0.7。（改完保存立即生效，不用重启）").Value, 0.2f, 6f);

            // ---- 枪线专用旋钮（改完保存立即生效，不用重启）----
            //   ⚠ 故意用 TracerLevel 而不是 TracerPeak：旧版 cfg 里残留的 TracerPeak 行
            //     是历史遗留（代码从来没读过），用同名字会让 Bind 读到那个旧值 1.6，
            //     把新默认值盖掉。换个 key 就彻底断开。
            TracerPeak = Mathf.Clamp(cfg.Bind("Fx", "TracerLevel", 2.0f,
                "枪线整体亮度。1=偏暗 / 2=默认（明显可见）/ 3~4=很亮（近距离可能过曝）。（保存即生效）").Value, 0.1f, 8f);
            TracerFalloff = Mathf.Clamp(cfg.Bind("Fx", "TracerSoftness", 1f,
                "枪线柔和度 0~1。1=最柔（无边缘）/ 0.5=URP 默认 / 0.2 以下会变成硬边亮斑。（保存即生效）").Value, 0f, 1f);
            TracerRadius = Mathf.Clamp(cfg.Bind("Fx", "TracerRadius", 3.4f,
                "枪线单点光半径（世界单位）。越大越连绵，但重叠更多、单点更暗。（保存即生效）").Value, 0.5f, 8f);
            TracerSpacing = Mathf.Clamp(cfg.Bind("Fx", "TracerSpacing", 0.45f,
                "枪线光点间距。越小越连续（等价于更柔），但光源数量变多。（保存即生效）").Value, 0.2f, 2f);
            // 只是个开关了：实际光点数按"距离 ÷ 间距"自动算，不再用这个数
            TracerSegments = Mathf.Clamp(cfg.Bind("Fx", "TracerSegments", 1,
                "枪线光效开关（>0 开启，0=关闭）。实际光点数按距离自动计算，这个数字本身不影响数量。").Value, 0, 16);
            SparkCount = Mathf.Clamp(cfg.Bind("Fx", "SparkCount", 18, "命中时掉几颗像素粒子").Value, 0, 60);
            SparkSpeed = Mathf.Clamp(cfg.Bind("Fx", "SparkSpeed", 1f, "粒子初速倍率").Value, 0.2f, 4f);

            if (!Enabled) { Plugin.Log.LogInfo("[STEMFishDLC] 打击感特效：关闭"); return; }

            _diagLeft = Debug ? 40 : 0;

            BuildSprites();

            try
            {
                _hitFlashPrefab = Resources.Load<GameObject>("Special/HitFlash");
                Diag(_hitFlashPrefab != null
                    ? "[STEMFishDLC] 特效容器: Special/HitFlash"
                    : "[STEMFishDLC] 无 Special/HitFlash，改用自建 renderer");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 载入 HitFlash 失败: {e.Message}"); }

            var harmony = new Harmony("com.stemfish.dlc.gunfx");
            int n = 0;

            var fire = AccessTools.Method(typeof(GunScript), "Fire");
            if (fire != null)
            {
                harmony.Patch(fire, postfix: new HarmonyMethod(
                    typeof(GunFx).GetMethod("Fire_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                n += Harmony.GetPatchInfo(fire)?.Postfixes?.Count ?? 0;
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 GunScript.Fire");

            var shoot = AccessTools.Method(typeof(TurretScript), "Shoot", new[] { typeof(FireInfo) });
            if (shoot != null)
            {
                harmony.Patch(shoot, postfix: new HarmonyMethod(
                    typeof(GunFx).GetMethod("Shoot_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                n += Harmony.GetPatchInfo(shoot)?.Postfixes?.Count ?? 0;
            }
            else Plugin.Log.LogWarning("[STEMFishDLC] 找不到 TurretScript.Shoot(FireInfo)");

            Plugin.Log.LogInfo($"[STEMFishDLC] 打击感就绪 | postfix={n} | 光照×{LightPower:0.0} 半径×{LightRange:0.0} " +
                               $"粒子={SparkCount}");
            Plugin.Log.LogInfo($"[STEMFishDLC] 枪线: 启用={(TracerSegments > 0)} 间距={TracerSpacing:0.00}u " +
                               $"整线亮度={TracerPeak:0.0} 半径={TracerRadius:0.00}u | 自动柔化参照: " +
                               $"强度1→{LightFx.AutoFalloff(1f):0.00} 2.5→{LightFx.AutoFalloff(2.5f):0.00} " +
                               $"5→{LightFx.AutoFalloff(5f):0.00}" +
                               $"{(FalloffOverride >= 0f ? $"（全局锁定 {FalloffOverride:0.00}）" : "")}");            Plugin.Log.LogInfo($"[STEMFishDLC] 柔化: 枪线={TracerFalloff:0.00} 命中={ImpactFalloff:0.00}" +
                               $"{(FalloffOverride >= 0f ? $" | 已被全局 {FalloffOverride:0.00} 覆盖" : "")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 打击感初始化失败: {e}");
        }
    }

    // ==========================================================================
    //  旧版 Fx 键的一次性迁移
    //  ---------------------------------------------------------------------------
    //  v0.12.0~v0.12.8 期间这些键只存在于 cfg 里（用户手写或早期版本写过），
    //  代码从未 Bind 过。v0.12.9 起正式启用同名键，所以必须先把残留删掉，
    //  否则读到的是旧值。
    //
    //  ★ 只在第一次执行（用 _cfgMigrated129 标记）。之后用户改值不会被删。
    //  ★ FalloffOverride / ImpactFalloff 也一起删：前者是全局强制覆盖，
    //    残留的 0.10 一旦被读到会把所有光的柔化设置全部压回"最硬"。
    // ==========================================================================
    private static void MigrateLegacyFxKeys(BepInEx.Configuration.ConfigFile cfg)
    {
        const string Flag = "_cfgMigrated129";
        try
        {
            bool done = cfg.Bind("Fx", Flag, false, "内部标记，请勿修改").Value;
            if (done) return;

            foreach (var key in new[] { "TracerPeak", "TracerFalloff", "TracerSpacing",
                                        "TracerRadius", "FalloffOverride", "ImpactFalloff" })
            {
                try { cfg.Remove(new BepInEx.Configuration.ConfigDefinition("Fx", key)); } catch { }
            }

            cfg.Bind("Fx", Flag, false, "内部标记，请勿修改").Value = true;
            try { cfg.Save(); } catch { }
            Plugin.Log.LogInfo("[STEMFishDLC] 已清理旧版遗留的 Fx 配置项（改用新的语义化键）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 清理旧 Fx 键失败: {e.Message}");
        }
    }

    // 贴图
    // ==========================================================================
    private static void BuildSprites()
    {
        var glow = MakeRadial(32);
        _flashBig = MakeSprite(glow, MuzzleScale, "stemfish_flash");
        _dotMid = MakeSprite(glow, ImpactScale, "stemfish_mid");
        _dotSmall = MakeSprite(glow, 0.14f, "stemfish_dot");

        // 像素方块：4x4 纯白实心，最近邻放大 → 真正的"像素"颗粒
        var sq = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        sq.filterMode = FilterMode.Point;                 // ★ 最近邻，保持硬边
        sq.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[16];
        for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
        sq.SetPixels32(px);
        sq.Apply();
        sq.name = "stemfish_square";
        _square = Sprite.Create(sq, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f),
                                4f / 0.13f, 0, SpriteMeshType.FullRect);    // ≈0.13u 一个方块（像素块要够大才看得清）
        _square.name = "stemfish_square";

        Plugin.Log.LogInfo($"[STEMFishDLC] 光点就绪: 枪口={WorldSize(_flashBig):0.00}u " +
                           $"命中={WorldSize(_dotMid):0.00}u 点={WorldSize(_dotSmall):0.00}u " +
                           $"方块={WorldSize(_square):0.000}u");
    }

    private static float WorldSize(Sprite s) => s.rect.width / s.pixelsPerUnit;

    private static Texture2D MakeRadial(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var px = new Color32[size * size];
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half, dy = (y - half) / half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - d);
                a = a * a * a;                          // 三次衰减 → 中心集中，边缘更柔
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        tex.SetPixels32(px);
        tex.Apply();
        tex.name = "stemfish_glow_tex";
        return tex;
    }

    private static Sprite MakeSprite(Texture2D tex, float worldSize, string name)
    {
        worldSize = Mathf.Max(0.02f, worldSize);
        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                               new Vector2(0.5f, 0.5f), tex.width / worldSize, 0, SpriteMeshType.FullRect);
        sp.name = name;
        return sp;
    }

    // ==========================================================================
    //  枪口：小而短的闪光（不再是一团闪光弹）
    // ==========================================================================
    private static void Fire_Postfix(GunScript __instance)
    {
        if (!Enabled || __instance == null) return;
        try
        {
            Vector3 muzzle = MuzzlePosition(__instance);

            if (!_fireDiagDone)
            {
                _fireDiagDone = true;
                Plugin.Log.LogInfo($"[特效] 首次开火 | gun={__instance.name} " +
                                   $"barrel={((__instance.barrel != null) ? __instance.barrel.name : "null")} " +
                                   $"枪口={muzzle} 角度={__instance.transform.eulerAngles.z:0.0}°");
            }

            // 枪口：小而短（0.10s），强度按游戏自己的量级（radGlow 0~1、营火 1~2），
            // 不是炮塔那个 80。grow 1.8 → 光晕在 0.13 秒里张开到 1.8 倍，是"推出去"的
            Spawn(_flashBig, muzzle, new Color(1f, 0.92f, 0.72f, 1f), "枪口", 0.10f, 1f);
            SpawnLight(muzzle, new Color(1f, 0.78f, 0.50f), 1.4f, 3.6f, 0.13f, ImpactFalloff, 1.8f);

            EnsureMuzzleParticle(__instance);
        }
        catch (Exception e) { Err("枪口闪光", e); }
    }

    // 枪口位置：有 barrel 用它，没有就沿枪管推 0.45 个枪长
    private static Vector3 MuzzlePosition(GunScript gun)
    {
        if (gun.barrel != null)
        {
            var dir = gun.barrel.right != Vector3.zero ? gun.barrel.right : gun.transform.right;
            return gun.barrel.position + dir * 0.12f;
        }

        var sr = gun.GetComponentInChildren<SpriteRenderer>();
        float len = (sr != null && sr.sprite != null)
            ? sr.sprite.rect.width / Mathf.Max(1f, sr.sprite.pixelsPerUnit)
            : 2f;
        return (Vector3)gun.transform.position + gun.transform.right * (len * 0.45f);
    }

    private static readonly HashSet<int> _muzzleFixed = new HashSet<int>();

    private static void EnsureMuzzleParticle(GunScript gun)
    {
        var ps = gun.muzzleParticle;
        if (ps == null) { Diag($"[特效] {gun.name}: 无 muzzleParticle"); return; }
        if (!_muzzleFixed.Add(ps.GetInstanceID())) return;
        try
        {
            Diag($"[特效] {gun.name}: muzzleParticle active={ps.gameObject.activeInHierarchy} " +
                 $"playing={ps.isPlaying} local={ps.transform.localPosition}");
        }
        catch { }
    }

    // ==========================================================================
    //  命中：金橙色像素粒子为主，闪光很小很弱
    // ==========================================================================
    private static void Shoot_Postfix(FireInfo info)
    {
        if (!Enabled || info == null) return;
        try
        {
            if (_shotLayerMask < 0)
                _shotLayerMask = LayerMask.GetMask("Ground", "Body", "Limb", "Descriptor");
            if (_shotLayerMask == 0) return;

            var hits = Physics2D.RaycastAll(info.pos, info.dir, 200f, _shotLayerMask);
            if (hits == null || hits.Length == 0) return;

            float best = float.MaxValue;
            RaycastHit2D chosen = default;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.collider == null) continue;
                if (info.ignoreTrans != null && h.transform == info.ignoreTrans) continue;
                if (h.distance <= 0.05f) continue;
                if (h.distance < best) { best = h.distance; chosen = h; found = true; }
            }
            if (!found) return;

            Vector2 p = chosen.point;
            var nd = info.dir.normalized;
            var point = new Vector3(p.x - nd.x * 0.04f, p.y - nd.y * 0.04f, 0f);
            bool flesh = IsFlesh(chosen);

            Diag($"[特效] 命中 {(flesh ? "生物" : "结构")} @ {point.x:0.00},{point.y:0.00} d={best:0.00} obj={chosen.collider.name}");

            // ---- ① 枪线光照：从枪口到命中点，越远越暗 ----
            SpawnTracer(info.pos, point);

            // ---- ② 命中闪光：很小很弱，光晕向外扩散（grow 2.1 是"扩散感"的来源）----
            // 「急速下坠」是娱乐模式：整体放大一圈，让每一枪都更"有回馈"
            float hype = HypeMode.Active ? HypeMode.HitFxScale : 1f;
            Spawn(_dotMid, point, flesh ? new Color(1f, 0.48f, 0.38f, 1f) : new Color(1f, 0.88f, 0.58f, 1f),
                  "命中", 0.18f * hype, 1f);
            SpawnLight(point, flesh ? new Color(1f, 0.46f, 0.30f) : new Color(1f, 0.70f, 0.40f),
                       1.05f * hype, 4.0f * hype, 0.18f * hype, ImpactFalloff, 2.1f);
            // 再加一层"余辉"：半径很大、强度极低（约核心的 1/6）、一路扩散到 2.2 倍。
            // 它的作用不是照亮，是让命中处周围的空气"渗"一点点颜色 ——
            // 光有这一层，观感才会从"一块亮斑"变成"一团散开的光"。
            SpawnLight(point, flesh ? new Color(1f, 0.40f, 0.26f) : new Color(1f, 0.62f, 0.34f),
                       0.18f * hype, 6.0f * hype, 0.26f * hype, 1f, 2.2f);

            // ---- ③ 主效果：金橙色像素粒子掉落 ----
            SpawnPixels(point, chosen.normal, flesh, hype);
        }
        catch (Exception e) { Err("命中特效", e); }
    }

    private static bool IsFlesh(RaycastHit2D hit)
    {
        try
        {
            int layer = hit.collider.gameObject.layer;
            if (layer == BodyLayer || layer == LimbLayer) return true;
            if (hit.collider.GetComponentInParent<Limb>() != null) return true;
            if (hit.collider.GetComponentInParent<Body>() != null) return true;
            return false;
        }
        catch { return false; }
    }

    // ==========================================================================
    //  枪线光照：沿枪口→命中点撒 N 个低强度点光源
    //  ---------------------------------------------------------------------------
    //  单个点光源只能照亮一个圆，想表现"一条线"就得沿路径放多个，
    //  并且**离枪口越远强度越低**（能量沿途衰减），否则整条线一样亮会很假。
    // ==========================================================================
    private static void SpawnTracer(Vector2 from, Vector3 to)
    {
        if (TracerSegments <= 0) return;

        try
        {
            Vector3 a = new Vector3(from.x, from.y, 0f);
            Vector3 b = to;
            float dist = Vector3.Distance(a, b);
            if (dist < 0.4f) return;                     // 贴脸开枪就不撒了

            // ★ 段数按**距离**算，让光点间距恒定 —— 这才是"线性发光"的关键。
            //   固定 6 段时，短枪线会挤在一起、长枪线会断成一串孤立的点。
            // ★ 上限必须够高：15 单位 / 0.45 间距 = 33 段。上限一旦截断，
            //   实际间距被拉大 → 出现暗缝，而且单点强度仍按原间距算（更暗）。
            float spacing = Mathf.Max(0.28f, TracerSpacing);
            int n = Mathf.Clamp(Mathf.RoundToInt(dist / spacing), 2, 40);

            // 真实间距（被上限截断后可能比设定值大）→ 叠加补偿要用它，不能用设定值
            spacing = dist / n;

            // ---- 光照：沿线的低强度点光源 ----
            if (LightEnabled)
            {
                // ★★ 重叠补偿（"亮斑带"的成因）
                //   Light2D 是加法混合。光沿直线密集排列时，线上任意一点都被多个光源覆盖：
                //       覆盖数 ≈ 2 × 半径 / 间距          （半径 3.4 / 间距 0.45 → 约 15 个）
                //   每个光源在该点的贡献还要乘上它自己的衰减值，整条半径上的平均值可以按
                //       平均衰减 ≈ 1 - 0.5 × falloff      （0=近乎矩形→1.0，1=完全化开→0.5）
                //   估算。→ 反解：单点 = 目标线亮度 / (覆盖数 × 平均衰减)
                //   这样"线上总亮度"才等于 TracerPeak，和覆盖密度**无关**：
                //   想更柔就加密光点（覆盖越多、单点越暗，叠加越平滑），亮度不会跟着涨。
                float baseRadius = Mathf.Max(0.5f, TracerRadius);
                float overlapN = Mathf.Max(1f, (2f * baseRadius) / Mathf.Max(0.05f, spacing));
                float avgFalloff = Mathf.Clamp(1f - 0.5f * Mathf.Clamp01(TracerFalloff), 0.35f, 1f);
                float perDot = TracerPeak / Mathf.Max(1f, overlapN * avgFalloff);

                for (int i = 1; i < n; i++)
                {
                    float t = i / (float)n;              // 0~1，不含两端
                    var pos = Vector3.Lerp(a, b, t);

                    // 能量沿途衰减（近枪口 1.0 → 远端 0.35），梯度拉大才像"射出去"
                    float fall = Mathf.Lerp(1f, 0.35f, t);
                    // 中段略亮，让整条线更连贯
                    float bump = 0.80f + 0.30f * Mathf.Sin(t * Mathf.PI);

                    // 半径近端略大、远端收窄 → 视觉上像"射出去"的锥形
                    float radius = Mathf.Lerp(baseRadius, baseRadius * 0.80f, t);
                    float life = Mathf.Lerp(0.20f, 0.13f, t);

                    float peak = perDot * fall * bump;
                    // grow 1.25：枪线要的是"一条线"，每个点只轻轻张开；
                    //   张得太多（1.6）等于把能量摊薄，线就看不出来了
                    SpawnLight(pos, new Color(1f, 0.72f, 0.42f), peak, radius, life, TracerFalloff, 1.25f);
                }
            }

            // ---- 视觉拖尾：小光点做"子弹轨迹"，比纯光照更锐利 ----
            int dotN = Mathf.Clamp(n / 2, 2, 14);
            for (int i = 1; i < dotN; i++)
            {
                float t = i / (float)dotN;
                var pos = Vector3.Lerp(a, b, t);
                Spawn(_dotSmall, pos, new Color(1f, 0.92f, 0.7f, 1f),
                      "枪线", 0.06f, 0.75f * Mathf.Lerp(1f, 0.45f, t));
            }
        }
        catch (Exception e) { Err("枪线光照", e); }
    }

    // ==========================================================================
    //  像素粒子：金橙色小方块，带重力、初速、淡出
    //  ---------------------------------------------------------------------------
    //  用 4x4 纯白 + FilterMode.Point 的方块 sprite，染成金橙。
    //  不用 Unity 的 ParticleSystem：那要配 prefab/材质，代码建很容易不显示；
    //  这里每粒一个 GameObject + 自己的 PixelParticle 脚本，行为完全可控。
    // ==========================================================================
    private static void SpawnPixels(Vector3 pos, Vector2 normal, bool flesh, float scale = 1f)
    {
        // 「急速下坠」这类娱乐模式会把 scale 调大：数量线性放大，尺寸温和放大
        int n = Mathf.RoundToInt((SparkCount + UnityEngine.Random.Range(-2, 3)) * Mathf.Max(1f, scale));
        if (n <= 0) return;
        float sizeMul = Mathf.Lerp(1f, 1.35f, Mathf.Clamp01(scale - 1f));

        var palette = flesh
            ? new[] { new Color(1f, 0.62f, 0.22f), new Color(1f, 0.78f, 0.35f), new Color(0.95f, 0.30f, 0.20f) }
            : new[] { new Color(1f, 0.72f, 0.25f), new Color(1f, 0.88f, 0.45f), new Color(1f, 0.55f, 0.16f) };

        for (int i = 0; i < n; i++)
        {
            try
            {
                var go = new GameObject("stemfish_px");
                go.transform.position = pos + new Vector3(
                    UnityEngine.Random.Range(-0.06f, 0.06f),
                    UnityEngine.Random.Range(-0.06f, 0.06f), 0f);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = _square;
                sr.drawMode = SpriteDrawMode.Simple;
                sr.color = palette[UnityEngine.Random.Range(0, palette.Length)];
                CopySorting(sr);

                float sc = UnityEngine.Random.Range(0.6f, 1.9f) * sizeMul;      // 尺寸随机
                go.transform.localScale = new Vector3(sc, sc, 1f);

                // 初速：以法线方向为主 + 随机扩散；有重力所以会"掉落"
                Vector2 dir = (normal * UnityEngine.Random.Range(1.2f, 2.6f)
                             + UnityEngine.Random.insideUnitCircle * 2.2f).normalized;
                float speed = UnityEngine.Random.Range(2.2f, 5.5f) * SparkSpeed;

                var px = go.AddComponent<PixelParticle>();
                px.Setup(dir * speed, UnityEngine.Random.Range(0.35f, 0.8f));
            }
            catch (Exception e) { Err("像素粒子", e); break; }
        }
    }

    // ==========================================================================
    //  生成通用光点
    // ==========================================================================
    private static void Spawn(Sprite sp, Vector3 pos, Color color, string tag)
        => Spawn(sp, pos, color, tag, 0.15f, 1f);

    private static void Spawn(Sprite sp, Vector3 pos, Color color, string tag, float life, float alpha)
    {
        if (sp == null) return;

        // 用游戏自己的 Special/HitFlash 当容器：排序层/材质继承，一定画得出来；
        // 但关掉它自带的 HitFlash（只有 0.15s、alpha 上限 0.6），生命周期我们接管。
        if (_hitFlashPrefab != null)
        {
            try
            {
                var go = UnityEngine.Object.Instantiate(_hitFlashPrefab, pos, Quaternion.identity);
                go.name = "stemfish_fx_" + tag;

                var hf = go.GetComponent<HitFlash>();
                if (hf != null) hf.enabled = false;

                var sr = go.GetComponent<SpriteRenderer>();
                if (sr == null) sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sp;
                sr.drawMode = SpriteDrawMode.Simple;
                sr.color = new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha));

                go.AddComponent<GlowFx>().Setup(life, Mathf.Clamp01(alpha));
                return;
            }
            catch (Exception e) { Err($"{tag}/HitFlash容器", e); }
        }

        try
        {
            var go = new GameObject("stemfish_fx_" + tag);
            go.transform.position = pos;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            sr.drawMode = SpriteDrawMode.Simple;
            sr.color = new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha));
            CopySorting(sr);
            go.AddComponent<GlowFx>().Setup(life, Mathf.Clamp01(alpha));
        }
        catch (Exception e) { Err($"{tag}/自建", e); }
    }

    // falloff < 0 时：先看全局 FalloffOverride，再退到自动
    // grow > 1 时，光晕在生命期内向外扩张（扩散感）
    private static void SpawnLight(Vector3 pos, Color color, float peak, float radius, float life,
                                   float falloff = -1f, float grow = 1f)
    {
        if (!LightEnabled) return;
        float f = FalloffOverride >= 0f ? FalloffOverride : falloff;
        try { LightFx.Spawn(pos, color, peak * LightPower, radius * LightRange, life, f, grow); }
        catch (Exception e) { Err("SpawnLight", e); }
    }

    private static bool _sortCached;
    private static string _sortLayer = "Default";
    private static int _sortOrder = 200;

    private static void CopySorting(SpriteRenderer sr)
    {
        if (!_sortCached)
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body != null)
                {
                    var refSr = body.GetComponentInChildren<SpriteRenderer>();
                    if (refSr != null)
                    {
                        _sortLayer = refSr.sortingLayerName;
                        _sortOrder = refSr.sortingOrder + 200;
                        _sortCached = true;
                    }
                }
            }
            catch { }
        }
        sr.sortingLayerName = _sortLayer;
        sr.sortingOrder = _sortOrder;
    }

    // ==========================================================================
    //  F5 自检：枪口 + 枪线 + 像素粒子，一次看全
    // ==========================================================================
    public static void TestAtPlayer()
    {
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) { Plugin.Log.LogWarning("[特效测试] 不在世界里"); return; }

            var p = body.transform.position;
            Plugin.Log.LogInfo($"[特效测试] 玩家 {p} | 亮度×{LightPower:0.00} 扩散×{LightRange:0.00} " +
                               $"柔化={TracerFalloff:0.00}/{ImpactFalloff:0.00}");

            // ★ 自检必须和实战**同一套参数**，否则按 F5 看到的好看、开枪却是另一回事
            Spawn(_flashBig, p + new Vector3(0f, 1.0f, 0f), new Color(1f, 0.92f, 0.72f, 1f), "测试枪口", 0.3f, 1f);
            SpawnLight(p + new Vector3(0f, 1.0f, 0f), new Color(1f, 0.78f, 0.50f), 1.4f, 3.6f, 0.3f, ImpactFalloff, 1.8f);

            // 枪线：往右 6 单位
            SpawnTracer(new Vector2(p.x, p.y), p + new Vector3(6f, 0f, 0f));

            // 命中：和实战一样弱的光 + 像素粒子
            SpawnLight(p + new Vector3(6f, 0f, 0f), new Color(1f, 0.70f, 0.40f), 1.05f, 4.0f, 0.3f, ImpactFalloff, 2.1f);
            SpawnPixels(p + new Vector3(6f, 0f, 0f), Vector2.left, false);

            Plugin.Log.LogInfo("[特效测试] 已生成：枪口 + 枪线 + 像素粒子");
        }
        catch (Exception e) { Plugin.Log.LogError($"[特效测试] 失败: {e}"); }
    }

    // ==========================================================================
    //  参数热重载
    //  ---------------------------------------------------------------------------
    //  调光照观感最贵的一步是"改参数 → 关游戏 → 重新进世界 → 开枪再看"。
    //  这里每 1 秒比对一次 BepInEx 配置文件的修改时间，变了就重读 Fx 段的
    //  观感旋钮 —— 改完 cfg 保存，下一枪立刻用新参数，不用重启。
    //  （开关类 / 粒子数这类需要重建状态的仍然要重启）
    // ==========================================================================
    private static DateTime _cfgStamp;
    private static float _cfgTimer;
    private static bool _cfgStampInit;

    public static void Tick()
    {
        _cfgTimer += Time.unscaledDeltaTime;
        if (_cfgTimer < 1f) return;
        _cfgTimer = 0f;

        try
        {
            var path = Path.Combine(Paths.ConfigPath, "com.stemfish.dlc.cfg");
            if (!File.Exists(path)) return;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (!_cfgStampInit) { _cfgStampInit = true; _cfgStamp = stamp; return; }
            if (stamp == _cfgStamp) return;
            _cfgStamp = stamp;

            float p = Mathf.Clamp(ReadCfg(path, "LightPower", LightPower), 0.05f, 6f);
            float r = Mathf.Clamp(ReadCfg(path, "LightRange", LightRange), 0.2f, 6f);
            float lv = Mathf.Clamp(ReadCfg(path, "TracerLevel", TracerPeak), 0.1f, 8f);
            float sf = Mathf.Clamp(ReadCfg(path, "TracerSoftness", TracerFalloff), 0f, 1f);
            float tr = Mathf.Clamp(ReadCfg(path, "TracerRadius", TracerRadius), 0.5f, 8f);
            float ts = Mathf.Clamp(ReadCfg(path, "TracerSpacing", TracerSpacing), 0.2f, 2f);

            bool changed =
                !Mathf.Approximately(p, LightPower) || !Mathf.Approximately(r, LightRange) ||
                !Mathf.Approximately(lv, TracerPeak) || !Mathf.Approximately(sf, TracerFalloff) ||
                !Mathf.Approximately(tr, TracerRadius) || !Mathf.Approximately(ts, TracerSpacing);
            if (!changed) return;

            LightPower = p;
            LightRange = r;
            TracerPeak = lv;
            TracerFalloff = sf;
            TracerRadius = tr;
            TracerSpacing = ts;
            Plugin.Log.LogInfo($"[STEMFishDLC] 光照参数已热更新: 全局×{LightPower:0.00}/扩散×{LightRange:0.00} | " +
                               $"枪线亮度={TracerPeak:0.00} 柔度={TracerFalloff:0.00} " +
                               $"半径={TracerRadius:0.00}u 间距={TracerSpacing:0.00}u");
        }
        catch { }
    }

    // 极简 ini 取值：不依赖 ConfigFile 的缓存状态，外部改文件也能读到最新值
    private static float ReadCfg(string path, string key, float def)
    {
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var s = raw.Trim();
                if (s.Length == 0 || s[0] == '#' || s[0] == '[') continue;
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(s.Substring(0, eq).Trim(), key, StringComparison.Ordinal)) continue;

                float v;
                if (float.TryParse(s.Substring(eq + 1).Trim(), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out v))
                    return v;
            }
        }
        catch { }
        return def;
    }

    private static void Diag(string msg)
    {
        if (_diagLeft <= 0) return;
        _diagLeft--;
        Plugin.Log.LogInfo(msg);
    }

    private static void Err(string where, Exception e)
    {
        var m = $"{where}: {e.GetType().Name}: {e.Message}";
        if (m == _lastErr || _errLeft <= 0) return;
        _lastErr = m;
        _errLeft--;
        Plugin.Log.LogWarning($"[STEMFishDLC] 特效失败 {m}\n{e.StackTrace}");
    }
}
