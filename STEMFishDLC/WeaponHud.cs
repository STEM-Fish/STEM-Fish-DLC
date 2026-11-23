using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace STEMFishDLC;

// ============================================================================
//  WeaponHud - 右下角 FPS 风格武器状态面板
//  ---------------------------------------------------------------------------
//  弹药显示改用"子弹格"（不再用长文字，彻底避免超出）：
//    1 个膛内格（亮 = roundInChamber==0 有弹，暗红 = 弹壳/空）
//    + 最多 12 个弹匣格（按 弹匣余弹/容量 比例点亮；空仓红、低于 1/3 黄、其余青绿）
//    射击模式用短标签区分：泵动/栓动 / 半自动 / 全自动
//
//  图标：物品贴图运行时转纯色剪影（RGB 置白 + 保留 alpha + alpha 包围盒自动裁边），
//        再用 Image.color 上色；对图集/不可读纹理也有效（Graphics.Blit 裁子矩形）。
//
//  数据（反汇编确认，全 public）：
//    GunScript: roundsInMag / magCapacity / hasMag / racked / safe / roundInChamber /
//               firingMode(Pump=0,SemiAuto=1,Auto=2) / feedType(Mag=0,Direct=1) /
//               structureDamage / animalDamage
//    RoundInChamber: Round=0（膛内有弹）/ Casing=1 / None=2
//    Body.attackCooldown（近战冷却）
// ============================================================================

public static class WeaponHud
{
    private static GameObject _root;
    private static GameObject _box;
    private static Image _icon;
    private static TextMeshProUGUI _name, _ammo, _mode, _damage;
    private static Image _accentBar;
    private static Image _chamberPip;
    private static readonly Image[] _magPips = new Image[12];
    private static bool _built;

    private static readonly Color BoxBg = new Color(0.045f, 0.055f, 0.07f, 0.84f);
    private static readonly Color AccentColor = new Color(0.20f, 0.78f, 0.72f, 1f);
    private static readonly Color IconColor = new Color(0.86f, 0.96f, 0.95f, 1f);
    private static readonly Color NameColor = new Color(0.93f, 0.96f, 0.96f, 1f);
    private static readonly Color DimColor = new Color(0.70f, 0.76f, 0.78f, 1f);
    private static readonly Color PipEmpty = new Color(0.20f, 0.23f, 0.26f, 1f);
    private static readonly Color PipOk = new Color(0.35f, 0.78f, 0.68f, 1f);
    private static readonly Color PipWarn = new Color(0.85f, 0.66f, 0.28f, 1f);
    private static readonly Color PipBad = new Color(0.78f, 0.28f, 0.28f, 1f);

    private const float MeleeDamage = 30f;
    private const float MeleeDistance = 4.5f;

    private static readonly Dictionary<int, Sprite> _silhouettes = new Dictionary<int, Sprite>();

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("HUD", "WeaponHud", true, "显示右下角武器状态 HUD").Value;
            Scale = Mathf.Clamp(plugin.Config.Bind("HUD", "WeaponHudScale", 1.12f,
                "右下角武器状态 HUD 缩放（1 = 原尺寸；锚点在右下角，放大时向上/向左长，不会压到加速条）").Value, 0.6f, 2f);
        }
        catch { }
    }

    public static bool Enabled = true;
    public static float Scale = 1.12f;

    public static void Update()
    {
        try
        {
            var body = PlayerCamera.main?.body;
            if (body == null)
            {
                if (_box != null && _box.activeSelf) _box.SetActive(false);
                return;
            }

            if (!_built) Build();
            if (_box == null) return;

            bool hide = DlcPanel.AnyOverlayOpen || !Enabled;
            if (_box.activeSelf == hide) _box.SetActive(!hide);
            if (hide) return;

            var item = body.GetItem(0);
            if (item == null) { ShowEmptyHand(body); return; }

            var gun = item.GetComponent<GunScript>();
            if (gun != null) ShowGun(gun, item);
            else ShowMelee(item, body);
        }
        catch (Exception e)
        {
            if (_mode != null) _mode.text = UiText.T("hud.readfail", Lang.T("读取失败", "READ FAILED"));
            Plugin.Log.LogWarning($"[STEMFishDLC] 武器 HUD 失败: {e.Message}");
        }
    }

    // ---------------- 枪械 ----------------
    private static void ShowGun(GunScript gun, Item item)
    {
        SetIcon(item);
        _name.text = SafeName(item);
        SetPipsVisible(true);

        if (GunHandling.IsReloading)
        {
            _ammo.text = "<color=#4fc3a1>" + string.Format(UiText.T("hud.reload", Lang.T("换弹 {0}s", "RELOAD {0}s")), GunHandling.ReloadRemaining) + "</color>";
            _mode.text = "";                        // 不再显示流程文字（会和弹药数字重叠）
            SetAllPips(PipEmpty);
            if (_chamberPip != null) _chamberPip.color = PipEmpty;
            _damage.text = "";
            if (_accentBar != null) _accentBar.color = AccentColor;
            return;
        }

        int cap = Mathf.Max(1, gun.magCapacity);
        int rounds = Mathf.Clamp(gun.roundsInMag, 0, cap);
        float frac = (float)rounds / cap;

        _ammo.text = $"<color=#{(frac <= 0.001f ? "d05a5a" : frac < 0.34f ? "d8a83e" : "8fd6c4")}>{rounds}</color>/{cap}";
        _mode.text = ModeName(gun.firingMode);

        // 膛内格：Round=0 有弹
        bool chambered = gun.roundInChamber == 0;
        if (_chamberPip != null)
            _chamberPip.color = chambered ? PipOk : PipBad;

        // 弹匣格
        int shown = Mathf.Min(cap, _magPips.Length);
        int filled = Mathf.RoundToInt(frac * shown);
        Color fillColor = frac <= 0.001f ? PipBad : (frac < 0.34f ? PipWarn : PipOk);
        for (int i = 0; i < _magPips.Length; i++)
        {
            var pip = _magPips[i];
            if (pip == null) continue;
            pip.gameObject.SetActive(i < shown);
            if (i < shown) pip.color = i < filled ? fillColor : PipEmpty;
        }

        // 底部只留极短的状态提示（避免溢出）
        string extra = "";
        if (!gun.hasMag) extra = "  " + UiText.T("hud.nomag", Lang.T("无弹匣", "NO MAG"));
        else if (gun.racked) extra = "  " + UiText.T("hud.racked", Lang.T("枪机开", "CHAMBER OPEN"));
        if (gun.safe) extra += "  " + UiText.T("hud.safe", Lang.T("保险", "SAFE"));
        _damage.text = extra.TrimStart();
        _damage.color = string.IsNullOrEmpty(extra) ? new Color(0.62f, 0.68f, 0.70f, 1f) : PipWarn;

        if (_accentBar != null)
            _accentBar.color = chambered && rounds > 0 ? AccentColor : PipBad;
    }

    private static string ModeName(GunScript.FiringMode mode)
    {
        switch ((int)mode)
        {
            case 0: return UiText.T("hud.mode.pump", Lang.T("泵动/栓动", "PUMP/BOLT"));
            case 1: return UiText.T("hud.mode.semi", Lang.T("半自动", "SEMI"));
            default: return UiText.T("hud.mode.auto", Lang.T("全自动", "AUTO"));
        }
    }

    // ---------------- 近战/空手 ----------------
    private static void ShowMelee(Item item, Body body)
    {
        SetIcon(item);
        _name.text = SafeName(item);
        SetPipsVisible(false);
        MeleeLine(body);
        if (_accentBar != null) _accentBar.color = new Color(0.62f, 0.55f, 0.30f, 1f);
    }

    private static void ShowEmptyHand(Body body)
    {
        _name.text = UiText.T("hud.unarmed", Lang.T("空手", "UNARMED"));
        if (_icon != null) _icon.enabled = false;
        SetPipsVisible(false);
        MeleeLine(body);
        if (_accentBar != null) _accentBar.color = new Color(0.45f, 0.47f, 0.50f, 1f);
    }

    private static void MeleeLine(Body body)
    {
        float cd = body.attackCooldown;
        _ammo.text = cd > 0.01f
                ? "<color=#d8a83e>" + string.Format(UiText.T("hud.cooldown", Lang.T("冷却 {0}s", "COOLDOWN {0}s")), cd) + "</color>"
                : "<color=#8fd6c4>" + UiText.T("hud.ready", Lang.T("就绪", "READY")) + "</color>";
        _mode.text = UiText.T("hud.melee", Lang.T("近战", "MELEE"));
        _damage.text = string.Format(UiText.T("hud.damage", Lang.T("伤害 {0}", "DMG {0}")), MeleeDamage)
                + " · " + string.Format(UiText.T("hud.range", Lang.T("范围 {0}", "RANGE {0}")), MeleeDistance);
        _damage.color = new Color(0.62f, 0.68f, 0.70f, 1f);
    }

    private static void SetPipsVisible(bool on)
    {
        if (_chamberPip != null) _chamberPip.gameObject.SetActive(on);
        for (int i = 0; i < _magPips.Length; i++)
            if (_magPips[i] != null) _magPips[i].gameObject.SetActive(on);
    }

    private static void SetAllPips(Color c)
    {
        if (_chamberPip != null) _chamberPip.color = c;
        foreach (var p in _magPips) if (p != null) p.color = c;
    }

    private static void SetIcon(Item item)
    {
        if (_icon == null) return;
        try
        {
            var sr = item.GetComponentInChildren<SpriteRenderer>(true);
            var sp = sr != null ? sr.sprite : null;
            var sil = sp != null ? GetSilhouette(sp) : null;

            _icon.sprite = sil != null ? sil : sp;
            _icon.color = IconColor;
            _icon.enabled = _icon.sprite != null;
            _icon.preserveAspect = true;
        }
        catch { _icon.enabled = false; }
    }

    private static string SafeName(Item item)
    {
        try
        {
            var n = item.fullName;
            return string.IsNullOrEmpty(n) ? item.id : n;
        }
        catch { return "?"; }
    }

    // ==========================================================================
    //  贴图 → 纯色剪影（保留 alpha、RGB 置白、按 alpha 自动裁边）
    // ==========================================================================
    private static Sprite GetSilhouette(Sprite src)
    {
        if (src == null) return null;
        int id = src.GetInstanceID();
        if (_silhouettes.TryGetValue(id, out var cached)) return cached;
        _silhouettes[id] = null;

        Sprite result = null;
        var prevActive = RenderTexture.active;
        RenderTexture rt = null;
        Texture2D readback = null;
        try
        {
            var tex = src.texture;
            if (tex == null) return null;
            var tr = src.textureRect;
            int w = Mathf.Max(1, Mathf.RoundToInt(tr.width));
            int h = Mathf.Max(1, Mathf.RoundToInt(tr.height));

            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var scale = new Vector2(tr.width / tex.width, tr.height / tex.height);
            var offset = new Vector2(tr.x / tex.width, tr.y / tex.height);
            Graphics.Blit(tex, rt, scale, offset);

            RenderTexture.active = rt;
            readback = new Texture2D(w, h, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            readback.Apply();

            var px = readback.GetPixels32();
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (px[y * w + x].a <= 12) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            if (maxX < 0) return null;

            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            var outTex = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
            outTex.filterMode = FilterMode.Point;
            outTex.wrapMode = TextureWrapMode.Clamp;

            var outPx = new Color32[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    var s = px[(y + minY) * w + (x + minX)];
                    outPx[y * cw + x] = new Color32(255, 255, 255, s.a);
                }
            outTex.SetPixels32(outPx);
            outTex.Apply();

            result = Sprite.Create(outTex, new Rect(0, 0, cw, ch), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            result.name = src.name + "_sil";
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 剪影生成失败 {src.name}: {e.Message}");
        }
        finally
        {
            RenderTexture.active = prevActive;
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
            if (readback != null) UnityEngine.Object.Destroy(readback);
        }

        _silhouettes[id] = result;
        return result;
    }

    // ==========================================================================
    //  构建
    // ==========================================================================
    private static void Build()
    {
        _root = new GameObject("STEMFishDLC_WeaponHUD");
        UnityEngine.Object.DontDestroyOnLoad(_root);

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 31000;
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();

        // 右侧贴边不变，向上加高避开加速指示器
        var boxRt = NewRect("WeaponBox", _root.transform);
        boxRt.anchorMin = boxRt.anchorMax = new Vector2(1f, 0f);
        boxRt.pivot = new Vector2(1f, 0f);
        boxRt.sizeDelta = new Vector2(340, 104);
        boxRt.anchoredPosition = new Vector2(-26, 118);
        boxRt.localScale = Vector3.one * Scale;      // 锚点/轴心都在右下角，放大只往左上长
        _box = boxRt.gameObject;
        var bg = _box.AddComponent<Image>();
        bg.color = BoxBg;
        bg.raycastTarget = false;

        var barRt = NewRect("Accent", _box.transform);
        barRt.anchorMin = new Vector2(0f, 0f); barRt.anchorMax = new Vector2(0f, 1f);
        barRt.pivot = new Vector2(0f, 0.5f);
        barRt.sizeDelta = new Vector2(3, -12);
        _accentBar = barRt.gameObject.AddComponent<Image>();
        _accentBar.color = AccentColor;
        _accentBar.raycastTarget = false;

        var iconRt = NewRect("Icon", _box.transform);
        iconRt.anchorMin = iconRt.anchorMax = new Vector2(0f, 0.5f);
        iconRt.pivot = new Vector2(0f, 0.5f);
        iconRt.sizeDelta = new Vector2(78, 78);
        iconRt.anchoredPosition = new Vector2(12, 0);
        _icon = iconRt.gameObject.AddComponent<Image>();
        _icon.raycastTarget = false;
        _icon.preserveAspect = true;
        _icon.enabled = false;

        _name = NewText("Name", _box.transform, "", 21, NameColor);
        var nrt = _name.rectTransform;
        nrt.anchorMin = new Vector2(0f, 1f); nrt.anchorMax = new Vector2(1f, 1f);
        nrt.pivot = new Vector2(0.5f, 1f);
        nrt.sizeDelta = new Vector2(-106, 26);
        nrt.anchoredPosition = new Vector2(47, -6);
        _name.alignment = TextAlignmentOptions.Left;
        _name.enableWordWrapping = false;
        _name.overflowMode = TextOverflowModes.Ellipsis;

        _ammo = NewText("Ammo", _box.transform, "", 22, NameColor);
        var art = _ammo.rectTransform;
        art.anchorMin = new Vector2(0f, 1f); art.anchorMax = new Vector2(0f, 1f);
        art.pivot = new Vector2(0f, 1f);
        art.sizeDelta = new Vector2(150, 28);
        art.anchoredPosition = new Vector2(100, -32);
        _ammo.alignment = TextAlignmentOptions.Left;
        _ammo.enableWordWrapping = false;

        _mode = NewText("Mode", _box.transform, "", 16, DimColor);
        var mrt = _mode.rectTransform;
        mrt.anchorMin = new Vector2(0f, 1f); mrt.anchorMax = new Vector2(1f, 1f);
        mrt.pivot = new Vector2(1f, 1f);
        mrt.sizeDelta = new Vector2(150, 24);
        mrt.anchoredPosition = new Vector2(-12, -34);
        _mode.alignment = TextAlignmentOptions.Right;
        _mode.enableWordWrapping = false;

        // 子弹格：1 个膛内位 + 12 个弹匣位
        float px = 100f;
        _chamberPip = MakePip("ChamberPip", _box.transform, px, -64f, 10f);
        px += 16f;
        for (int i = 0; i < _magPips.Length; i++)
        {
            _magPips[i] = MakePip("Pip" + i, _box.transform, px + i * 12f, -64f, 8f);
        }

        _damage = NewText("Damage", _box.transform, "", 15, new Color(0.62f, 0.68f, 0.70f, 1f));
        var drt = _damage.rectTransform;
        drt.anchorMin = new Vector2(0f, 1f); drt.anchorMax = new Vector2(1f, 1f);
        drt.pivot = new Vector2(0.5f, 1f);
        drt.sizeDelta = new Vector2(-106, 20);
        drt.anchoredPosition = new Vector2(47, -82);
        _damage.alignment = TextAlignmentOptions.Left;
        _damage.enableWordWrapping = false;
        _damage.overflowMode = TextOverflowModes.Ellipsis;

        _box.SetActive(false);
        _built = true;
        Plugin.Log.LogInfo("[STEMFishDLC] 武器 HUD 构建完成（右下角，子弹格）");
    }

    private static Image MakePip(string name, Transform parent, float x, float y, float w)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, 16);
        rt.anchoredPosition = new Vector2(x, y);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = PipEmpty;
        img.raycastTarget = false;
        return img;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, Color color)
    {
        var rt = NewRect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = DlcPanel.Font;
        if (font != null) t.font = font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.raycastTarget = false;
        return t;
    }
}
