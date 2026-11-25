using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

namespace STEMFishDLC;

// ============================================================================
//  HudButton - 在左下角 HUD 挂一个"战术面板"按钮
//  ---------------------------------------------------------------------------
//  反汇编确认的原版机制：
//    健康面板按钮 = uGUI Button，onClick 绑 PlayerCamera.WoundViewButton()
//                 图标 sprite 来自 PlayerCamera.woundButtonNormal / woundButtonFlash / woundButtonScreen
//    面板开关     = PlayerCamera.ToggleWoundView(bool)
//
//  我们不去碰原版的按钮逻辑（那样会破坏它的"受伤闪烁"提示），而是：
//    ① 克隆原版按钮 GameObject（同款外观 / hover / 尺寸 / 锚点 = 视觉天然一致）
//    ② 清空克隆体的 onClick，改绑 DlcPanel.Toggle
//    ③ 换成自己画的像素图标（白色像素 + 轻微青色调），跟 HUD 风格一致
//    ④ 叠放到那一列按钮的最上方
//
//  进入世界（PlayerCamera.main / woundButton 就绪）后创建一次；场景重开按钮被销毁会自动重建。
// ============================================================================

public static class HudButton
{
    private const string ButtonName = "STEMFishTacticalButton";
    private const float Gap = 4f;          // 与原版按钮列一致的间距

    private static GameObject _clone;
    private static Sprite _icon;
    private static float _offX, _offY;     // 微调（屏幕像素）

    public static void Init(Plugin plugin)
    {
        try
        {
            _offX = plugin.Config.Bind("HUD", "ButtonOffsetX", 0f, "战术终端按钮水平微调（屏幕像素，负数向左）").Value;
            _offY = plugin.Config.Bind("HUD", "ButtonOffsetY", 0f, "战术终端按钮垂直微调（屏幕像素，负数向下）").Value;
        }
        catch { }
    }

    // 插件 Update 每帧调用（失败重试，直到成功为止）
    public static void Update()
    {
        try
        {
            if (!CharacterSelect.IsPlayerSf07())
            {
                if (_clone != null && _clone.activeSelf) _clone.SetActive(false);
                return;
            }

            if (_clone != null)
            {
                // 健康 / 制作 / 终端面板打开时让位（原版 HUD 也是这个行为）
                bool hide = DlcPanel.AnyOverlayOpen;
                if (_clone.activeSelf == hide) _clone.SetActive(!hide);
                return;
            }

            var pc = PlayerCamera.main;
            if (pc == null || pc.woundButton == null) return;   // 还没进世界 / HUD 未就绪

            // Button 可能在图标本身、父节点或子节点上，三种都试
            var srcButton = pc.woundButton.GetComponentInParent<Button>();
            if (srcButton == null) srcButton = pc.woundButton.GetComponentInChildren<Button>();
            if (srcButton == null) srcButton = pc.woundButton.GetComponent<Button>();
            if (srcButton == null) return;

            Create(pc, srcButton);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] HUD 按钮创建失败: {e.Message}");
        }
    }

    private static void Create(PlayerCamera pc, Button srcButton)
    {
        var srcRt = srcButton.transform as RectTransform;
        if (srcRt == null) return;

        // ★ 关键：不再挂在 HUD 容器里（容器可能带 LayoutGroup / Mask / 尺寸只够放原有按钮，
        //   挂进去会被布局顶掉或被裁剪 → 上一版按钮就这样"消失"了）。
        //   改为挂到 Canvas 根节点，用源按钮的屏幕坐标自己定位，完全不受容器约束。
        var canvas = srcButton.GetComponentInParent<Canvas>();
        var canvasRt = canvas != null ? canvas.transform as RectTransform : null;
        Transform parentT = canvasRt != null ? (Transform)canvasRt : srcButton.transform.parent;
        if (parentT == null) return;

        var go = UnityEngine.Object.Instantiate(srcButton.gameObject, parentT);
        go.name = ButtonName;

        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
        {
            // 清掉从源按钮继承的锚点/布局约束，改用绝对定位
            var le = go.GetComponent<LayoutElement>();
            if (le != null) UnityEngine.Object.Destroy(le);

            var corners = new Vector3[4];
            srcRt.GetWorldCorners(corners);          // Overlay 画布：世界坐标 == 屏幕坐标
            float w = corners[2].x - corners[0].x;
            float h = corners[2].y - corners[0].y;
            if (w < 1f) w = 48f;
            if (h < 1f) h = 48f;

            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            // 直接沿用原按钮的本地尺寸（rect.size 已经是画布单位，避免缩放换算误差）
            rt.sizeDelta = srcRt.rect.size;

            // 目标：整个 Canvas 里"同一竖列"的最上方元素，再往上放一格
            // （原版按钮可能分散在不同父容器里，只找兄弟节点会漏掉"制作"按钮）
            // ★ x 以"制作"按钮为准（没有则退回健康按钮）——之前只取健康按钮的中心，
            //   和"制作/健康"那一列差了一点，看起来偏右。
            var taken = new System.Collections.Generic.List<Rect>();
            float srcCenterX = (corners[0].x + corners[2].x) * 0.5f;
            var craftRt = pc.craftButton != null ? pc.craftButton.transform as RectTransform : null;
            if (craftRt != null)
            {
                var cr = WorldRect(craftRt);
                if (cr.width > 8f) srcCenterX = (cr.xMin + cr.xMax) * 0.5f;
            }
            float srcTop0 = corners[1].y;
            float srcTopY = srcTop0;

            if (canvas != null)
            {
                foreach (var cand in canvas.GetComponentsInChildren<RectTransform>(true))
                {
                    if (cand == null || cand == rt || cand == srcRt) continue;
                    if (!cand.gameObject.activeInHierarchy) continue;
                    var rr = WorldRect(cand);
                    if (rr.width < 8f || rr.height < 8f) continue;
                    if (rr.width > w * 3f || rr.height > h * 3f) continue;                      // 忽略大面板
                    if (Mathf.Abs((rr.xMin + rr.xMax) * 0.5f - srcCenterX) > w) continue;        // 只看同一列
                    // ★ 只认紧挨着的那几个（之前给 6h 的窗口，会把远处 HUD 元素也算进来 → 按钮被顶到很上面）
                    if (Mathf.Abs(rr.yMax - srcTop0) > h * 1.6f) continue;
                    taken.Add(rr);
                    if (rr.yMax > srcTopY) srcTopY = rr.yMax;
                }
            }

            const float Margin = 8f;                 // 视觉安全边距：按钮贴图可能比 rect 大
            Vector2 screen = new Vector2(srcCenterX + _offX, srcTopY + Gap + h * 0.5f + _offY);
            Rect myRect = Rect.MinMaxRect(screen.x - w * 0.5f, screen.y - h * 0.5f, screen.x + w * 0.5f, screen.y + h * 0.5f);

            for (int iter = 0; iter < 16; iter++)
            {
                if (myRect.yMax > Screen.height - 8f) break;                 // 别顶出屏幕
                bool hit = false;
                var test = Rect.MinMaxRect(myRect.xMin - Margin, myRect.yMin - Margin, myRect.xMax + Margin, myRect.yMax + Margin);
                foreach (var o in taken) if (test.Overlaps(o, true)) { hit = true; break; }
                if (!hit) break;
                screen.y += h * 0.5f + Gap + Margin;
                myRect = Rect.MinMaxRect(screen.x - w * 0.5f, screen.y - h * 0.5f, screen.x + w * 0.5f, screen.y + h * 0.5f);
            }

            // 无论如何都钳制在屏幕内 —— 之前算出屏幕外坐标就直接赋值，按钮就"消失"了
            screen.y = Mathf.Clamp(screen.y, h * 0.5f + 10f, Screen.height - h * 0.5f - 10f);
            screen.x = Mathf.Clamp(screen.x, w * 0.5f + 10f, Screen.width - w * 0.5f - 10f);

            Vector2 local = screen;
            if (canvasRt != null)
                RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, screen, null, out local);
            rt.anchoredPosition = local;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.SetAsLastSibling();

            Plugin.Log.LogInfo($"[STEMFishDLC] HUD 按钮挂载: canvas={(canvasRt != null ? canvasRt.name : "null")}, " +
                               $"源父级={(srcButton.transform.parent != null ? srcButton.transform.parent.name : "null")}, 屏幕=({screen.x:0},{screen.y:0}), 尺寸=({w:0}x{h:0}), 遮挡候选={taken.Count}");
        }

        // ---- 点击：清空原版回调（否则会打开健康面板），改开我们的面板 ----
        var btn = go.GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityAction(DlcPanel.Toggle));
            btn.interactable = true;
        }

        ApplyIcon(pc, go);
        ApplyTooltip(go);

        _clone = go;
    }

    // 克隆体会继承原版按钮的 UITooltip（所以之前悬浮显示"医疗面板（R）"）→ 改成我们自己的文案
    private static void ApplyTooltip(GameObject go)
    {
        try
        {
            foreach (var tip in go.GetComponentsInChildren<UITooltip>(true))
            {
                tip.skipLocale = true;
                tip.tipName = UiText.T("hud.button.name", Lang.T("战术终端", "TACTICAL TERMINAL"));
                tip.tipDesc = UiText.T("hud.button.desc", Lang.T("打开 STEM Fish 战术终端（F8）", "Open the STEM Fish tactical terminal (F8)"));
                tip.localeName = "";
                tip.localeDesc = "";
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 悬浮提示改写失败: {e.Message}");
        }
    }

    // 屏幕/世界空间下的矩形（HUD 是 ScreenSpaceOverlay，世界坐标即屏幕坐标）
    private static Rect WorldRect(RectTransform rt)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
    }

    // 与任意已占用矩形重叠？（内缩 2px 容差，避免像素级贴边误判）
    private static bool OverlapsAny(RectTransform me, System.Collections.Generic.List<Rect> others)
    {
        var r = WorldRect(me);
        r.xMin += 2f; r.yMin += 2f; r.xMax -= 2f; r.yMax -= 2f;
        foreach (var o in others)
        {
            var oo = o;
            oo.xMin += 2f; oo.yMin += 2f; oo.xMax -= 2f; oo.yMax -= 2f;
            if (r.Overlaps(oo)) return true;
        }
        return false;
    }

    // 把克隆体上的图标换成我们的像素图标（保持原版的 hover/动画组件不动）
    private static void ApplyIcon(PlayerCamera pc, GameObject go)
    {
        Image target = null;
        foreach (var img in go.GetComponentsInChildren<Image>(true))
        {
            if (img.sprite == pc.woundButtonNormal || img.sprite == pc.woundButtonFlash || img.sprite == pc.woundButtonScreen)
            {
                target = img;
                break;
            }
        }
        if (target == null) target = go.GetComponentInChildren<Image>(true);
        if (target == null) return;

        var icon = GetIcon();
        if (icon == null) return;

        target.sprite = icon;
        target.preserveAspect = true;
        target.color = new Color(0.80f, 0.96f, 1f, 1f);   // 轻微青色调，和 STEM Fish 主色呼应
    }

    // ==========================================================================
    //  程序化生成 16x16 像素图标：终端窗口 + ">" 提示符
    //  （不依赖任何外部贴图，白色像素便于 Image.color 着色）
    // ==========================================================================
    private static Sprite GetIcon()
    {
        if (_icon != null) return _icon;

        // 只有内容的行（裁掉上下空白，保证 pivot 居中后视觉居中）
        string[] rows =
        {
            ".#############..",
            ".#...........#..",
            ".#..#........#..",
            ".#...#.......#..",
            ".#....#......#..",
            ".#...#.......#..",
            ".#..#........#..",
            ".#...........#..",
            ".#...........#..",
            ".#############..",
        };

        int w = rows[0].Length, h = rows.Length;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        var clear = new Color(0f, 0f, 0f, 0f);
        var solid = Color.white;
        for (int y = 0; y < h; y++)
        {
            string row = rows[y];
            for (int x = 0; x < w; x++)
            {
                bool on = x < row.Length && row[x] == '#';
                tex.SetPixel(x, h - 1 - y, on ? solid : clear);   // 图源是自上而下，纹理是自下而上
            }
        }
        tex.Apply();

        _icon = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 16f, 0, SpriteMeshType.FullRect);
        _icon.name = "stemfish_tactical_icon";
        return _icon;
    }

    // 场景切换/重开时清掉引用（按钮随 HUD 一起被销毁）
    public static void Reset() { _clone = null; }
}
