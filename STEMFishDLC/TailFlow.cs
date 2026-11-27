using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  TailFlow - SF-07 的尾巴分段飘动
//  ---------------------------------------------------------------------------
//  原版 `TailScript.Update()` 只做一件事：把**整根**尾巴绕根部转到移动方向
//  （SignedAngle + LerpAngle，最后 clamp 到 ±80°）。所以尾巴是一根硬棍子，
//  跑起来只是整根甩，没有"尾梢跟不上身体"的感觉。
//
//  参考 SkinSyncMod 的 TailFlowDeform，这里用同一套思路但换成更省事的实现：
//    · 把尾巴 sprite 沿长轴切成 N 段（每段一个 SpriteRenderer，左端为轴心）
//    · 一条 Verlet 链（世界坐标）当骨架：根点钉在尾巴根部，其余点受重力 + 风
//    · 约束迭代保持段长，尾梢自然滞后、过冲、回弹
//    · 每帧把段摆到链上，段的方向 = 相邻两点的连线
//
//  ★ 为什么不自己建 mesh：mesh 要自己找 URP 2D 的 lit 材质，稍微搞错就
//    不受光照（暗层里尾巴会发光）。分段 SpriteRenderer 直接继承原尾巴的
//    sharedMaterial / sortingLayer / color，光照表现和原版一模一样。
//
//  ★ 原版的 TailScript 不关：它负责"整根转到移动方向"，我们只在上面叠加弯曲。
//    两者叠加出来的效果是"整根甩 + 尾梢拖"，比任何一边单独用都自然。
// ============================================================================

public static class TailFlow
{
    public static bool Enabled = true;
    public static int Segments = 9;
    public static float WaveAmp = 1f;
    public static float Damping = 0.86f;

    private static TailRig _rig;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Cosmetic", "TailFlow", true,
                "SF-07 的尾巴分段飘动（关掉 = 只有原版的整根旋转）").Value;
            Segments = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowSegments", 9,
                "尾巴分几段（越多越柔顺，6~12 合适）").Value, 3, 16);
            WaveAmp = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowAmp", 1f,
                "飘动幅度（0 = 只有惯性，1 = 默认，2 = 很飘）").Value, 0f, 3f);
            Damping = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "TailFlowDamping", 0.86f,
                "阻尼（越小停得越快；0.95 会很飘）").Value, 0.5f, 0.98f);
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

            // 原版角色：尾巴保持原样（我们的美术只给 SF-07）
            if (CharacterSelect.Current != CharacterSelect.Sf07) { Kill(); return; }

            if (_rig != null && !_rig.Alive) { _rig = null; }
            if (_rig != null && _rig.Owner != body) Kill();

            if (_rig == null)
            {
                var tail = body.GetComponentInChildren<TailScript>(true);
                if (tail == null) return;

                var sr = tail.GetComponent<SpriteRenderer>();
                if (sr == null) sr = tail.GetComponentInChildren<SpriteRenderer>(true);
                if (sr == null || sr.sprite == null) return;

                // 复用已有组件：EngineTick 一帧可能 Pump 多次，直接 AddComponent 会堆一摞
                _rig = tail.gameObject.GetComponent<TailRig>();
                if (_rig == null) _rig = tail.gameObject.AddComponent<TailRig>();
                _rig.Bind(sr, body);
                Plugin.Log.LogInfo($"[STEMFishDLC] 尾巴飘动已挂载（sprite={sr.sprite.name}，{Segments} 段）");
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
        // 组件必须销毁：只把引用置空的话，下一次 Tick 会在同一个物体上再挂一个
        try { UnityEngine.Object.Destroy(rig); } catch { }
    }
}

// ============================================================================
//  挂在尾巴 GameObject 上的组件
// ============================================================================
public class TailRig : MonoBehaviour
{
    private SpriteRenderer _origin;      // 原版那根整根尾巴
    private Body _owner;

    private Transform[] _segTr;
    private SpriteRenderer[] _segSr;
    private Sprite[] _segSprites;

    private Vector2[] _pts;              // 世界坐标骨架
    private Vector2[] _prev;

    private float _segLen;
    private float _halfLocal;
    private float _localLen;
    private Sprite _lastSprite;
    private bool _built;
    private bool _restored;

    public bool Alive => this != null && _origin != null;
    public Body Owner => _owner;

    public void Bind(SpriteRenderer origin, Body owner)
    {
        _origin = origin;
        _owner = owner;
        _origin.enabled = false;         // 原图藏起来，只留骨架数据
        _restored = false;
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        try { if (_origin != null) _origin.enabled = true; } catch { }
        ClearSegments();
    }

    private void OnDestroy() => Restore();

    // 换皮肤 / 读档重建时 sprite 会换掉，需要重新切片
    public void Sync()
    {
        if (_origin == null) return;
        var sp = _origin.sprite;
        if (sp == null) { ClearSegments(); return; }
        if (_built && sp == _lastSprite) return;
        Rebuild(sp);
    }

    private void Rebuild(Sprite sp)
    {
        ClearSegments();

        var tex = sp.texture;
        if (tex == null) return;

        var rect = sp.rect;
        float ppu = sp.pixelsPerUnit <= 0.01f ? 8f : sp.pixelsPerUnit;
        int totalPx = Mathf.Max(1, Mathf.RoundToInt(rect.width));

        int want = Mathf.Clamp(TailFlow.Segments, 3, 16);
        int segPx = Mathf.Max(2, totalPx / want);
        int count = Mathf.CeilToInt(totalPx / (float)segPx);

        _segTr = new Transform[count];
        _segSr = new SpriteRenderer[count];
        _segSprites = new Sprite[count];

        for (int i = 0; i < count; i++)
        {
            int x0 = i * segPx;
            int x1 = Mathf.Min(totalPx, x0 + segPx + 1);   // 相邻段重叠 1px，视觉上不留缝
            if (x1 <= x0) x1 = Mathf.Min(totalPx, x0 + 1);

            var r = new Rect(rect.x + x0, rect.y, x1 - x0, rect.height);
            Sprite piece;
            try
            {
                piece = Sprite.Create(tex, r, new Vector2(0f, 0.5f), ppu, 0, SpriteMeshType.FullRect);
                piece.name = sp.name + "_tf" + i;
            }
            catch { continue; }

            var go = new GameObject("TailSeg" + i);
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = piece;
            sr.sharedMaterial = _origin.sharedMaterial;
            sr.sortingLayerID = _origin.sortingLayerID;
            sr.sortingOrder = _origin.sortingOrder;
            sr.color = _origin.color;
            // ★ 不复制 flipX/flipY：pivot 已经设成左端，翻转会让段朝反方向画
            sr.flipX = false;
            sr.flipY = false;

            _segTr[i] = go.transform;
            _segSr[i] = sr;
            _segSprites[i] = piece;
        }

        _localLen = rect.width / ppu;
        _halfLocal = _localLen * 0.5f;

        float scale = Mathf.Abs(transform.lossyScale.x);
        if (scale < 0.0001f) scale = 1f;
        float worldLen = _localLen * scale;
        _segLen = worldLen / Mathf.Max(1, count);

        _pts = new Vector2[count + 1];
        _prev = new Vector2[count + 1];
        ResetChain();

        _lastSprite = sp;
        _built = true;

        Plugin.Log.LogInfo($"[STEMFishDLC] 尾巴切片: {sp.name} {totalPx}px → {count} 段 | " +
                           $"ppu={ppu:0.##} 世界长={worldLen:0.##}u 段长={_segLen:0.###}u " +
                           $"缩放={scale:0.##} 镜像={(transform.lossyScale.x < 0f ? "是" : "否")}");
    }

    private void ResetChain()
    {
        Vector2 root = RootWorld();
        Vector2 dir = transform.TransformDirection(Vector3.right);
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.right;
        for (int i = 0; i < _pts.Length; i++)
        {
            _pts[i] = root + dir * (_segLen * i);
            _prev[i] = _pts[i];
        }
    }

    private Vector2 RootWorld()
    {
        Vector3 local = new Vector3(-_halfLocal, 0f, 0f);
        Vector3 w = transform.TransformPoint(local);
        return new Vector2(w.x, w.y);
    }

    private void LateUpdate()
    {
        if (!_built || _pts == null || _pts.Length < 2) return;
        if (_origin == null) return;

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (dt <= 0f) return;

        Vector2 root = RootWorld();
        float amp = TailFlow.WaveAmp;
        float damp = TailFlow.Damping;
        float t = Time.time;

        // ---- 1. 积分（世界坐标，Verlet） ----
        for (int i = 1; i < _pts.Length; i++)
        {
            Vector2 p = _pts[i];
            Vector2 v = (p - _prev[i]) * damp;

            float k = i / (float)(_pts.Length - 1);       // 越靠近尾梢越飘
            float wind = (Mathf.Sin(t * 1.9f + i * 0.55f) * 2.6f +
                          Mathf.Sin(t * 0.8f + 1.2f) * 1.5f) * k * amp;

            Vector2 acc = new Vector2(wind, -0.6f + Mathf.Sin(t * 2.3f + i * 0.7f) * 1.2f * k * amp);

            _prev[i] = p;
            _pts[i] = p + v + acc * dt * dt;
        }
        _pts[0] = root;

        // ---- 2. 段长约束 ----
        for (int iter = 0; iter < 3; iter++)
        {
            for (int i = 1; i < _pts.Length; i++)
            {
                Vector2 d = _pts[i] - _pts[i - 1];
                float len = d.magnitude;
                if (len < 1e-5f) continue;
                Vector2 corr = d * (1f - _segLen / len);
                if (i == 1)
                {
                    _pts[i] -= corr;                       // 根点不动，全给第一个点
                }
                else
                {
                    _pts[i] -= corr * 0.5f;
                    _pts[i - 1] += corr * 0.5f;
                }
            }
            _pts[0] = root;
        }

        // ---- 3. 摆段 ----
        // ★ Body.SwitchDir 用 localScale.x = -1 表示朝向，镜像会让"世界角度"和
        //   子物体的局部角度反号。所以这里统一算局部角度：
        //      世界角 = 父级角 - 局部角（镜像时）
        //      不镜像 = 父级角 + 局部角
        //   位置用 InverseTransformPoint，负缩放会被自动处理。
        float z = transform.position.z;
        float parentAng = transform.eulerAngles.z;
        float sgn = Mathf.Sign(transform.lossyScale.x);
        if (Mathf.Abs(sgn) < 0.5f) sgn = 1f;

        for (int i = 0; i < _segTr.Length && i + 1 < _pts.Length; i++)
        {
            var tr = _segTr[i];
            if (tr == null) continue;

            Vector2 a = _pts[i];
            Vector2 d = _pts[i + 1] - a;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

            tr.localPosition = transform.InverseTransformPoint(new Vector3(a.x, a.y, z));
            tr.localRotation = Quaternion.Euler(0f, 0f, (ang - parentAng) * sgn);
        }
    }

    private void ClearSegments()
    {
        if (_segTr != null)
        {
            for (int i = 0; i < _segTr.Length; i++)
                if (_segTr[i] != null) Destroy(_segTr[i].gameObject);
        }
        if (_segSprites != null)
        {
            for (int i = 0; i < _segSprites.Length; i++)
                if (_segSprites[i] != null) Destroy(_segSprites[i]);
        }
        _segTr = null;
        _segSr = null;
        _segSprites = null;
        _pts = null;
        _prev = null;
        _built = false;
        _lastSprite = null;
    }
}
