using System;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  LaserSight - MDRZ9 的红色激光束（纯视觉，暂时不接供电/命中判定）
//  ---------------------------------------------------------------------------
//  反汇编确认：`GunScript.barrel` 是枪口 Transform（开火时从这里出射线），
//  它的 `right` 在世界空间就是枪管朝向 —— 身体用 localScale.x = -1 镜像时
//  Unity 会自动翻转，所以不用自己判断 isRight。
//
//  线用 LineRenderer 画，材质 Sprites/Default（不受 2D 光照影响，暗层里也是亮的），
//  sortingOrder 压在所有肢体之上。只有主手是 MDRZ9 时才显示。
//
//  以后接机制时：把 `EndPoint` 暴露出去，命中判定直接用同一条射线就行。
// ============================================================================

public static class LaserSight
{
    public static bool Enabled = true;
    public static float Range = 40f;
    public static float Width = 0.018f;
    public static Color BeamColor = new Color(1f, 0.16f, 0.12f, 0.55f);

    private static LineRenderer _line;
    private static GameObject _go;
    private static Material _mat;
    private static GunScript _gun;

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
            Width = Mathf.Clamp(plugin.Config.Bind("Cosmetic", "LaserSightWidth", 0.018f,
                "激光粗细（世界单位）").Value, 0.004f, 0.1f);
            BeamColor.a = Mathf.Clamp01(plugin.Config.Bind("Cosmetic", "LaserSightAlpha", 0.55f,
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
            if (item == null || item.id != "mdrz9") { Hide(); return; }

            var gun = item.GetComponent<GunScript>();
            if (gun == null || gun.barrel == null) { Hide(); return; }

            EnsureLine();
            if (_line == null) return;

            Vector2 origin = gun.barrel.position;
            Vector2 dir = gun.barrel.right;
            if (dir.sqrMagnitude < 1e-4f) { Hide(); return; }
            dir.Normalize();

            origin += dir * 0.08f;

            Vector2 end = origin + dir * Range;
            try
            {
                var hit = Physics2D.Raycast(origin, dir, Range, LayerMask.GetMask("Ground"));
                if (hit.collider != null) end = hit.point;
            }
            catch { }

            EndPoint = end;
            Active = true;

            _go.SetActive(true);
            _line.SetPosition(0, new Vector3(origin.x, origin.y, 0f));
            _line.SetPosition(1, new Vector3(end.x, end.y, 0f));
            _line.startColor = BeamColor;
            _line.endColor = new Color(BeamColor.r, BeamColor.g, BeamColor.b, BeamColor.a * 0.25f);
            _line.startWidth = Width;
            _line.endWidth = Width * 0.6f;

            if (_gun != gun)
            {
                _gun = gun;
                Plugin.Log.LogInfo("[STEMFishDLC] 激光束已挂到 MDRZ9");
            }
        }
        catch (Exception e)
        {
            Once($"[STEMFishDLC] 激光束异常: {e.Message}");
            Hide();
        }
    }

    private static void EnsureLine()
    {
        if (_line != null) return;
        try
        {
            _go = new GameObject("STEMFishDLC_Laser");
            UnityEngine.Object.DontDestroyOnLoad(_go);

            _line = _go.AddComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.useWorldSpace = true;
            _line.numCapVertices = 2;
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Stretch;

            var sh = Shader.Find("Sprites/Default");
            _mat = sh != null ? new Material(sh) : null;
            if (_mat != null) _line.material = _mat;

            _line.sortingOrder = 26000;
            _line.receiveShadows = false;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.startColor = BeamColor;
            _line.endColor = BeamColor;
            _line.startWidth = Width;
            _line.endWidth = Width;

            _go.SetActive(false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 激光束创建失败: {e.Message}");
            _line = null;
        }
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
