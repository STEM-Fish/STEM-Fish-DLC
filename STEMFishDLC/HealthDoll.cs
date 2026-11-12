using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  HealthDoll - 右上角人体图（塔科夫风格：按部位实时上色）
//  ---------------------------------------------------------------------------
//  作者要求：
//    ① 放在屏幕右上角、暂停菜单「存档」按钮下方
//    ② 像塔科夫那样一眼看出哪个部位出血/骨折/快废了
//    ③ **不能把颜色画进贴图** —— 轮廓图作者以后会改，配色必须走运行时填充
//    ④ 要能区分上臂/前臂/手、大腿/小腿/脚（v0.13.1 细化）
//    ⑤ 和战术终端一样，别的界面弹出来时隐藏
//
//  ★ 为什么是"15 个 Image / 9 张图"
//    左右（近侧/远侧）只是水平镜像的位置，形状一样 → 9 张贴图够用：
//    head / thorax / stomach / arm_u / arm_d / hand / leg_u / leg_d / foot
//    但每个部位要**独立染色**，所以 Image 是 15 个（左右各一个）。
//
//  ★ 部位归类不猜名字，先用 Limb 的布尔字段：
//    isHead / isAbdomen / isArm / isLegLimb。
//    只有"上臂 vs 前臂 vs 手"这类细分才需要看名字（游戏用
//    UpArmF / DownArmF / HandF / ThighF / ShinF / FootF 这种 F/B 后缀）。
//
//  ★ 兜底链：如果某个部位在游戏里根本不存在（比如没有独立的 Shin），
//    那一格不隐藏，而是**沿用父部位的颜色**（小腿跟大腿走），
//    这样人形始终完整，不会缺一块。
// ============================================================================

public static class HealthDoll
{
    public static bool Enabled = true;
    public static float Scale = 0.7f;
    public static float OffsetX = -26f;
    public static float OffsetY = -84f;      // 存档按钮 高44 + 上边距26 + 间隔14

    // ---- 逻辑槽（15 个）----
    private const int S_Head = 0, S_Thorax = 1, S_Stomach = 2;
    private const int S_ArmU_F = 3, S_ArmU_B = 4, S_ArmD_F = 5, S_ArmD_B = 6, S_Hand_F = 7, S_Hand_B = 8;
    private const int S_LegU_F = 9, S_LegU_B = 10, S_LegD_F = 11, S_LegD_B = 12, S_Foot_F = 13, S_Foot_B = 14;
    private const int Slots = 15;

    // 某个槽没有对应肢体时，沿这条链向上借用颜色（-1 = 到头了）
    private static readonly int[] Fallback =
    {
        -1, -1, -1,                                   // 头/胸/腹
        S_Thorax, S_Thorax,                           // 上臂 → 胸
        S_ArmU_F, S_ArmU_B,                           // 前臂 → 上臂
        S_ArmD_F, S_ArmD_B,                           // 手   → 前臂
        S_Stomach, S_Stomach,                         // 大腿 → 腹
        S_LegU_F, S_LegU_B,                           // 小腿 → 大腿
        S_LegD_F, S_LegD_B,                           // 脚   → 小腿
    };

    // ---------------- 图集（textures/doll_atlas.png） ----------------
    //  ★ 所有部位装在**一张 64×64 的图**里，代码按下面的区域切。
    //    改贴图只需要换这一张，不用管 9 个独立文件的尺寸/PPU ——
    //    也就不会再出现"改完图以后人偶整体变大变小"的放缩问题。
    //    坐标是**左上原点**、单位像素；Unity 纹理是左下原点，切的时候会自动翻转 y。
    private class Part
    {
        public string Name;          // UI 节点名（也用于日志）
        public int AX, AY, AW, AH;   // 图集里的区域
        public float X, Y;           // 画布上的位置（左上原点）
        public int Slot;
    }

    private const int AtlasW = 160, AtlasH = 197;

    //  ★ v0.13.13：切分从"矩形"改成**沿身体的 BFS 分区**（geodesic Voronoi）。
    //    矩形切法会把大腿像素算进手臂、把肩膀算进头，颜色就会串到别的部位上；
    //    BFS 只沿不透明像素扩张，手臂不会越过空气去抢腿。
    //    生成器：tools/dollgen（改完 bodypart.png 重跑一次即可）。
    private static readonly Part[] Parts =
    {
        new Part { Name="head",    AX=  0, AY=  0, AW=54, AH=44, X=18f, Y=  2f, Slot=S_Head },
        new Part { Name="thorax",  AX= 55, AY=  0, AW=43, AH=45, X=24f, Y= 45f, Slot=S_Thorax },
        new Part { Name="stomach", AX= 99, AY=  0, AW=42, AH=34, X=24f, Y= 88f, Slot=S_Stomach },

        new Part { Name="arm_u_f", AX= 33, AY= 46, AW=21, AH=41, X=64f, Y= 49f, Slot=S_ArmU_F },
        new Part { Name="arm_u_b", AX=142, AY=  0, AW=17, AH=43, X= 8f, Y= 50f, Slot=S_ArmU_B },
        new Part { Name="arm_d_f", AX= 55, AY= 46, AW=17, AH=38, X=74f, Y= 87f, Slot=S_ArmD_F },
        new Part { Name="arm_d_b", AX=  0, AY= 46, AW=16, AH=36, X= 3f, Y= 89f, Slot=S_ArmD_B },
        new Part { Name="hand_f",  AX= 73, AY= 46, AW=16, AH=15, X=77f, Y=123f, Slot=S_Hand_F },
        new Part { Name="hand_b",  AX= 17, AY= 46, AW=15, AH=15, X= 0f, Y=126f, Slot=S_Hand_B },

        new Part { Name="leg_u_f", AX= 26, AY= 98, AW=21, AH=56, X=53f, Y=106f, Slot=S_LegU_F },
        new Part { Name="leg_u_b", AX= 90, AY= 46, AW=22, AH=51, X=16f, Y=108f, Slot=S_LegU_B },
        new Part { Name="leg_d_f", AX= 48, AY= 98, AW=22, AH=49, X=54f, Y=161f, Slot=S_LegD_F },
        new Part { Name="leg_d_b", AX=113, AY= 46, AW=22, AH=49, X=14f, Y=157f, Slot=S_LegD_B },
        new Part { Name="foot_f",  AX= 71, AY= 98, AW=25, AH=21, X=57f, Y=208f, Slot=S_Foot_F },
        new Part { Name="foot_b",  AX=  0, AY= 98, AW=25, AH=22, X= 6f, Y=205f, Slot=S_Foot_B },
    };

    // 尾巴：纯装饰，固定半透明灰，不参与健康染色（也不做出血闪烁）
    // 灰色原本就画在这张图里，生成器按"灰 = 尾巴"单独抠出来，画在最上层。
    private const int TailAX = 0, TailAY = 155, TailAW = 98, TailAH = 42;
    private const float TailX = 41f, TailY = 120f;

    private const float CanvasW = 140f, CanvasH = 230f;

    // 没受伤时整体压暗/透明一点，受伤才"亮起来"（作者要求）
    private const float HealthyAlpha = 0.38f;

    // 受伤配色：**灰白 → 淡卡其 → 琥珀 → 橙红 → 红**，断肢黑
    //  ★ 健康端不用绿色：饱和绿在右上角太扎眼，而且和"安全/正常"的直觉冲突。
    //    满血给灰白（近乎无彩色），伤得越重才越往暖色/红色走 ——
    //    这样"有没有事"是靠**有没有颜色**看出来的，不是靠色相辨识。
    private static readonly Color Good = new Color(0.80f, 0.82f, 0.85f, 1f);   // 灰白 #CCD1D9
    private static readonly Color Fair = new Color(0.82f, 0.80f, 0.66f, 1f);   // 淡卡其 #D1CCA8
    private static readonly Color Hurt = new Color(0.88f, 0.68f, 0.32f, 1f);   // 琥珀 #E0AE52
    private static readonly Color Bad  = new Color(0.86f, 0.44f, 0.24f, 1f);   // 橙红 #DB703D
    private static readonly Color Crit = new Color(0.82f, 0.24f, 0.20f, 1f);   // 红   #D13D33
    private static readonly Color Gone = new Color(0.12f, 0.12f, 0.14f, 1f);

    private static GameObject _root;
    private static Image[] _imgs;
    private static bool _built;
    private static bool _visible = true;
    private static float _t;

    // ---- 左侧两根竖条：声音 / 体力 ----
    //  ★ 纯装饰性 HUD：声音条 = 当前噪音（移动速度 + 开枪脉冲），体力条 = Body.stamina(0~100)
    private static Image _sndFill, _stmFill;
    private static Sprite _barSprite;
    private static float _noisePulse;      // 开枪/受击时打满一下，然后衰减
    private static Vector3 _lastPos;
    private static bool _hasLastPos;
    private static float _noise;           // 0~1，做了平滑

    private static readonly float[] _sum = new float[Slots];
    private static readonly int[] _cnt = new int[Slots];
    private static readonly bool[] _gone = new bool[Slots];
    private static readonly float[] _health = new float[Slots];
    private static readonly float[] _bleedSum = new float[Slots];
    private static readonly float[] _bleed = new float[Slots];
    private static readonly Color BleedHi = new Color(1f, 0.16f, 0.16f, 1f);
    private static readonly Color[] _lastColor = new Color[Parts.Length];
    private static bool _warnedNoArt;
    private static bool _diagOnce;
    private static string _lastWarn;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("HUD", "HealthDoll", true,
                "右上角人体图（按部位显示受伤）").Value;
            // ★ 键名换成 HealthDollScaleV2：v0.13.12 换了整套人偶美术，
            //   画布从 84×92 变成 140×230。旧键里存的 1.6 会把新图放到
            //   224×368（半个屏幕），所以干脆用新键，默认 0.7。
            var dollEntry = plugin.Config.Bind("HUD", "HealthDollScaleV2", 0.8f,
                "人体图缩放（1 = 140×230 像素原尺寸；0.8 约 112×184）");
            Scale = Mathf.Clamp(dollEntry.Value, 0.3f, 2f);

            // v0.18.10：作者觉得人偶和两侧条状偏小 → 默认 0.7 → 0.8（老 cfg 一次性迁移）
            try
            {
                var mk = plugin.Config.Bind("HUD", "DollScaleMigrated", false,
                    "内部标记：人体图默认缩放是否已从 0.7 升到 0.8，别手动改");
                if (!mk.Value && Mathf.Abs(Scale - 0.7f) < 0.001f)
                {
                    dollEntry.Value = 0.8f;
                    Scale = 0.8f;
                    Plugin.Log.LogInfo("[STEMFishDLC] 人体图缩放：0.7 → 0.8（一次性迁移）");
                }
                mk.Value = true;
            }
            catch { }
            OffsetX = plugin.Config.Bind("HUD", "HealthDollX", -26f,
                "人体图水平位置（屏幕像素，负数向左）").Value;
            OffsetY = plugin.Config.Bind("HUD", "HealthDollY", -84f,
                "人体图垂直位置（屏幕像素，负数向下；默认在暂停菜单「存档」按钮下方）").Value;
        }
        catch { }

        if (!Enabled) { Plugin.Log.LogInfo("[STEMFishDLC] 人体图：关闭"); return; }
        Plugin.Log.LogInfo($"[STEMFishDLC] 人体图：启用（{Parts.Length} 个部位 / 缩放×{Scale:0.0}）");

        // 开枪 → 声音条打满一下（只对玩家自己）
        try
        {
            var fire = AccessTools.Method(typeof(GunScript), "Fire");
            if (fire != null)
            {
                var post = typeof(HealthDoll).GetMethod("Fire_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
                new Harmony("com.stemfish.dlc.dollnoise").Patch(fire, postfix: new HarmonyMethod(post));
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 声音条 hook 失败: {e.Message}"); }
    }

    private static void Fire_Postfix(GunScript __instance)
    {
        try
        {
            if (__instance == null) return;
            var pc = PlayerCamera.main;
            if (pc == null || pc.body == null) return;
            // GunScript.body 是 nonpub，这里直接用"枪挂在谁身上"判断更省事
            if (!__instance.transform.IsChildOf(pc.body.transform)) return;
            NotifyNoise(0.85f);
        }
        catch { }
    }

    // ==========================================================================
    //  每帧
    // ==========================================================================
    public static void Tick()
    {
        if (!Enabled) return;

        try
        {
            Body body = null;
            try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }

            // 和战术终端一样：任何覆盖式界面（终端 / MOD 管理 / 暂停 / 原版健康 / 制作）打开时让位
            bool covered = false;
            try { covered = DlcPanel.AnyOverlayOpen || ModMenu.Visible; } catch { }

            if (body == null || covered)
            {
                SetVisible(false);
                return;
            }
            SetVisible(true);

            // 有人出血时每帧刷新（闪烁要平滑），没人出血时 6.7Hz 就够
            _t += Time.unscaledDeltaTime;
            bool anyBleed = false;
            for (int s = 0; s < Slots; s++)
                if (_bleed[s] > 0.3f) { anyBleed = true; break; }
            if (_t < (anyBleed ? 0f : 0.08f)) return;
            _t = 0f;

            EnsureBuilt();
            if (_imgs == null) return;
            Refresh(body);
        }
        catch (Exception e) { Once($"[STEMFishDLC] 人体图刷新失败: {e.Message}"); }
    }

    private static void SetVisible(bool v)
    {
        if (_root == null) return;
        if (_visible == v) return;
        _visible = v;
        try { _root.SetActive(v); } catch { }
    }

    // ==========================================================================
    //  构建 UI
    // ==========================================================================
    private static void EnsureBuilt()
    {
        if (_built) return;

        EnsureAtlas();
        if (_atlasTex == null)
        {
            if (!_warnedNoArt)
            {
                _warnedNoArt = true;
                Plugin.Log.LogWarning("[STEMFishDLC] 人体图：找不到 textures/doll_atlas.png，已跳过。");
            }
            return;
        }

        _built = true;
        try
        {
            _root = new GameObject("STEMFishDLC_HealthDoll");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 25000;      // 游戏 HUD 之上、暂停菜单(32000)之下
            // 不加 GraphicRaycaster：只显示，绝不挡点击

            var holder = NewRect("Doll", _root.transform);
            holder.anchorMin = holder.anchorMax = new Vector2(1f, 1f);
            holder.pivot = new Vector2(1f, 1f);
            holder.sizeDelta = new Vector2(CanvasW, CanvasH);
            holder.anchoredPosition = new Vector2(OffsetX, OffsetY);
            holder.localScale = Vector3.one * Scale;

            _imgs = new Image[Parts.Length];
            for (int i = 0; i < Parts.Length; i++)
            {
                var p = Parts[i];
                var rt = NewRect(p.Name, holder);
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);     // 左上角为基准
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(p.AW, p.AH);
                rt.anchoredPosition = new Vector2(p.X, -p.Y);

                var img = rt.gameObject.AddComponent<Image>();
                img.color = Good;
                img.raycastTarget = false;
                var sp = CellSprite(p.AX, p.AY, p.AW, p.AH);
                if (sp != null) img.sprite = sp;
                else img.color = new Color(0.5f, 0.5f, 0.5f, 0.25f);   // 缺区域退化
                _imgs[i] = img;
            }

            // 尾巴：在身体**之后**创建并提到最上层 —— 原图里尾巴是画在右臂前面的
            var tailRt = NewRect("tail", holder);
            tailRt.anchorMin = tailRt.anchorMax = new Vector2(0f, 1f);
            tailRt.pivot = new Vector2(0f, 1f);
            tailRt.sizeDelta = new Vector2(TailAW, TailAH);
            tailRt.anchoredPosition = new Vector2(TailX, -TailY);
            var tailImg = tailRt.gameObject.AddComponent<Image>();
            tailImg.raycastTarget = false;
            tailImg.color = TailColor;
            var tailSp = CellSprite(TailAX, TailAY, TailAW, TailAH);
            if (tailSp != null) tailImg.sprite = tailSp;
            tailRt.SetAsLastSibling();

            BuildMeters(holder);

            _visible = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 人体图已构建（图集 {_atlasTex.width}×{_atlasTex.height} / " +
                               $"{Parts.Length} 个部位 + 尾巴）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 人体图构建失败: {e.Message}");
            _imgs = null;
        }
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        return rt;
    }

    // ==========================================================================
    //  图集：一张 64×64 的 doll_atlas.png，代码按区域切
    // ==========================================================================
    private static Texture2D _atlasTex;
    private static bool _atlasTried;
    private static readonly Color TailColor = new Color(0.60f, 0.62f, 0.65f, 0.45f);   // 灰 = 尾巴（纯装饰）

    private static void EnsureAtlas()
    {
        if (_atlasTried) return;
        _atlasTried = true;
        try
        {
            var path = Path.Combine(FishAssets.TextureDir, "doll_atlas.png");
            if (!File.Exists(path)) return;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(tex); return; }
            tex.filterMode = FilterMode.Bilinear;       // 这张图是柔边剪影（不是像素画），双线性更顺
            tex.wrapMode = TextureWrapMode.Clamp;
            _atlasTex = tex;
        }
        catch (Exception e) { Once($"[STEMFishDLC] 人体图集加载失败: {e.Message}"); }
    }

    private static Sprite CellSprite(int x, int y, int w, int h)
    {
        if (_atlasTex == null) return null;
        // 图集坐标是左上原点，Unity 纹理是左下原点 → y 翻转
        var rect = new Rect(x, _atlasTex.height - (y + h), w, h);
        return Sprite.Create(_atlasTex, rect, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    // ==========================================================================
    //  左侧两根竖条（声音 / 体力）
    //  ---------------------------------------------------------------------------
    //  放在人偶左边（x = -10 / -4）。人偶本体的最左边是 x=0，所以不会压到剪影。
    //  宽 4px、高 84px，半透明，纯装饰 —— 让右上角那块不至于太单调。
    //  声音：移动速度 → 噪音，开枪时打满再衰减
    //  体力：Body.stamina 是 0~100
    // ==========================================================================
    private static void BuildMeters(Transform holder)
    {
        _barSprite = WhiteSprite();
        _sndFill = MakeMeter(holder, "MeterSound", -18f);
        _stmFill = MakeMeter(holder, "MeterStamina", -9f);
    }

    private static Image MakeMeter(Transform holder, string name, float x)
    {
        // 底槽
        var bgRt = NewRect(name + "Bg", holder);
        bgRt.anchorMin = bgRt.anchorMax = new Vector2(0f, 1f);
        bgRt.pivot = new Vector2(0f, 1f);
        bgRt.sizeDelta = new Vector2(6f, 150f);
        bgRt.anchoredPosition = new Vector2(x, -22f);
        var bg = bgRt.gameObject.AddComponent<Image>();
        bg.sprite = _barSprite;
        bg.color = new Color(0f, 0f, 0f, 0.35f);
        bg.raycastTarget = false;

        // 填充条（从底部往上长）
        var rt = NewRect(name, holder);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(4f, 144f);
        rt.anchoredPosition = new Vector2(x + 1f, -25f);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = _barSprite;
        img.type = Image.Type.Filled;
        img.fillMethod = Image.FillMethod.Vertical;
        img.fillOrigin = (int)Image.OriginVertical.Bottom;
        img.fillAmount = 0f;
        img.raycastTarget = false;
        return img;
    }

    private static Sprite WhiteSprite()
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.filterMode = FilterMode.Point;
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    private static void UpdateMeters(Body body)
    {
        if (_sndFill == null || _stmFill == null) return;

        // ---------- 声音 ----------
        float speed = 0f;
        try
        {
            var p = body.transform.position;
            if (_hasLastPos)
            {
                float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
                speed = (p - _lastPos).magnitude / dt;
            }
            _lastPos = p;
            _hasLastPos = true;
        }
        catch { }

        float moving = Mathf.Clamp01(speed / 7f);                       // 走 ≈2，跑 ≈5+
        _noisePulse = Mathf.MoveTowards(_noisePulse, 0f, Time.unscaledDeltaTime * 1.5f);
        float want = Mathf.Clamp01(Mathf.Max(moving * 0.8f, _noisePulse));
        _noise = Mathf.Lerp(_noise, want, 0.25f);                      // 平滑，别一格一格跳

        _sndFill.fillAmount = _noise;
        _sndFill.color = Color.Lerp(new Color(0.32f, 0.66f, 0.74f, 0.50f),
                                    new Color(0.95f, 0.45f, 0.30f, 0.90f), _noise);

        // ---------- 体力 ----------
        float st = 1f;
        try { st = Mathf.Clamp01(body.stamina / 100f); } catch { }
        _stmFill.fillAmount = st;
        _stmFill.color = st > 0.30f
            ? Color.Lerp(new Color(0.85f, 0.72f, 0.42f, 0.60f), new Color(0.90f, 0.84f, 0.62f, 0.70f), st)
            : Color.Lerp(new Color(0.90f, 0.32f, 0.24f, 0.85f), new Color(0.85f, 0.62f, 0.30f, 0.70f), st / 0.30f);
    }

    // 外部打一个噪音脉冲（开枪时由 Fire_Postfix 调用）
    public static void NotifyNoise(float amount)
    {
        if (amount > _noisePulse) _noisePulse = Mathf.Clamp01(amount);
    }

    // ==========================================================================
    //  刷新颜色
    // ==========================================================================
    private static void Refresh(Body body)
    {
        UpdateMeters(body);

        for (int i = 0; i < Slots; i++) { _sum[i] = 0f; _cnt[i] = 0; _gone[i] = false; _bleedSum[i] = 0f; }

        try
        {
            var limbs = body.limbs;
            if (limbs != null)
            {
                for (int i = 0; i < limbs.Length; i++)
                {
                    var l = limbs[i];
                    if (l == null) continue;
                    int s = SlotOf(l);
                    if (s < 0) continue;
                    if (l.dismembered) { _gone[s] = true; continue; }
                    _sum[s] += HealthOf(l);
                    _cnt[s]++;
                    try { if (l.bleedAmount > 0f) _bleedSum[s] += l.bleedAmount; } catch { }
                }
            }
        }
        catch { }

        // 该槽没数据 → 沿兜底链向上借颜色
        for (int s = 0; s < Slots; s++)
        {
            _health[s] = _cnt[s] > 0 ? _sum[s] / _cnt[s] : -1f;
            _bleed[s] = _bleedSum[s];
        }

        for (int s = 0; s < Slots; s++)
        {
            if (_health[s] >= 0f) continue;
            int p = Fallback[s];
            int guard = 0;
            while (p >= 0 && _health[p] < 0f && guard++ < 8) p = Fallback[p];
            _health[s] = (p >= 0 && _health[p] >= 0f) ? _health[p] : 1f;
        }

        // 整体透明度：满血时整张图压到 38%（"没受伤时不抢眼睛"），
        // 一有伤就迅速回到不透明 —— 25% 损伤就完全实心。
        float worst = 1f;
        for (int s = 0; s < Slots; s++) if (_health[s] < worst) worst = _health[s];
        float dmg = Mathf.Clamp01(1f - worst);
        float bodyAlpha = Mathf.Lerp(HealthyAlpha, 1f, Mathf.Clamp01(dmg * 4f));

        float now = Time.unscaledTime;
        for (int i = 0; i < Parts.Length; i++)
        {
            int s = Parts[i].Slot;
            var c = ColorOf(_health[s], _gone[s]);

            float bleed = _bleed[s];
            if (bleed > 0.3f)
            {
                // 出血闪烁：频率随出血量上升（0.3 → 0.8Hz，20+ → 5Hz）。
                // 用 smoothstep 把正弦削成"呼吸"而不是硬闪，再和受伤色插值 ——
                // 这样大出血的部位会持续轻轻跳动，不会盖掉健康度本身的颜色信息。
                float freq = Mathf.Lerp(0.8f, 5f, Mathf.Clamp01(bleed / 20f));
                float wave = 0.5f + 0.5f * Mathf.Sin(now * freq * 6.2831853f);
                wave = Mathf.SmoothStep(0f, 1f, wave);
                c = Color.Lerp(c, BleedHi, wave * 0.8f);
            }

            c.a = Mathf.Max(bodyAlpha, bleed > 0.3f ? 0.85f : 0f);   // 出血部位始终看得见

            var img = _imgs[i];
            if (img == null) continue;

            // 闪烁必须每帧写（不能走"值没变就跳过"）；静态部位才做变化比较
            if (bleed <= 0.3f && Same(c, _lastColor[i])) continue;
            _lastColor[i] = c;
            img.color = c;
        }

        if (!_diagOnce)
        {
            _diagOnce = true;
            var sb = new System.Text.StringBuilder();
            var limbs = body.limbs;
            if (limbs != null)
                for (int i = 0; i < limbs.Length; i++)
                {
                    var l = limbs[i];
                    if (l == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(l.name).Append("→").Append(SlotOf(l));
                }
            Plugin.Log.LogInfo($"[STEMFishDLC] 人体图肢体映射（{limbs?.Length ?? 0} 个）: {sb}");
        }
    }

    private static float HealthOf(Limb l)
    {
        float skin = Mathf.Clamp01(l.skinHealth / 100f);
        float muscle = Mathf.Clamp01(l.muscleHealth / 100f);
        return Mathf.Min(skin, muscle);
    }

    private static Color ColorOf(float h, bool gone)
    {
        if (gone) return Gone;
        if (h >= 0.85f) return Color.Lerp(Fair, Good, Mathf.InverseLerp(0.85f, 1f, h));
        if (h >= 0.60f) return Color.Lerp(Hurt, Fair, Mathf.InverseLerp(0.60f, 0.85f, h));
        if (h >= 0.35f) return Color.Lerp(Bad, Hurt, Mathf.InverseLerp(0.35f, 0.60f, h));
        if (h > 0.01f) return Color.Lerp(Crit, Bad, Mathf.InverseLerp(0.01f, 0.35f, h));
        return Crit;
    }

    private static bool Same(Color a, Color b)
    {
        return Mathf.Abs(a.r - b.r) < 0.004f && Mathf.Abs(a.g - b.g) < 0.004f &&
               Mathf.Abs(a.b - b.b) < 0.004f && Mathf.Abs(a.a - b.a) < 0.004f;
    }

    // ==========================================================================
    //  部位归类
    // ==========================================================================
    private static int SlotOf(Limb l)
    {
        bool back = IsBack(l);
        try
        {
            if (l.isHead) return S_Head;
            if (l.isAbdomen) return S_Stomach;

            string n = NameOf(l);

            if (l.isArm)
            {
                if (Has(n, "hand")) return back ? S_Hand_B : S_Hand_F;
                if (Has(n, "up")) return back ? S_ArmU_B : S_ArmU_F;
                if (Has(n, "down") || Has(n, "fore") || Has(n, "lower")) return back ? S_ArmD_B : S_ArmD_F;
                return back ? S_ArmD_B : S_ArmD_F;          // 兜底：当整条前臂
            }

            if (l.isLegLimb)
            {
                if (Has(n, "foot") || Has(n, "feet")) return back ? S_Foot_B : S_Foot_F;
                if (Has(n, "shin") || Has(n, "calf") || Has(n, "lower") || Has(n, "down"))
                    return back ? S_LegD_B : S_LegD_F;
                if (Has(n, "thigh") || Has(n, "up")) return back ? S_LegU_B : S_LegU_F;
                return back ? S_LegU_B : S_LegU_F;          // 兜底：当整条大腿
            }

            return S_Thorax;                                 // UpTorso 等躯干件
        }
        catch { return -1; }
    }

    private static string NameOf(Limb l)
    {
        string n = null;
        try { n = l.name; } catch { }
        if (string.IsNullOrEmpty(n)) { try { n = l.fullName; } catch { } }
        return (n ?? "").ToLowerInvariant();
    }

    private static bool Has(string s, string k) => s.IndexOf(k, StringComparison.Ordinal) >= 0;

    private static bool IsBack(Limb l)
    {
        string n = null;
        try { n = l.name; } catch { }
        if (string.IsNullOrEmpty(n)) { try { n = l.fullName; } catch { } }
        if (string.IsNullOrEmpty(n)) return false;
        if (n.EndsWith("B", StringComparison.Ordinal)) return true;
        if (n.EndsWith("Back", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static void Once(string msg)
    {
        if (_lastWarn == msg) return;
        _lastWarn = msg;
        Plugin.Log.LogWarning(msg);
    }
}
