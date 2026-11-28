using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LaserSight - MDRZ9 的红色激光束（纯视觉，暂时不接供电/命中判定）
//  ---------------------------------------------------------------------------
//  反汇编确认：`GunScript.barrel` 是枪口 Transform（开火时从这里出射线）。
//
//  方向：`barrel.right` 只吃 rotation、**不吃 scale**，而 `Body.SwitchDir` 用
//  `localScale.x = -1` 表示朝向 —— 所以镜像时枪管视觉朝左、right 却仍然指 +X。
//  要乘 `Mathf.Sign(barrel.lossyScale.x)` 把方向掰回来。
//
//  ★ 为什么不用 LineRenderer：
//    URP 2D 下 `Shader.Find("Sprites/Default")` 是内置管线 shader，LineRenderer
//    用它要么渲染成粉色、要么直接不可见（0.25.0/0.25.1 就是这样，肉眼完全看不到）。
//    改成最朴素也最可靠的办法：一张 **1x1 白色 Sprite**（ppu = 1），
//    SpriteRenderer 把 localScale.x 拉到线长、y 拉到粗细，旋转对准方向 ——
//    和角色身上的其它 sprite 走完全同一条渲染路径，不存在"看不见"的可能。
//
//  两层：核心（细、亮）+ 光晕（3 倍宽、淡）—— 亮背景上也有轮廓。
// ============================================================================

public static class LaserSight
{
    public static bool Enabled = true;
    public static float Range = 40f;
    public static float Width = 0.040f;
    public static Color BeamColor = new Color(1f, 0.16f, 0.12f, 0.90f);

    private static GameObject _go;
    private static SpriteRenderer _core, _glow;
    private static Sprite _beam;
    private static GunScript _gun;
    private static bool _built;

    public static Vector2 EndPoint { get; private set; }
    public static bool Active { get; private set; }

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Cosmetic", "LaserSight", true,
                "MDRZ9 的红色激光束（纯视觉，不影响命中）").Value;
            Range = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "LaserSightRange", 40f,
                "激光长度上限（世界单位）").Value, 3f, 120f);
            Width = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "LaserSightWidthV2", 0.040f,
                "激光粗细（世界单位）").Value, 0.006f, 0.2f);
            BeamColor.a = Mathf.Clamp01(plugin.Config.Bind("Cosmetic", "LaserSightAlpha", 0.90f,
                "激光不透明度（0~1）").Value);
        }
        catch { }
    }

    public static void Update()
    {
        try
        {
            if (!Enabled) { Hide(); return; }
            if (CharacterSelect.Current != CharacterSelect.Sf07) { Hide(); return; }

            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body == null) { Hide(); return; }

            var item = body.GetItem(0);
            if (item == null || (item.id != "mdrz9" && item.id != "mdr")) { Hide(); return; }   // mdr = 旧存档里的别名

            var gun = item.GetComponent<GunScript>();
            if (gun == null || gun.barrel == null) { Hide(); return; }

            Ensure();

            Vector2 origin = gun.barrel.position;
            Vector2 dir = gun.barrel.right;

            float sgn = Mathf.Sign(gun.barrel.lossyScale.x);
            if (Mathf.Approximately(sgn, 0f)) sgn = 1f;
            dir *= sgn;

            if (dir.sqrMagnitude < 1e-4f) { Hide(); return; }
            dir.Normalize();

            origin += dir * 0.06f;

            Vector2 end = origin + dir * Range;
            try
            {
                var hit = Physics2D.Raycast(origin, dir, Range, LayerMask.GetMask("Ground"));
                if (hit.collider != null) end = hit.point;
            }
            catch { }

            EndPoint = end;
            Active = true;

            float len = Vector2.Distance(origin, end);
            if (len < 0.01f) { Hide(); return; }

            Vector2 mid = (origin + end) * 0.5f;
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

            _go.SetActive(true);
            _go.transform.position = new Vector3(mid.x, mid.y, 0f);
            _go.transform.rotation = Quaternion.Euler(0f, 0f, ang);

            _core.transform.localScale = new Vector3(len, Width, 1f);
            _core.color = BeamColor;

            if (_glow != null)
            {
                _glow.transform.localScale = new Vector3(len, Width * 3.4f, 1f);
                _glow.color = new Color(BeamColor.r, BeamColor.g * 0.45f, BeamColor.b * 0.35f, BeamColor.a * 0.28f);
            }

            if (_gun != gun)
            {
                _gun = gun;
                Plugin.Log.LogInfo($"[STEMFishDLC] 激光束已挂到 MDRZ9（宽 {Width:0.###}u，{(_core.sharedMaterial != null ? _core.sharedMaterial.shader.name : "默认材质")}）");
            }
        }
        catch (Exception e)
        {
            Once($"[STEMFishDLC] 激光束异常: {e.Message}");
            Hide();
        }
    }

    // ======================================================================
    //  构建：1x1 白 sprite + SpriteRenderer
    // ======================================================================
    private static void Ensure()
    {
        if (_built) return;
        _built = true;
        try
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "LaserPixel";

            _beam = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f,
                                  0, SpriteMeshType.FullRect);
            _beam.name = "LaserBeam";

            _go = new GameObject("STEMFishDLC_Laser");
            UnityEngine.Object.DontDestroyOnLoad(_go);

            var mat = BeamMaterial();

            _glow = MakeLayer("Glow", 25998, mat);
            _core = MakeLayer("Core", 25999, mat);

            _go.SetActive(false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 激光束创建失败: {e.Message}");
            _built = false;
        }
    }

    private static SpriteRenderer MakeLayer(string name, int order, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_go.transform, false);
        go.transform.localPosition = Vector3.zero;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _beam;
        sr.drawMode = SpriteDrawMode.Simple;
        sr.sortingOrder = order;
        sr.color = BeamColor;
        if (mat != null) sr.sharedMaterial = mat;
        return sr;
    }

    private static Material BeamMaterial()
    {
        string[] names =
        {
            "Universal Render Pipeline/2D/Sprite-Unlit-Default",
            "Sprites/Default",
            "Universal Render Pipeline/Unlit",
        };
        for (int i = 0; i < names.Length; i++)
        {
            var sh = Shader.Find(names[i]);
            if (sh == null) continue;
            try { return new Material(sh); } catch { }
        }
        return null;
    }

    private static void Hide()
    {
        Active = false;
        _gun = null;
        if (_go != null && _go.activeSelf) _go.SetActive(false);
    }

    private static readonly System.Collections.Generic.HashSet<string> _once = new System.Collections.Generic.HashSet<string>();
    private static void Once(string msg)
    {
        if (_once.Add(msg)) Plugin.Log.LogWarning(msg);
    }
}
