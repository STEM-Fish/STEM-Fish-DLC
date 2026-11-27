using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  TailFlow - SF-07 的尾巴飘动（照搬 SkinSyncMod 的 TailFlowDeform 做法）
//  ---------------------------------------------------------------------------
//  原版 `TailScript.Update()` 只把**整根**尾巴绕根部转到移动方向（clamp ±80°），
//  是一根硬棍子。要做"飘"，必须放弃"旋转一张贴图"，改成**变形一条网格**。
//
//  反汇编 SkinSyncMod 得到的完整做法（这里逐条照搬）：
//    ① mesh 挂在 **Body 根节点**下（不是尾巴自己），localPosition/Rotation 全零、scale 1
//       —— 顶点点位每帧用世界坐标算完再 InverseTransformPoint 回来，所以不受
//          父级旋转/镜像的干扰，也就不会像我上一版那样整体错位。
//    ② 顶点是 (segments+1)×2 的一条四边形带：每段两个顶点（上下边缘），
//       每段 2 个三角形；`_tris[i*6..+5] = (i*2, (i+1)*2, i*2+1, i*2+1, (i+1)*2, (i+1)*2+1)`
//    ③ 宽度不是估的：`GetPixels32()` 扫贴图，**沿长轴逐列**求不透明像素的
//       minY/maxY → 每列的中心偏移 `_segCenter[i]` 和半宽 `_segHalf[i]`（像素 / ppu）
//       —— 尾巴从粗到细的轮廓就是从这里来的，所以弯曲时不会像分段贴图那样"断成一节一节"
//    ④ 骨架是世界坐标的 Verlet 链：根点 = `transform.position`（尾巴 pivot 就是根部），
//       第一个骨点用 `AnchorFollow` 软跟随"根点 + dir × segLen"，
//       其余点吃重力 + 双频正弦风 + 速度阻尼；子步积分（dt 切成 ≤MaxFixedDt）
//    ⑤ 约束跑两轮：先**弯曲角限制**（相邻两段夹角超过 MaxBendDeg 就把下一段转回去），
//       再**段长约束**（把后一个点拉回 segLen）—— 前者防止尾巴对折，后者防止拉长
//    ⑥ 摆顶点前对骨链做一次 `_smoothPos = Lerp(_smoothPos, bonePos, Smoothness)`，
//       消掉逐帧抖动；切线用相邻两段的角平分线，side = 切线法线 × (dir.y >= 0 ? 1 : -1)
//    ⑦ **光照**：`FindLitMaterialSource()` 从 `Body.limbs` 里随便找一个肢体，
//       复制它的 sharedMaterial（URP 2D 的 Sprite-Lit），
//       再用 `ResetTransientLimbUniforms()` 把 8 个伤害/状态属性清零：
//       _BloodOverlay / _SkinDamage / _MuscleDamage / _InfectionPercent /
//       _SnowAmount / _Dirtyness / _Wetness / _Pain
//       —— 不清零的话尾巴会继承手臂上的血污、脏污、疼痛参数，颜色会乱。
//       找不到肢体就退回 `Shader.Find("Sprites/Default")`。
//    ⑧ 每帧把 `_mat.color = _sr.color`、`_mr.sortingLayerID/Order = _sr.xxx`，
//       再把原 SpriteRenderer 关掉。
// ============================================================================

public static class TailFlow
{
    // ★ 默认值全部取自 SkinSyncMod 的 TailDeformConfig..cctor（反汇编出来的真实值）：
    //   Segments=10 ConstraintIters=6 Damping=0.45 SpeedDamping=0.05 Stiffness=1
    //   Gravity=(0,-1.2) WindFreq=1.2 WindAmp=0.012 SpeedDisturb=0.005
    //   MaxBendDeg=18 AnchorFollow=1 Smoothness=0.55 FrontGuard=1 MaxStep=0.15 MaxFixedDt=0.02
    //   上一版我凭感觉填的值偏差很大（重力 5 倍、风力 29 倍、迭代少一半），
    //   表现就是"萎焉下垂 + 机械抖动 + 一节一节"。
    public static bool Enabled = true;
    public static int Segments = 18;
    public static float Width = 1.7f;
    public static float WindAmp = 0.012f;
    public static float SpeedDisturb = 0.005f;
    public static float WindFreq = 1.2f;
    public static float Gravity = 1.0f;
    public static float Damping = 0.40f;
    public static float SpeedDamping = 0.05f;
    public static float Stiffness = 1f;
    public static float Smoothness = 0.30f;
    public static float MaxBendDeg = 30f;
    public static float AnchorFollow = 1f;
    public static float MaxStep = 0.40f;
    public static bool FrontGuard = true;

    private static TailRig _rig;
    private static bool _noTailWarned;
    private static bool _noSrWarned;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Cosmetic", "TailFlow", true,
                "SF-07 的尾巴网格飘动（关掉 = 只有原版整根旋转）").Value;
            Segments = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowSegments", 18,
                "尾巴分几段（越多越柔顺；12~24 平滑，太多会吃性能）").Value, 4, 32);
            Width = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowWidth", 1.7f,
                "尾巴粗细倍率（1 = 贴图原宽；贴图本身只有 5~10 像素高，1.6~2.0 才饱满）").Value, 0.3f, 3f);
            WindAmp = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowAmp", 0.35f,
                "风的幅度（0 = 只有惯性和重力，0.6 已经很飘）").Value, 0f, 2f);
            WindFreq = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowFreq", 1.6f,
                "风的频率（越大抖得越快）").Value, 0.2f, 6f);
            Gravity = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowGravity", 1f,
                "向下的重力（越大尾梢垂得越低）").Value, 0f, 20f);
            Damping = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowDamping", 0.40f,
                "速度阻尼（越大越黏、停得越快）").Value, 0f, 0.95f);
            SpeedDamping = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowSpeedDamp", 0.05f,
                "速度越快阻尼越小（原版 0.05）").Value, 0f, 0.3f);
            Stiffness = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowStiffness", 1f,
                "段长约束强度（1 = 硬约束，越小越软）").Value, 0.1f, 1f);
            Smoothness = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowSmooth", 0.30f,
                "骨链平滑（越大越顺滑，但滞后也越大，会显得迟钝）").Value, 0f, 0.95f);
            MaxBendDeg = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowMaxBend", 30f,
                "相邻两段的最大夹角（度）—— 段数越多单段弯得越少，30 左右刚好").Value, 5f, 80f);
            AnchorFollow = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowAnchor", 1f,
                "第一骨点跟随根部的强度（1 = 完全跟随）").Value, 0.05f, 1f);
            MaxStep = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowMaxStep", 0.40f,
                "单个子步的最大位移（太小会跟不上身体、一顿一顿）").Value, 0.02f, 2f);
            FrontGuard = plugin.Config.Bind("Cosmetic", "TailFlowFrontGuard", true,
                "防止尾巴甩到身体前面（原版开）").Value;
        }
        catch { }
    }

    public static void Tick()
    {
        try
        {
            if (!Enabled) { Kill(); return; }

            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body == null) { Kill(); return; }

            if (CharacterSelect.Current != CharacterSelect.Sf07) { Kill(); return; }

            if (_rig != null && _rig.Owner != body) Kill();

            if (_rig == null)
            {
                var tail = body.GetComponentInChildren<TailScript>(true);
                if (tail == null)
                {
                    if (!_noTailWarned)
                    {
                        _noTailWarned = true;
                        Plugin.Log.LogInfo("[STEMFishDLC] Body 下没找到 TailScript —— 这个角色没有尾巴");
                    }
                    return;
                }

                var sr = tail.GetComponent<SpriteRenderer>();
                if (sr == null) sr = tail.GetComponentInChildren<SpriteRenderer>(true);
                if (sr == null)
                {
                    if (!_noSrWarned)
                    {
                        _noSrWarned = true;
                        Plugin.Log.LogWarning("[STEMFishDLC] TailScript 上没找到 SpriteRenderer，尾巴飘动不生效");
                    }
                    return;
                }

                _rig = tail.gameObject.GetComponent<TailRig>();
                if (_rig == null) _rig = tail.gameObject.AddComponent<TailRig>();
                _rig.Bind(sr, body);
                Plugin.Log.LogInfo($"[STEMFishDLC] 尾巴网格已挂载（sprite={sr.sprite?.name}，{Segments} 段）");
            }

            _rig.Sync();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 尾巴飘动失败: {e.Message}");
        }
    }

    private static void Kill()
    {
        if (_rig == null) return;
        var rig = _rig;
        _rig = null;
        try { rig.Restore(); } catch { }
        try { UnityEngine.Object.Destroy(rig); } catch { }
    }
}

// ============================================================================
//  尾巴网格
// ============================================================================
public class TailRig : MonoBehaviour
{
    private SpriteRenderer _sr;
    private Body _body;

    private GameObject _meshGo;
    private MeshFilter _mf;
    private MeshRenderer _mr;
    private Mesh _mesh;
    private Material _mat;

    private Vector2[] _bonePos;
    private Vector2[] _bonePrev;
    private Vector2[] _smoothPos;
    private float[] _segHalf;
    private float[] _segCenter;

    private Vector3[] _verts;
    private Vector2[] _uvs;
    private int[] _tris;

    private float _segLen;
    private float _windPhase;
    private float _smoothBaseAmp;
    private Vector2 _dir = Vector2.right;
    private bool _physicsInited;
    private string _cachedKey;
    private bool _restored;

    public bool Alive => this != null && _sr != null;
    public Body Owner => _body;

    public void Bind(SpriteRenderer sr, Body body)
    {
        _sr = sr;
        _body = body;
        _restored = false;
        if (_meshGo == null) BuildMeshChild();
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        try { if (_sr != null) _sr.enabled = true; } catch { }
        try { if (_meshGo != null) Destroy(_meshGo); } catch { }
        try { if (_mesh != null) Destroy(_mesh); } catch { }
        try { if (_mat != null) Destroy(_mat); } catch { }
        _meshGo = null;
        _mesh = null;
        _mat = null;
    }

    private void OnDestroy() => Restore();

    // ======================================================================
    //  BuildMeshChild —— mesh 挂在 Body 根下，全部 identity
    // ======================================================================
    private void BuildMeshChild()
    {
        _meshGo = new GameObject("TailDeformMesh");
        var parent = _body != null ? _body.transform : transform;
        _meshGo.transform.SetParent(parent, false);
        _meshGo.transform.localPosition = Vector3.zero;
        _meshGo.transform.localRotation = Quaternion.identity;
        _meshGo.transform.localScale = Vector3.one;

        _mf = _meshGo.AddComponent<MeshFilter>();
        _mr = _meshGo.AddComponent<MeshRenderer>();

        // ---- 材质：抄一个肢体的 lit 材质，再把瞬时状态清零 ----
        var src = FindLitMaterialSource();
        if (src != null && src.sharedMaterial != null)
            _mat = new Material(src.sharedMaterial);
        else
            _mat = new Material(Shader.Find("Sprites/Default"));

        ResetTransientLimbUniforms(_mat);
        _mr.sharedMaterial = _mat;

        _mesh = new Mesh();
        _mesh.name = "TailDeformMesh";
        _mesh.MarkDynamic();
        _mf.sharedMesh = _mesh;
    }

    /// <summary>从 Body.limbs 里找一个带材质的手臂/腿，抄它的 URP 2D lit 材质</summary>
    private Renderer FindLitMaterialSource()
    {
        try
        {
            if (_body == null || _body.limbs == null) return null;
            for (int i = 0; i < _body.limbs.Length; i++)
            {
                var limb = _body.limbs[i];
                if (limb == null) continue;
                var r = limb.GetComponent<SpriteRenderer>();
                if (r != null && r.sharedMaterial != null) return r;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 肢体材质上挂着 8 个"瞬时状态"属性（血污 / 伤口 / 脏 / 湿 / 疼…）。
    /// 不清零的话尾巴会继承手臂的状态，颜色和亮度都不对。
    /// </summary>
    private static void ResetTransientLimbUniforms(Material m)
    {
        if (m == null) return;
        string[] props =
        {
            "_BloodOverlay", "_SkinDamage", "_MuscleDamage", "_InfectionPercent",
            "_SnowAmount", "_Dirtyness", "_Wetness", "_Pain",
        };
        for (int i = 0; i < props.Length; i++)
        {
            if (m.HasProperty(props[i])) m.SetFloat(props[i], 0f);
        }
    }

    // ======================================================================
    //  Sync：换皮肤（sprite 变了）就重建
    // ======================================================================
    public void Sync()
    {
        if (_sr == null) return;
        var sp = _sr.sprite;
        if (sp == null) return;

        int seg = Mathf.Max(2, TailFlow.Segments);

        // ★ key 用 "名字 + rect + 段数"，不用 GetInstanceID：
        //   FishSkin 每帧校正角色贴图，可能每帧 generate 一个新的 Sprite 实例，
        //   用实例 ID 判断会导致**每帧重建**——骨链被反复重置成一条直线，
        //   表现就是"隔一会儿动一下、动起来一顿一顿"。
        var rc = sp.rect;
        string key = sp.name + "|" + (int)rc.x + "," + (int)rc.y + "," +
                     (int)rc.width + "x" + (int)rc.height + "|" + seg;
        bool need = key != _cachedKey || _bonePos == null || _bonePos.Length != seg + 1;
        if (need) Rebuild(sp, seg);

        if (_sr.enabled) _sr.enabled = false;
        if (_mat != null) _mat.color = _sr.color;
        if (_mr != null)
        {
            _mr.sortingLayerID = _sr.sortingLayerID;
            _mr.sortingOrder = _sr.sortingOrder;
        }
    }

    // ======================================================================
    //  Rebuild：扫像素 → 每列半宽/中心 → 顶点/UV/三角
    // ======================================================================
    private void Rebuild(Sprite sp, int seg)
    {
        var rr = sp.rect;
        _cachedKey = sp.name + "|" + (int)rr.x + "," + (int)rr.y + "," +
                     (int)rr.width + "x" + (int)rr.height + "|" + seg;

        var tex = sp.texture;
        if (tex == null) return;

        var rect = sp.textureRect;
        int x0 = Mathf.RoundToInt(rect.x), y0 = Mathf.RoundToInt(rect.y);
        int w = Mathf.RoundToInt(rect.width), h = Mathf.RoundToInt(rect.height);
        if (w <= 0 || h <= 0) return;

        float ppu = sp.pixelsPerUnit <= 0.01f ? 8f : sp.pixelsPerUnit;

        // 世界长度（骨链用世界坐标）
        float scale = Mathf.Abs(transform.lossyScale.x);
        if (scale < 0.0001f) scale = 1f;

        int n = seg + 1;
        _verts = new Vector3[n * 2];
        _uvs = new Vector2[n * 2];
        _tris = new int[seg * 6];
        _bonePos = new Vector2[n];
        _bonePrev = new Vector2[n];
        _smoothPos = new Vector2[n];
        _segHalf = new float[n];
        _segCenter = new float[n];

        for (int i = 0; i < seg; i++)
        {
            int v0 = i * 2, v1 = i * 2 + 1, v2 = (i + 1) * 2, v3 = (i + 1) * 2 + 1;
            int t = i * 6;
            _tris[t + 0] = v0;
            _tris[t + 1] = v2;
            _tris[t + 2] = v1;
            _tris[t + 3] = v1;
            _tris[t + 4] = v2;
            _tris[t + 5] = v3;
        }

        int firstCol = 0, lastCol = w - 1;
        BuildProfileFromPixels(sp, tex, x0, y0, w, h, ppu, n, scale, ref firstCol, ref lastCol);
        int axisLenPx = Mathf.Max(1, lastCol - firstCol + 1);

        // ★ 段长 = 总长 ÷ 段数。这里以前漏了除法，直接把整条尾巴的长度
        //   当成了一段，链上每一段都按整条长度排开 → 总长变成 段数×尾长（67u），
        //   看起来就是一条横穿地图的白线。
        float localLen = axisLenPx / ppu;
        _segLen = localLen / seg * scale;

        // ---- UV：沿长轴线性，v 用贴图内的实际上下边界 ----
        float texW = tex.width, texH = tex.height;
        for (int i = 0; i < n; i++)
        {
            float u = (x0 + firstCol + (axisLenPx - 1) * (i / (float)(n - 1))) / texW;
            u = Mathf.Clamp01(u);
            _uvs[i * 2] = new Vector2(u, y0 / texH);
            _uvs[i * 2 + 1] = new Vector2(u, (y0 + h) / texH);
        }

        if (_mat != null) _mat.mainTexture = tex;
        _physicsInited = false;

        Plugin.Log.LogInfo($"[STEMFishDLC] 尾巴网格重建: {sp.name} {w}x{h}px → {seg} 段 | " +
                           $"有效长 {axisLenPx}px ppu={ppu:0.##} 世界总长={localLen * scale:0.##}u " +
                           $"段长={_segLen:0.###}u 中段宽≈{_segHalf[seg / 2] * 2f:0.##}u 缩放={scale:0.##}");
    }

    /// <summary>
    /// 逐列扫不透明像素：得到每列的中心偏移和半宽（像素 / ppu，mesh 局部单位）。
    /// 扫不动（贴图不可读）就退回"矩形带"。
    /// </summary>
    private void BuildProfileFromPixels(Sprite sp, Texture2D tex, int x0, int y0, int w, int h,
                                        float ppu, int n, float scale, ref int firstCol, ref int lastCol)
    {
        Color32[] px = null;
        try { px = tex.GetPixels32(); } catch { px = null; }

        int tw = tex.width;

        if (px == null)
        {
            BuildFallbackProfile(sp, w, h, ppu, n, scale);
            firstCol = 0;
            lastCol = w - 1;
            return;
        }

        // 先找有像素的首/末列（去掉两侧透明留白）
        int first = -1, last = -1;
        for (int x = 0; x < w; x++)
        {
            bool any = false;
            for (int y = 0; y < h; y++)
            {
                int idx = (y0 + y) * tw + (x0 + x);
                if (idx >= 0 && idx < px.Length && px[idx].a > 8) { any = true; break; }
            }
            if (any) { if (first < 0) first = x; last = x; }
        }
        if (first < 0) { first = 0; last = w - 1; }

        firstCol = first;
        lastCol = last;

        float pivotY = sp.pivot.y;

        for (int i = 0; i < n; i++)
        {
            int x = first + Mathf.RoundToInt((last - first) * (i / (float)(n - 1)));
            x = Mathf.Clamp(x, first, last);

            int minY = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int idx = (y0 + y) * tw + (x0 + x);
                if (idx < 0 || idx >= px.Length || px[idx].a <= 8) continue;
                if (minY < 0) minY = y;
                maxY = y;
            }

            float cy, hh;
            if (minY < 0)
            {
                cy = h * 0.5f;
                hh = Mathf.Max(0.5f, h * 0.5f);
            }
            else
            {
                cy = (minY + maxY + 1) * 0.5f;
                hh = Mathf.Max(0.5f, (maxY - minY + 1) * 0.5f);
            }

            // ★ 顶点用世界坐标算，这里必须换算成世界单位（像素/ppu × 世界缩放）
            _segCenter[i] = (cy - pivotY) / ppu * scale;
            _segHalf[i] = hh / ppu * scale;
        }

        // ★ 轮廓平滑：像素扫描出来的宽度是逐列跳变的（54px 的图每 1.5px 采一次），
        //   直接拿去生成网格，边缘会有明显的"台阶"，看起来就是一节一节。
        //   两遍 3 点平均就能把台阶抹平，又不会把尾巴的粗细变化吃掉。
        var tmpH = new float[n];
        var tmpC = new float[n];
        for (int pass = 0; pass < 2; pass++)
        {
            Array.Copy(_segHalf, tmpH, n);
            Array.Copy(_segCenter, tmpC, n);
            for (int i = 1; i < n - 1; i++)
            {
                _segHalf[i] = (tmpH[i - 1] + tmpH[i] + tmpH[i + 1]) / 3f;
                _segCenter[i] = (tmpC[i - 1] + tmpC[i] + tmpC[i + 1]) / 3f;
            }
        }
    }

    /// <summary>贴图不可读时的退路：等宽矩形带（没有粗细变化，但至少位置对）</summary>
    private void BuildFallbackProfile(Sprite sp, int w, int h, float ppu, int n, float scale)
    {
        float pivotY = sp.pivot.y;
        float cy = h * 0.5f;
        float hh = h * 0.5f;
        for (int i = 0; i < n; i++)
        {
            // ★ 顶点用世界坐标算，这里必须换算成世界单位（像素/ppu × 世界缩放）
            _segCenter[i] = (cy - pivotY) / ppu * scale;
            _segHalf[i] = hh / ppu * scale;
        }
        Plugin.Log.LogWarning("[STEMFishDLC] 尾巴贴图不可读，退回矩形带（会没有粗细变化）");
    }

    // ======================================================================
    //  InitBonesAt：链从尾巴根部沿"身体后方"排开
    // ======================================================================
    private void InitBonesAt(Vector2 pos, Vector2 dir)
    {
        for (int i = 0; i < _bonePos.Length; i++)
        {
            _bonePos[i] = pos + dir * (_segLen * i);
            _bonePrev[i] = _bonePos[i];
            _smoothPos[i] = _bonePos[i];
        }
        _physicsInited = true;
    }

    // ======================================================================
    //  LateUpdate：Verlet + 弯曲限制 + 段长约束 + 顶点生成
    // ======================================================================
    private void LateUpdate()
    {
        if (_sr == null || _sr.sprite == null) return;
        if (_meshGo == null || _bonePos == null || _bonePos.Length < 2) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // 尾巴朝身体后方
        bool right = true;
        try { right = _body == null || _body.isRight; } catch { }
        _dir = right ? Vector2.left : Vector2.right;

        Vector2 root = transform.position;

        if (!_physicsInited) InitBonesAt(root, _dir);

        int count = _bonePos.Length;

        float speed = 0f;
        try
        {
            if (_body != null && _body.rb != null) speed = Mathf.Abs(_body.rb.velocity.x);
        }
        catch { }

        // ---- 物理参数（全部对齐 SkinSyncMod 的 TailDeformConfig） ----
        Vector2 gravity = new Vector2(0f, -TailFlow.Gravity);

        // 速度越快阻尼越大：原版 damp = Clamp01(Damping + Min(|vx| * SpeedDamping, 0.5))
        float damp = Mathf.Clamp01(TailFlow.Damping + Mathf.Min(speed * TailFlow.SpeedDamping, 0.5f));
        float keep = 1f - damp;

        float maxBend = Mathf.Max(0f, TailFlow.MaxBendDeg) * Mathf.Deg2Rad;
        float maxStep = Mathf.Max(0.02f, TailFlow.MaxStep);
        float smooth = Mathf.Clamp(TailFlow.Smoothness, 0f, 0.99f);
        float anchorFollow = Mathf.Clamp01(TailFlow.AnchorFollow);
        float stiffness = Mathf.Clamp01(TailFlow.Stiffness);
        int iters = 8;                                   // 比原版多两次，段多时更稳

        // 风力：频率和幅度都随速度涨（原版 WindFreq + speed*0.15 / WindAmp + speed*SpeedDisturb）
        float windFreq = TailFlow.WindFreq + speed * 0.15f;
        float windTarget = TailFlow.WindAmp + speed * TailFlow.SpeedDisturb;
        _windPhase += dt * windFreq;
        _smoothBaseAmp = Mathf.Lerp(_smoothBaseAmp, windTarget, 0.2f);
        float windAmp = _smoothBaseAmp;

        // ---- 根点 + 第一个骨点跟随 ----
        // 原版 AnchorFollow = 1：第一个骨点直接钉在 root + dir*segLen 上，
        // 之前我给 0.45，根部一直是松的，整条尾巴"挂"在半空甩不起来。
        _bonePos[0] = root;
        _bonePrev[0] = root;

        Vector2 anchor = root + _dir * _segLen;
        _bonePos[1] = Vector2.Lerp(_bonePos[1], anchor, anchorFollow);
        _bonePrev[1] = Vector2.Lerp(_bonePrev[1], anchor, anchorFollow);

        // ---- 子步积分 ----
        float maxFixedDt = 0.02f;                        // 原版 MaxFixedDt
        int steps = Mathf.Max(1, Mathf.CeilToInt(dt / maxFixedDt));
        float h = dt / steps;

        for (int s = 0; s < steps; s++)
        {
            for (int i = 2; i < count; i++)
            {
                Vector2 vel = (_bonePos[i] - _bonePrev[i]) * keep;

                if (vel.magnitude > maxStep) vel = vel.normalized * maxStep;

                _bonePrev[i] = _bonePos[i];

                Vector2 next = _bonePos[i] + vel + gravity * (h * h);

                float k = i / (float)(count - 1);
                float amp = windAmp * k * k;                  // 越靠尾梢越飘
                float wave = Mathf.Sin(_windPhase + k * Mathf.PI * 2f) * amp;
                next += Vector2.up * wave;

                Vector2 d = next - _bonePos[i];
                if (d.magnitude > maxStep) next = _bonePos[i] + d.normalized * maxStep;

                _bonePos[i] = next;
            }

            for (int it = 0; it < iters; it++)
            {
                // ---- 约束 1：相邻两段最大夹角 ----
                for (int i = 1; i < count - 1; i++)
                {
                    Vector2 d1 = _bonePos[i] - _bonePos[i - 1];
                    Vector2 d2 = _bonePos[i + 1] - _bonePos[i];
                    if (d1.sqrMagnitude < 1e-6f || d2.sqrMagnitude < 1e-6f) continue;

                    float a1 = Mathf.Atan2(d1.y, d1.x) * Mathf.Rad2Deg;
                    float a2 = Mathf.Atan2(d2.y, d2.x) * Mathf.Rad2Deg;
                    float delta = Mathf.DeltaAngle(a1, a2) * Mathf.Deg2Rad;

                    if (Mathf.Abs(delta) > maxBend)
                    {
                        float clamped = Mathf.Clamp(delta, -maxBend, maxBend);
                        float cos = Mathf.Cos(clamped), sin = Mathf.Sin(clamped);
                        Vector2 u = d1.normalized;
                        Vector2 rot = new Vector2(u.x * cos - u.y * sin, u.x * sin + u.y * cos);
                        _bonePos[i + 1] = _bonePos[i] + rot * d2.magnitude;
                    }
                }

                // ---- 约束 2：段长（stiffness 控制软硬） ----
                for (int i = 1; i < count; i++)
                {
                    Vector2 d = _bonePos[i] - _bonePos[i - 1];
                    float len = d.magnitude;
                    if (len < 1e-5f) continue;
                    _bonePos[i] -= d * (stiffness * (len - _segLen) / len);
                }
            }

            // ---- 约束 3：尾巴不许窜到身体前面（原版 FrontGuard） ----
            if (TailFlow.FrontGuard)
            {
                float margin = 0.05f;
                for (int i = 2; i < count; i++)
                {
                    Vector2 v = _bonePos[i] - root;
                    float dot = Vector2.Dot(v, _dir);
                    if (dot >= -margin) continue;
                    Vector2 perp = v - _dir * dot;
                    _bonePos[i] = root + _dir * (-margin) + perp;
                }
            }
        }

        // ---- 平滑 ----
        if (_smoothPos == null || _smoothPos.Length != count) _smoothPos = new Vector2[count];
        for (int i = 0; i < count; i++)
            _smoothPos[i] = Vector2.Lerp(_bonePos[i], _smoothPos[i], smooth);

        // ---- 顶点 ----
        float sideSign = _dir.y >= 0f ? 1f : -1f;
        var meshTr = _meshGo.transform;
        float z = transform.position.z;

        for (int i = 0; i < count; i++)
        {
            Vector2 tangent;
            if (i == 0) tangent = _smoothPos[1] - _smoothPos[0];
            else if (i == count - 1) tangent = _smoothPos[count - 1] - _smoothPos[count - 2];
            else
            {
                Vector2 a = (_smoothPos[i] - _smoothPos[i - 1]).normalized;
                Vector2 b = (_smoothPos[i + 1] - _smoothPos[i]).normalized;
                tangent = a + b;
            }
            if (tangent.sqrMagnitude < 1e-6f) tangent = _dir;
            tangent.Normalize();

            Vector2 side = new Vector2(-tangent.y, tangent.x) * sideSign;
            float halfW = _segHalf[i] * TailFlow.Width;      // 贴图只有 5~10px 高，乘个倍率才饱满
            Vector2 mid = _smoothPos[i] + side * _segCenter[i];
            Vector2 lo = mid - side * halfW;
            Vector2 hi = mid + side * halfW;

            _verts[i * 2] = meshTr.InverseTransformPoint(new Vector3(lo.x, lo.y, z));
            _verts[i * 2 + 1] = meshTr.InverseTransformPoint(new Vector3(hi.x, hi.y, z));
        }

        _mesh.Clear();
        _mesh.vertices = _verts;
        _mesh.uv = _uvs;
        _mesh.triangles = _tris;
        _mesh.RecalculateBounds();
    }
}
