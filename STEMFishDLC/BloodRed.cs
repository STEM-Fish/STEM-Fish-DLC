using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  BloodRed - 把主角的血改成「人类 / 守卫」那种红
//  ---------------------------------------------------------------------------
//  原版的黄血其实是**三处独立的东西**，只改一处另两处照样黄（v0.19.2 之前只改了
//  `_BloodDark/_BloodLight` 两个颜色，所以看上去"还是黄的"）：
//
//    ① 肢体血渍贴图：`Limb` 材质里那张 `Texture2D "limbBlood"`，均色 (223,176,0) 黄。
//       它挂在 shader graph 生成的属性上：
//       `_SampleTexture2D_722a3646334c45d58299ef3dd9fd21be_Texture_1_Texture2D`
//       → 名字带哈希，**只能按"当前贴图名叫 limbBlood"来认**，不能写死
//    ② 流血粒子：`BleedParticle.startColor` = (1, 0.788, 0)、
//       `Special/UnderwaterBleedParticle` 同色 → 一起改
//    ③ 落地/溅墙血渍：`blockblood` 的 SpriteRenderer.color = (1, 0.79, 0) 黄；
//       `wallblood` 的 sprite 本身就是黄图。`BleedParticle.isHuman` 决定用哪套
//       （false → wallblood / Special/blockblood；true → wallbloodhuman /
//        Special/blockbloodhuman，红）
//
//  守卫（人类）那套血 = `HumanBlood` 粒子 prefab 的 startColor
//  = (0.717, 0, 0.057)，`GuardScript.AnimalHit` 用的就是它 → 这里照抄这个色。
//
//  为什么能"只改主角"：`Limb.Awake` 里本来就是
//      mat = new Material(renderer.material);  renderer.sharedMaterial = mat;
//  每条肢体各持一份材质克隆，改它不会波及敌人。
//
//  ⚠ 不再挂 `Limb.Awake` 的 postfix（那是全局的，会把所有生物的血都染红）；
//    改成每 0.5 秒扫一次 `PlayerCamera.main.body`，幂等，读档 / 换局自动跟上。
// ============================================================================

public static class BloodRed
{
    public static bool Enabled = true;

    // 深色（干涸/稀薄）与亮色（新鲜）—— 默认值直接取守卫血 (183, 0, 15)
    public static Color Dark = new Color(0.394f, 0.000f, 0.031f, 1f);
    public static Color Light = new Color(0.717f, 0.000f, 0.057f, 1f);

    private static Texture2D _limbTex;          // 皮肤包里的 Blood/limbBlood.png（红）
    private static string _bloodProp;           // 材质里指向 limbBlood 的那个属性名（哈希）
    private static GameObject _wallHuman;       // wallbloodhuman
    private static GameObject _groundHuman;     // Special/blockbloodhuman
    private static FieldInfo _fWall, _fGround;  // BleedParticle 的私有字段

    private static float _next;
    private static bool _logged;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("General", "RedBlood", true,
                "把主角的血改成红色（肢体血渍 + 流血粒子 + 地面/墙面血渍；原版是克隆体的橙黄色）").Value;
            Dark = ParseHex(plugin.Config.Bind("General", "BloodDark", "#650008",
                "血液深色（#RRGGBB）").Value, Dark);
            Light = ParseHex(plugin.Config.Bind("General", "BloodLight", "#B7000F",
                "血液亮色（#RRGGBB）").Value, Light);
        }
        catch { }

        if (!Enabled)
        {
            Plugin.Log.LogInfo("[STEMFishDLC] 红血：关闭");
            return;
        }

        LoadLimbTexture();
        ResolveDecals();
        PatchAmputation();

        Plugin.Log.LogInfo($"[STEMFishDLC] 红血：肢体贴图{(_limbTex != null ? "已载入" : "缺失（只用材质色）")} · " +
                           $"粒子/血渍 → ({Light.r * 255:0},{Light.g * 255:0},{Light.b * 255:0})");
    }

    // ---------------------------------------------------------------------
    //  资源
    // ---------------------------------------------------------------------
    private static void LoadLimbTexture()
    {
        try
        {
            var p = Path.Combine(FishSkin.PackDir, "Blood", "limbBlood.png");
            if (!File.Exists(p))
            {
                Plugin.Log.LogInfo($"[STEMFishDLC] 红血：{p} 不存在，只用材质色");
                return;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(p)))
            {
                UnityEngine.Object.Destroy(tex);
                return;
            }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "limbBlood";
            _limbTex = tex;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 红血：贴图载入失败 {e.Message}");
        }
    }

    private static void ResolveDecals()
    {
        try
        {
            _wallHuman = Resources.Load<GameObject>("wallbloodhuman");
            _groundHuman = Resources.Load<GameObject>("Special/blockbloodhuman");
            _fWall = AccessTools.Field(typeof(BleedParticle), "wallBleed");
            _fGround = AccessTools.Field(typeof(BleedParticle), "groundBleed");
            if (_wallHuman == null || _groundHuman == null)
                Plugin.Log.LogWarning($"[STEMFishDLC] 红血：人类血渍预制体没找到（wall={_wallHuman != null} ground={_groundHuman != null}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 红血：血渍预制体解析失败 {e.Message}");
        }
    }

    // 截肢小游戏的血液图是黄色贴图，换不了图，只能靠 Image.color 乘成红色
    private static void PatchAmputation()
    {
        try
        {
            var m = AccessTools.Method(typeof(AmputationMinigame), "Start");
            if (m == null) return;
            var post = typeof(BloodRed).GetMethod("Amp_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.bloodred").Patch(m, postfix: new HarmonyMethod(post));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 截肢血液上色 hook 失败: {e.Message}");
        }
    }

    private static void Amp_Postfix(AmputationMinigame __instance)
    {
        if (!Enabled || __instance == null) return;
        try
        {
            var img = __instance.bloodImage;
            if (img != null) img.color = new Color(Light.r, Light.g, Light.b, 1f);
        }
        catch { }
    }

    // ---------------------------------------------------------------------
    //  每帧（节流 0.5s）
    // ---------------------------------------------------------------------
    public static void Tick()
    {
        if (!Enabled) return;
        if (Time.realtimeSinceStartup < _next) return;
        _next = Time.realtimeSinceStartup + 0.5f;

        try
        {
            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body == null) return;

            // v0.23：红血是 SF-07 的改造特征；原版三个角色保持黄色 SAW 血
            if (!CharacterSelect.IsSf07(body)) return;

            int mats = 0, parts = 0, decals = 0;

            // ① + ② 肢体材质 / 肢体上的流血粒子
            foreach (var limb in body.GetComponentsInChildren<Limb>(true))
            {
                if (limb == null) continue;

                var sr = limb.GetComponent<SpriteRenderer>();
                var mat = sr != null ? sr.sharedMaterial : null;
                if (mat != null && FixMaterial(mat)) mats++;

                foreach (var ps in limb.GetComponentsInChildren<ParticleSystem>(true))
                    if (TintParticle(ps)) parts++;
            }

            // ③ 贴花：BleedParticle 决定调哪套预制体
            foreach (var bp in body.GetComponentsInChildren<BleedParticle>(true))
                if (FixDecals(bp)) decals++;

            // 顺带把身体上其它黄血粒子（吐血 / 水下流血）也改掉
            foreach (var ps in body.GetComponentsInChildren<ParticleSystem>(true))
                if (TintParticle(ps)) parts++;

            if (!_logged && (mats > 0 || parts > 0 || decals > 0))
            {
                _logged = true;
                Plugin.Log.LogInfo($"[STEMFishDLC] 红血已应用：材质 {mats} 处 · 粒子 {parts} 个 · 血渍 {decals} 个");
            }
        }
        catch (Exception e)
        {
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                Plugin.Log.LogWarning($"[STEMFishDLC] 红血应用失败: {e.Message}");
            }
        }
    }
    private static string _lastError;

    // ---------------------------------------------------------------------
    //  ① 材质：血渍贴图 + 两个血色
    // ---------------------------------------------------------------------
    private static bool FixMaterial(Material mat)
    {
        bool changed = false;
        try
        {
            if (mat.HasProperty("_BloodDark"))
            {
                var c = mat.GetColor("_BloodDark");
                if (Mathf.Abs(c.r - Dark.r) > 0.004f || Mathf.Abs(c.g - Dark.g) > 0.004f || Mathf.Abs(c.b - Dark.b) > 0.004f)
                {
                    mat.SetColor("_BloodDark", Dark);
                    changed = true;
                }
            }
            if (mat.HasProperty("_BloodLight"))
            {
                var c = mat.GetColor("_BloodLight");
                if (Mathf.Abs(c.r - Light.r) > 0.004f || Mathf.Abs(c.g - Light.g) > 0.004f || Mathf.Abs(c.b - Light.b) > 0.004f)
                {
                    mat.SetColor("_BloodLight", Light);
                    changed = true;
                }
            }

            if (_limbTex != null)
            {
                if (string.IsNullOrEmpty(_bloodProp)) _bloodProp = FindBloodProp(mat);
                if (!string.IsNullOrEmpty(_bloodProp) && mat.HasProperty(_bloodProp))
                {
                    var cur = mat.GetTexture(_bloodProp);
                    if (!ReferenceEquals(cur, _limbTex))
                    {
                        mat.SetTexture(_bloodProp, _limbTex);
                        changed = true;
                    }
                }
            }
        }
        catch { }
        return changed;
    }

    // shader graph 烘出来的属性名带哈希，只能按"当前绑的贴图叫 limbBlood"反查
    private static string FindBloodProp(Material mat)
    {
        try
        {
            foreach (var p in mat.GetTexturePropertyNames())
            {
                Texture t = null;
                try { t = mat.GetTexture(p); } catch { }
                if (t != null && t.name == "limbBlood") return p;
            }
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------------
    //  ② 粒子：黄血 → 守卫红
    // ---------------------------------------------------------------------
    private static bool TintParticle(ParticleSystem ps)
    {
        if (ps == null) return false;
        try
        {
            var g = ps.main.startColor;
            if (g.mode != ParticleSystemGradientMode.Color &&
                g.mode != ParticleSystemGradientMode.TwoColors) return false;

            if (!IsCloneBlood(g.colorMax) && !IsCloneBlood(g.colorMin)) return false;

            var main = ps.main;
            main.startColor = new ParticleSystem.MinMaxGradient(Dark, Light);
            return true;
        }
        catch { return false; }
    }

    // 和原版黄血 (1, 0.788, 0) 比对（容差放宽，避免浮点误差漏掉）
    private static bool IsCloneBlood(Color c)
        => c.r > 0.9f && c.g > 0.7f && c.g < 0.88f && c.b < 0.15f;

    // ---------------------------------------------------------------------
    //  ③ 血渍预制体：wallblood → wallbloodhuman，blockblood → blockbloodhuman
    // ---------------------------------------------------------------------
    private static bool FixDecals(BleedParticle bp)
    {
        if (bp == null) return false;
        bool changed = false;
        try
        {
            if (!bp.isHuman)
            {
                bp.isHuman = true;          // 之后再生成的墙/地血渍直接走 human 分支
                changed = true;
            }

            if (_fWall != null && _wallHuman != null)
            {
                var cur = _fWall.GetValue(bp) as GameObject;
                if (cur != null && cur.name.IndexOf("human", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _fWall.SetValue(bp, _wallHuman);
                    changed = true;
                }
            }
            if (_fGround != null && _groundHuman != null)
            {
                var cur = _fGround.GetValue(bp) as GameObject;
                if (cur != null && cur.name.IndexOf("human", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _fGround.SetValue(bp, _groundHuman);
                    changed = true;
                }
            }
        }
        catch { }
        return changed;
    }

    private static Color ParseHex(string s, Color fallback)
    {
        try
        {
            Color c;
            if (!string.IsNullOrEmpty(s) && ColorUtility.TryParseHtmlString(s.Trim(), out c)) return c;
        }
        catch { }
        return fallback;
    }
}
