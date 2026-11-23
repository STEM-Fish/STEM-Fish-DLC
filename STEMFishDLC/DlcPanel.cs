using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace STEMFishDLC;

// ============================================================================
//  DlcPanel - 游戏内面板（uGUI + TextMeshPro，不用 IMGUI）
//  ---------------------------------------------------------------------------
//  设计目标：贴合游戏原生 UI（深色半透明底 + 像素感边框 + TMP 字体），
//  挂在游戏自己的 Canvas 之上，随游戏 UI 一起缩放。
//
//  交互对齐原版（反汇编 PlayerCamera.ToggleWoundView）：
//    打开时 PlayUISound(UISoundType.HealthPanelOn, 1) + 开场缩放/淡入动画
//    关闭时 PlayUISound(UISoundType.Close, 1)
//    （UISoundType: Click=0 MiniClick=1 Close=2 HealthPanelOn=3 InventoryOpen=4 …）
//
//  终端页（Tab0）用条状仪表显示"原版健康面板看不到的变化速率"：
//    负重/血容量/心跳失常/体温/出血/意识 的 每秒变化量 + 临界倒计时
// ============================================================================

public static class DlcPanel
{
    private static GameObject _root;
    private static GameObject _panel;
    private static CanvasGroup _group;
    private static TextMeshProUGUI _bodyText;
    private static readonly TextMeshProUGUI[] _tabTexts = new TextMeshProUGUI[3];
    private static readonly string[] TabNames = { "体征", "归档", "链路" };
    private static int _tab;
    private static bool _visible;

    // 终端页条状仪表
    private const int RowCount = 6;
    private static readonly BarRow[] _rows = new BarRow[RowCount];
    private static RectTransform _barContent;
    private static TextMeshProUGUI _barHeader, _barFooter;

    // 档案页（左：条目列表 / 右：正文阅读）
    private static RectTransform _codexRoot, _codexListContent, _codexReaderContent;
    private static ScrollRect _codexScroll;
    private static TextMeshProUGUI _codexReaderText, _codexHeader;
    private static string _codexSelected;

    // 链路页（设备自检：上行已断开，只剩本地缓存）
    private static RectTransform _linkRoot;
    private static TextMeshProUGUI _linkText;

    // 开场动画
    private static float _animT = 1f;
    private const float AnimDuration = 0.16f;

    // 配色（对齐游戏暗色 UI）
    private static readonly Color PanelBg = new Color(0.055f, 0.065f, 0.085f, 0.96f);
    private static readonly Color HeaderBg = new Color(0.09f, 0.11f, 0.14f, 0.98f);
    private static readonly Color AccentColor = new Color(0.20f, 0.78f, 0.72f, 1f);
    private static readonly Color DimText = new Color(0.72f, 0.78f, 0.80f, 1f);
    private static readonly Color BodyText = new Color(0.90f, 0.94f, 0.94f, 1f);
    private static readonly Color BarBgColor = new Color(0.13f, 0.16f, 0.19f, 1f);
    private static readonly Color OkColor = new Color(0.31f, 0.76f, 0.63f, 1f);
    private static readonly Color WarnColor = new Color(0.85f, 0.66f, 0.28f, 1f);
    private static readonly Color BadColor = new Color(0.82f, 0.31f, 0.31f, 1f);

    public static bool Visible => _visible;

    private static string M(string key, string zh, string en)
    {
        return UiText.T("dlc." + key, Lang.T(zh, en));
    }

    private static void RefreshTabNames()
    {
        TabNames[0] = M("tab.vitals", "体征", "VITALS");
        TabNames[1] = M("tab.archive", "归档", "ARCHIVE");
        TabNames[2] = M("tab.link", "链路", "LINK");
    }

    // 任意"覆盖式面板"打开中（原版健康面板 / 制作面板 / 我们的终端）→ HUD 该让位
    // 暂停（ESC 菜单）时 HUD/终端都该让位 —— 和健康面板、制作面板一致
    public static bool AnyOverlayOpen => _visible || GamePanelOpen() || Plugin.IsPaused;

    // 原版覆盖式面板是否打开（健康 / 制作）
    private static bool GamePanelOpen()
    {
        try
        {
            var pc = PlayerCamera.main;
            if (pc == null) return false;
            if (pc.woundView != null && pc.woundView.activeSelf) return true;
            if (pc.craftingPanel != null && pc.craftingPanel.activeSelf) return true;
        }
        catch { }
        return false;
    }

    // 关掉游戏原版的覆盖式面板（打开我们的终端前调用，实现"互斥"）
    public static void CloseGamePanels()
    {
        try
        {
            var pc = PlayerCamera.main;
            if (pc == null) return;
            if (pc.woundView != null && pc.woundView.activeSelf) pc.ToggleWoundView(false);
            if (pc.craftingPanel != null && pc.craftingPanel.activeSelf) pc.OpenCraftScreen();   // 该方法是 toggle
        }
        catch { }
    }

    // 供其它 UI 模块复用同一套字体获取逻辑
    public static TMP_FontAsset Font => GetFont();

    public static float Scale = 1.15f;      // 战术终端整体放大（面板居中，等比放大不跑位）

    public static void Init(Plugin plugin)
    {
        try
        {
            Scale = Mathf.Clamp(plugin.Config.Bind("HUD", "TerminalScale", 1.15f,
                "战术终端面板缩放（1 = 780×640 原尺寸；默认 1.15 兼顾可读性和留白）").Value, 0.7f, 1.6f);
        }
        catch { }
        RefreshTabNames();
        Plugin.Log.LogInfo($"[STEMFishDLC] 面板模块就绪（F8 / HUD 按钮开关，缩放 ×{Scale:0.00}）");
    }

    // ==========================================================================
    //  开关
    // ==========================================================================
    public static void Toggle()
    {
        if (Plugin.IsPaused && !_visible) return;   // 暂停中不新开终端
        if (_root == null) Build();

        _visible = !_visible;

        if (_visible)
        {
            CloseGamePanels();                       // 互斥：关掉健康/制作面板
            _panel.SetActive(true);
            _animT = 0f;                        // 触发开场动画
            PlayUiSound(SoundHealthPanelOn);
            Refresh();
        }
        else
        {
            PlayUiSound(SoundClose);
            _panel.SetActive(false);
        }

        try
        {
            Cursor.visible = _visible || Cursor.visible;
            Cursor.lockState = _visible ? CursorLockMode.None : Cursor.lockState;
        }
        catch { }
    }

    // 因为"按 ESC 暂停"而被临时藏起来（取消暂停后自动恢复）
    private static bool _hiddenByPause;

    public static void Update()
    {
        Vitals.Tick();                          // 速率采样（关闭面板时也采，打开即有数据）

        // 与健康面板/制作面板一致：ESC 暂停时终端必须隐藏，取消暂停后恢复
        if (Plugin.IsPaused)
        {
            if (_visible)
            {
                _hiddenByPause = true;
                _panel.SetActive(false);
            }
            return;
        }
        if (_hiddenByPause)
        {
            _hiddenByPause = false;
            if (_visible && _panel != null) _panel.SetActive(true);
        }

        // 互斥：健康/制作面板一打开，我们的终端自动关闭（与原版行为一致）
        if (_visible && GamePanelOpen())
        {
            _visible = false;
            if (_panel != null) _panel.SetActive(false);
            return;
        }

        if (!_visible) return;

        // 开场动画：缩放 + 淡入（用 unscaled，暂停时也能播）
        if (_animT < 1f)
        {
            _animT = Mathf.Min(1f, _animT + Time.unscaledDeltaTime / AnimDuration);
            float e = Mathf.SmoothStep(0f, 1f, _animT);
            if (_group != null) _group.alpha = e;
            if (_panel != null)
                _panel.transform.localScale = Vector3.Lerp(new Vector3(0.94f, 0.94f, 1f), Vector3.one, e) * Scale;
        }

        if (_tab == 0) UpdateBars();
        else Refresh();
    }

    // ==========================================================================
    //  构建 UI
    // ==========================================================================
    private static void Build()
    {
        _root = new GameObject("STEMFishDLC_UI");
        UnityEngine.Object.DontDestroyOnLoad(_root);

        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();

        // 面板主体
        var panelRt = NewRect("Panel", _root.transform);
        panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(780, 640);
        panelRt.anchoredPosition = Vector2.zero;
        panelRt.localScale = Vector3.one * Scale;
        _panel = panelRt.gameObject;
        AddImage(_panel, PanelBg);
        _group = _panel.AddComponent<CanvasGroup>();

        // 顶栏
        var headerRt = NewRect("Header", _panel.transform);
        headerRt.anchorMin = new Vector2(0f, 1f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(0, 52);
        headerRt.anchoredPosition = Vector2.zero;
        AddImage(headerRt.gameObject, HeaderBg);

        var title = NewText("Title", headerRt, M("title", "战术终端", "TACTICAL TERMINAL"), 26, AccentColor);
        var trt = title.rectTransform;
        trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(1, 1);
        trt.offsetMin = new Vector2(20, 0); trt.offsetMax = new Vector2(-70, 0);
        title.alignment = TextAlignmentOptions.Left;

        // 标题下分隔线
        var lineRt = NewRect("Line", _panel.transform);
        lineRt.anchorMin = new Vector2(0f, 1f); lineRt.anchorMax = new Vector2(1f, 1f);
        lineRt.pivot = new Vector2(0.5f, 1f);
        lineRt.sizeDelta = new Vector2(0, 2);
        lineRt.anchoredPosition = new Vector2(0, -52);
        AddImage(lineRt.gameObject, new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.55f));

        // Tab 行
        var tabRow = NewRect("Tabs", _panel.transform);
        tabRow.anchorMin = new Vector2(0f, 1f); tabRow.anchorMax = new Vector2(1f, 1f);
        tabRow.pivot = new Vector2(0.5f, 1f);
        tabRow.sizeDelta = new Vector2(-32, 40);
        tabRow.anchoredPosition = new Vector2(0, -62);
        var hlg = tabRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8; hlg.childForceExpandWidth = true; hlg.childForceExpandHeight = true;
        hlg.padding = new RectOffset(0, 0, 0, 0);

        for (int i = 0; i < TabNames.Length; i++)
        {
            int idx = i;
            var btnRt = NewRect("Tab" + i, tabRow);
            var img = AddImage(btnRt.gameObject, new Color(0.14f, 0.17f, 0.21f, 1f));
            var btn = btnRt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() =>
            {
                _tab = idx;
                PlayUiSound(SoundClick);
                Refresh();
            });
            var txt = NewText("Label", btnRt, TabNames[i], 20, BodyText);
            var lrt = txt.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            txt.alignment = TextAlignmentOptions.Center;
            _tabTexts[i] = txt;
        }

        // 内容区
        var contentRt = NewRect("Content", _panel.transform);
        contentRt.anchorMin = new Vector2(0f, 0f); contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.offsetMin = new Vector2(24, 52);
        contentRt.offsetMax = new Vector2(-24, -112);

        // 文本页（保留：没建好列表时的兜底显示）
        _bodyText = NewText("Body", contentRt, "", 19, BodyText);
        var brt = _bodyText.rectTransform;
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
        brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        _bodyText.alignment = TextAlignmentOptions.TopLeft;
        _bodyText.enableWordWrapping = true;
        _bodyText.lineSpacing = 10;

        // 档案页（左右分栏）
        _codexRoot = NewRect("Codex", contentRt);
        _codexRoot.anchorMin = Vector2.zero; _codexRoot.anchorMax = Vector2.one;
        _codexRoot.offsetMin = Vector2.zero; _codexRoot.offsetMax = Vector2.zero;
        BuildCodexPage();

        // 链路页（纯文本自检）
        _linkRoot = NewRect("Link", contentRt);
        _linkRoot.anchorMin = Vector2.zero; _linkRoot.anchorMax = Vector2.one;
        _linkRoot.offsetMin = Vector2.zero; _linkRoot.offsetMax = Vector2.zero;
        BuildLinkPage();

        // 条状仪表页（终端）
        _barContent = NewRect("Bars", contentRt);
        _barContent.anchorMin = Vector2.zero; _barContent.anchorMax = Vector2.one;
        _barContent.offsetMin = Vector2.zero; _barContent.offsetMax = Vector2.zero;
        BuildBars();

        // 底部提示
        var footRt = NewRect("Footer", _panel.transform);
        footRt.anchorMin = new Vector2(0f, 0f); footRt.anchorMax = new Vector2(1f, 0f);
        footRt.pivot = new Vector2(0.5f, 0f);
        footRt.sizeDelta = new Vector2(-32, 36);
        footRt.anchoredPosition = new Vector2(0, 10);
        var foot = NewText("Foot", footRt, M("foot", "点击左下角图标可收起 · 所有数据仅存于本机",
                "Click the lower-left icon to collapse · all data is stored locally"), 15, DimText);
        var frt = foot.rectTransform;
        frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;
        foot.alignment = TextAlignmentOptions.Left;

        // 关闭按钮
        var closeRt = NewRect("Close", headerRt);
        closeRt.anchorMin = new Vector2(1f, 0.5f); closeRt.anchorMax = new Vector2(1f, 0.5f);
        closeRt.pivot = new Vector2(1f, 0.5f);
        closeRt.sizeDelta = new Vector2(56, 32);
        closeRt.anchoredPosition = new Vector2(-10, 0);
        var closeImg = AddImage(closeRt.gameObject, new Color(0.35f, 0.16f, 0.16f, 1f));
        var closeBtn = closeRt.gameObject.AddComponent<Button>();
        closeBtn.targetGraphic = closeImg;
        closeBtn.onClick.AddListener(() => { if (_visible) Toggle(); });
        var closeTxt = NewText("X", closeRt, UiText.T("common.close", Lang.T("关闭", "Close")), 16, BodyText);
        var crt2 = closeTxt.rectTransform;
        crt2.anchorMin = Vector2.zero; crt2.anchorMax = Vector2.one;
        crt2.offsetMin = Vector2.zero; crt2.offsetMax = Vector2.zero;
        closeTxt.alignment = TextAlignmentOptions.Center;

        _panel.SetActive(false);
        Plugin.Log.LogInfo("[STEMFishDLC] 面板构建完成");
    }

    // ==========================================================================
    //  条状仪表：每行 = 标签 + 进度条 + 数值/速率
    // ==========================================================================
    private class BarRow
    {
        public TextMeshProUGUI label;
        public TextMeshProUGUI value;
        public Image fill;
        public RectTransform fillRt;
    }

    private static void BuildBars()
    {
        // 小标题
        _barHeader = NewText("BarHeader", _barContent, "", 18, DimText);
        var hrt = _barHeader.rectTransform;
        hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1);
        hrt.pivot = new Vector2(0.5f, 1f);
        hrt.sizeDelta = new Vector2(0, 26);
        hrt.anchoredPosition = new Vector2(0, -2);
        _barHeader.alignment = TextAlignmentOptions.Left;

        const float rowH = 52f;
        const float topPad = 34f;

        for (int i = 0; i < RowCount; i++)
        {
            float y = -(topPad + i * rowH);

            var rowRt = NewRect("Row" + i, _barContent);
            rowRt.anchorMin = new Vector2(0, 1); rowRt.anchorMax = new Vector2(1, 1);
            rowRt.pivot = new Vector2(0.5f, 1f);
            rowRt.sizeDelta = new Vector2(0, rowH - 6);
            rowRt.anchoredPosition = new Vector2(0, y);

            var row = new BarRow();

            // 标签
            row.label = NewText("Label", rowRt, "", 18, BodyText);
            var lrt = row.label.rectTransform;
            lrt.anchorMin = new Vector2(0, 1); lrt.anchorMax = new Vector2(0, 1);
            lrt.pivot = new Vector2(0, 1);
            lrt.sizeDelta = new Vector2(130, 24);
            lrt.anchoredPosition = new Vector2(0, 0);
            row.label.alignment = TextAlignmentOptions.Left;

            // 进度条背景
            var barBgRt = NewRect("BarBg", rowRt);
            barBgRt.anchorMin = new Vector2(0, 1); barBgRt.anchorMax = new Vector2(0, 1);
            barBgRt.pivot = new Vector2(0, 1);
            barBgRt.sizeDelta = new Vector2(330, 16);
            barBgRt.anchoredPosition = new Vector2(134, -4);
            AddImage(barBgRt.gameObject, BarBgColor);

            // 进度条填充（靠 anchorMax.x 控制百分比）
            var fillRt = NewRect("BarFill", barBgRt);
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = new Vector2(0.5f, 1f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.offsetMin = new Vector2(0, 0); fillRt.offsetMax = new Vector2(0, 0);
            row.fillRt = fillRt;
            row.fill = AddImage(fillRt.gameObject, OkColor);
            row.fill.raycastTarget = false;

            // 数值
            row.value = NewText("Value", rowRt, "", 17, BodyText);
            var vrt = row.value.rectTransform;
            vrt.anchorMin = new Vector2(0, 1); vrt.anchorMax = new Vector2(1, 1);
            vrt.pivot = new Vector2(0.5f, 1);
            vrt.sizeDelta = new Vector2(-474, 24);
            vrt.anchoredPosition = new Vector2(237, 0);
            row.value.alignment = TextAlignmentOptions.Left;

            _rows[i] = row;
        }

        _barFooter = NewText("BarFooter", _barContent, "", 16, DimText);
        var frt = _barFooter.rectTransform;
        frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0);
        frt.pivot = new Vector2(0.5f, 0f);
        frt.sizeDelta = new Vector2(0, 70);
        frt.anchoredPosition = new Vector2(0, 0);
        _barFooter.alignment = TextAlignmentOptions.BottomLeft;
        _barFooter.enableWordWrapping = true;
    }

    // 每帧刷新条状仪表
    private static void UpdateBars()
    {
        var body = PlayerCamera.main?.body;
        if (body == null)
        {
            if (_barHeader != null) _barHeader.text = M("vitals.waiting", "<b>体征采样</b>   <color=#5a6672>等待信号</color>",
                "<b>VITALS</b>   <color=#5a6672>WAITING FOR SIGNAL</color>");
            if (_barFooter != null) _barFooter.text = M("vitals.nobody", "<color=#5a6672>没有接到身体。终端在等。</color>",
                "<color=#5a6672>No body connected. The terminal is waiting.</color>");
            return;
        }

        try
        {
            // 0) 负重（越高越糟）
            float encMax = body.maxEncumberance > 0.01f ? body.maxEncumberance : 1f;
            float encPct = body.totalEncumberance / encMax;
            SetRow(0, M("row.encumbrance", "负重", "LOAD"), encPct,
                $"{body.totalEncumberance:0.#}/{encMax:0.#}u   {Vitals.FormatRate(Vitals.EncumberanceRate, "u")}",
                HighBad(encPct, 0.7f, 0.95f));

            // 1) 血容量（越低越糟）+ 临界倒计时
            float eta = Vitals.BloodCriticalEta();
            string etaTxt = eta < 0f ? "" : $"   <color=#d05a5a>" + M("vitals.critical", "临界", "CRITICAL") + $" {Vitals.FormatEta(eta)}</color>";
            float bvPct = body.bloodVolume / 100f;
            SetRow(1, M("row.blood", "血容量", "BLOOD"), bvPct,
                $"{body.bloodVolume:0.0}/100   {Vitals.FormatRate(Vitals.BloodVolumeRate, "")}{etaTxt}",
                LowBad(bvPct, 0.55f, 0.35f));

            // 2) 心跳失常（越高越糟）
            string fibState = body.fibrillationRising ? "  <color=#d05a5a>" + M("vitals.worsening", "恶化中", "WORSENING") + "</color>" : "";
            float fibPct = body.fibrillationProgress / Vitals.FibrillationCritical;
            SetRow(2, M("row.fibrillation", "心跳失常", "ARRHYTHMIA"), fibPct,
                $"{body.fibrillationProgress:0.00}/1.00   {Vitals.FormatRate(Vitals.FibrillationRate, "/s")}{fibState}",
                HighBad(fibPct, 0.4f, 0.75f));

            // 3) 体温（偏离 37 越远越糟）
            float tp = Mathf.InverseLerp(28f, 42f, body.temperature);
            SetRow(3, M("row.temperature", "体温", "TEMP"), tp,
                $"{body.temperature:0.0}°C   偏差 {body.tempDiffFromNormal:+0.0;-0.0;0}°   {Vitals.FormatRate(Vitals.TemperatureRate, "°")}",
                TempColor(body.temperature));

            // 4) 出血速率（越高越糟）
            float bleedNorm = body.totalBleedSpeed / (Vitals.BleedCritical * 1.5f);
            SetRow(4, M("row.bleeding", "出血速率", "BLEED RATE"), bleedNorm,
                $"{body.totalBleedSpeed:0.000}   凝血 {Safe(() => body.bleedClottingSpeed, 0f):0.00}   内出血 {body.internalBleeding:0.0}",
                HighBad(bleedNorm, 0.6f, 0.9f));

            // 5) 意识（越低越糟；RiseRate/FallRate 是 static 字段）
            float consPct = body.consciousness / 100f;
            SetRow(5, M("row.consciousness", "意识", "CONSCIOUSNESS"), consPct,
                $"{body.consciousness:0.0}/100   回升系数 {Body.consciousnessRiseRate:0.00} / 下降系数 {Body.consciousnessFallRate:0.00}   Δ {Vitals.FormatRate(Vitals.ConsciousnessRate, "")}",
                LowBad(consPct, 0.6f, 0.3f));

            _barHeader.text = M("vitals.rate", "<b>体征采样</b>   <color=#5a6672>变化速率由本机自行推算</color>",
                "<b>VITALS</b>   <color=#5a6672>rates calculated locally</color>");

            int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0;
            var skills = body.GetComponent<Skills>();
            string skillTxt = skills != null ? $"STR {skills.STR} · RES {skills.RES} · INT {skills.INT}" : "";
            _barFooter.text = string.Format(M("vitals.depth", "<color=#5a6672>当前深度 {0} 层    {1}    采样间隔 0.2 秒</color>",
                "<color=#5a6672>Depth {0}    {1}    sample interval 0.2 s</color>"), depth, skillTxt);
        }
        catch (Exception e)
        {
            if (_barFooter != null) _barFooter.text = "<color=#d05a5a>读取失败: " + e.Message + "</color>";
        }
    }

    // ---- 颜色规则（明确区分"越高越糟"和"越低越糟"，避免上次血容量误标红） ----
    private static Color HighBad(float pct, float warnAt, float badAt)
        => pct >= badAt ? BadColor : (pct >= warnAt ? WarnColor : OkColor);

    private static Color LowBad(float pct, float warnAt, float badAt)
        => pct <= badAt ? BadColor : (pct <= warnAt ? WarnColor : OkColor);

    private static Color TempColor(float t)
    {
        if (t <= Vitals.TempLowCritical + 1.5f || t >= Vitals.TempHighCritical - 0.5f) return BadColor;
        if (t <= 34.5f || t >= 39f) return WarnColor;
        return OkColor;
    }

    // 设置一行：label / 百分比 / 颜色 / 数值文本
    private static void SetRow(int i, string label, float pct, string valueText, Color color)
    {
        var row = _rows[i];
        if (row == null) return;

        pct = Mathf.Clamp01(pct);
        row.label.text = label;
        row.value.text = valueText;

        if (row.fillRt != null)
            row.fillRt.anchorMax = new Vector2(pct, 1f);

        if (row.fill != null) row.fill.color = color;
    }

    private static float Safe(Func<float> f, float def)
    {
        try { return f(); } catch { return def; }
    }

    // ==========================================================================
    //  页切换
    // ==========================================================================
    private static void Refresh()
    {
        try
        {
            for (int i = 0; i < _tabTexts.Length; i++)
            {
                if (_tabTexts[i] == null) continue;
                _tabTexts[i].color = (i == _tab) ? AccentColor : BodyText;
                var parentImg = _tabTexts[i].transform.parent.GetComponent<Image>();
                if (parentImg != null)
                    parentImg.color = (i == _tab) ? new Color(0.13f, 0.26f, 0.26f, 1f) : new Color(0.14f, 0.17f, 0.21f, 1f);
            }

            if (_barContent != null) _barContent.gameObject.SetActive(_tab == 0);
            if (_codexRoot != null) _codexRoot.gameObject.SetActive(_tab == 1);
            if (_linkRoot != null) _linkRoot.gameObject.SetActive(_tab == 2);
            if (_bodyText != null) _bodyText.gameObject.SetActive(false);   // 三个页都有专属视图

            if (_tab == 0) UpdateBars();
            else if (_tab == 1) TickCodex();       // ★ 不重建列表，只在解锁/层数变化时刷新
            else RefreshLink();
        }
        catch (Exception e)
        {
            if (_bodyText != null)
            {
                _bodyText.gameObject.SetActive(true);
                _bodyText.text = M("vitals.readfail", "读取数据失败: ", "DATA READ FAILED: ") + e.Message;
            }
        }
    }

    // ==========================================================================
    //  滚动视图工厂（ScrollRect + Viewport + 竖排 LayoutGroup）
    //  ---------------------------------------------------------------------------
    //  content 上挂 VerticalLayoutGroup + ContentSizeFitter → 加多少行都自动排。
    //  Viewport 必须有一张（近乎透明的）Image，否则 ScrollRect 收不到拖拽事件。
    // ==========================================================================
    private static void AddScroll(Transform parent, string name, out RectTransform content)
    {
        var rootRt = NewRect(name, parent);
        rootRt.anchorMin = Vector2.zero; rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = new Vector2(4, 4); rootRt.offsetMax = new Vector2(-4, -4);

        var scroll = rootRt.gameObject.AddComponent<ScrollRect>();

        var viewport = NewRect("Viewport", rootRt);
        viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero; viewport.offsetMax = Vector2.zero;
        viewport.gameObject.AddComponent<RectMask2D>();
        var vpImg = viewport.gameObject.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0.004f);

        content = NewRect("Content", viewport);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0, 0);
        content.anchoredPosition = Vector2.zero;

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.spacing = 4;
        vlg.padding = new RectOffset(2, 4, 2, 4);

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 26f;
        scroll.inertia = true;
    }

    // 一行可点击的条目（列表项 / 设置项共用）
    private static Button AddRow(Transform parent, string name, float height, Color bg, out TextMeshProUGUI label, out TextMeshProUGUI value)
    {
        var rt = NewRect(name, parent);
        rt.sizeDelta = new Vector2(0, height);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight = height;

        var img = AddImage(rt.gameObject, bg);
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        label = NewText("Label", rt, "", 17, BodyText);
        var lrt = label.rectTransform;
        lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 1);
        lrt.offsetMin = new Vector2(10, 0); lrt.offsetMax = new Vector2(-96, 0);
        label.alignment = TextAlignmentOptions.Left;

        value = NewText("Value", rt, "", 16, DimText);
        var vrt = value.rectTransform;
        vrt.anchorMin = new Vector2(1, 0); vrt.anchorMax = new Vector2(1, 1);
        vrt.pivot = new Vector2(1, 0.5f);
        vrt.sizeDelta = new Vector2(88, 0);
        vrt.anchoredPosition = new Vector2(-10, 0);
        value.alignment = TextAlignmentOptions.Right;
        return btn;
    }

    // ==========================================================================
    //  档案页
    // ==========================================================================
    private static void BuildCodexPage()
    {
        // 顶部：标题 + 收集进度
        _codexHeader = NewText("CodexHeader", _codexRoot, "", 17, DimText);
        var hrt = _codexHeader.rectTransform;
        hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1);
        hrt.pivot = new Vector2(0.5f, 1f);
        hrt.sizeDelta = new Vector2(0, 26);
        hrt.anchoredPosition = Vector2.zero;
        _codexHeader.alignment = TextAlignmentOptions.Left;

        const float top = 30f;

        // 左：条目列表
        var listBg = NewRect("ListBg", _codexRoot);
        listBg.anchorMin = new Vector2(0, 0); listBg.anchorMax = new Vector2(0, 1);
        listBg.pivot = new Vector2(0, 0.5f);
        listBg.sizeDelta = new Vector2(256, -top);
        listBg.anchoredPosition = new Vector2(0, -top * 0.5f);
        AddImage(listBg.gameObject, new Color(0.08f, 0.10f, 0.13f, 1f));
        AddScroll(listBg, "ListScroll", out _codexListContent);

        // 右：正文阅读区
        var readBg = NewRect("ReadBg", _codexRoot);
        readBg.anchorMin = new Vector2(0, 0); readBg.anchorMax = new Vector2(1, 1);
        readBg.offsetMin = new Vector2(266, 0);
        readBg.offsetMax = new Vector2(0, -top);
        AddImage(readBg.gameObject, new Color(0.07f, 0.085f, 0.105f, 1f));

        var svRt = NewRect("ReadScroll", readBg);
        svRt.anchorMin = Vector2.zero; svRt.anchorMax = Vector2.one;
        svRt.offsetMin = new Vector2(10, 8); svRt.offsetMax = new Vector2(-10, -8);
        var scroll = svRt.gameObject.AddComponent<ScrollRect>();

        var viewport = NewRect("Viewport", svRt);
        viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero; viewport.offsetMax = Vector2.zero;
        viewport.gameObject.AddComponent<RectMask2D>();
        var vpImg = viewport.gameObject.AddComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0.004f);

        _codexReaderContent = NewRect("Content", viewport);
        _codexReaderContent.anchorMin = new Vector2(0, 1); _codexReaderContent.anchorMax = new Vector2(1, 1);
        _codexReaderContent.pivot = new Vector2(0.5f, 1f);
        _codexReaderContent.sizeDelta = Vector2.zero;
        _codexReaderContent.anchoredPosition = Vector2.zero;

        // 竖排 LayoutGroup：让 TMP 的 preferredHeight 真正驱动内容高度（只有 Fitter 不够）
        var rvlg = _codexReaderContent.gameObject.AddComponent<VerticalLayoutGroup>();
        rvlg.childForceExpandWidth = true;
        rvlg.childForceExpandHeight = false;
        rvlg.childControlWidth = true;
        rvlg.childControlHeight = true;
        rvlg.padding = new RectOffset(0, 0, 0, 0);

        _codexReaderText = NewText("ReaderText", _codexReaderContent, "", 18, BodyText);
        _codexReaderText.alignment = TextAlignmentOptions.TopLeft;
        _codexReaderText.enableWordWrapping = true;
        _codexReaderText.lineSpacing = 16;
        var rtrt = _codexReaderText.rectTransform;
        rtrt.anchorMin = new Vector2(0, 1); rtrt.anchorMax = new Vector2(1, 1);
        rtrt.pivot = new Vector2(0.5f, 1f);
        rtrt.sizeDelta = Vector2.zero;
        rtrt.anchoredPosition = Vector2.zero;

        var fitter = _codexReaderContent.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = viewport;
        scroll.content = _codexReaderContent;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        _codexScroll = scroll;      // 切条目时要把滚动位置复位到顶部
        scroll.scrollSensitivity = 26f;
    }

    // ==========================================================================
    //  归档页
    //  ---------------------------------------------------------------------------
    //  ★ 这里有个必须记住的坑：列表**不能每帧重建**。
    //    之前 Update 里 `if (_tab == 1) RefreshCodex()` 每帧把整列按钮
    //    SetParent(null) + Destroy 再重建，于是 PointerDown 和 PointerUp
    //    落在两个不同的 GameObject 上，UGUI 的 Button 判定不成一次点击
    //    → onClick 永远不触发，表现就是"除了第一项，其他都点不动"。
    //    现在只在「解锁数量变化 / 所在层变化」时重建，点击只换高亮 + 正文。
    // ==========================================================================
    private static int _codexDirty = 1;          // 1 = 需要重建列表
    private static int _codexDepth = -999;
    private static int _codexReadVer = -1;
    private static readonly List<Button> _codexRows = new List<Button>();
    private static readonly List<TextMeshProUGUI> _codexRowTexts = new List<TextMeshProUGUI>();

    private static readonly Color RowBgIdle = new Color(0.10f, 0.125f, 0.155f, 1f);
    private static readonly Color RowBgSel = new Color(0.13f, 0.26f, 0.26f, 1f);

    public static void TickCodex()
    {
        int readVer = LoreData.ReadCount;
        int depth = 0;
        try { if (WorldGeneration.world != null) depth = WorldGeneration.world.biomeDepth; } catch { }

        if (_codexDirty != 0 || readVer != _codexReadVer || depth != _codexDepth)
        {
            _codexReadVer = readVer;
            _codexDepth = depth;
            _codexDirty = 0;
            RefreshCodex();
        }
    }

    private static void RefreshCodex()
    {
        if (_codexListContent == null) return;

        // 清空旧行（先解除父子关系，避免销毁延迟导致本帧重复）
        for (int i = _codexListContent.childCount - 1; i >= 0; i--)
        {
            var c = _codexListContent.GetChild(i);
            c.SetParent(null, false);
            UnityEngine.Object.Destroy(c.gameObject);
        }
        _codexRows.Clear();
        _codexRowTexts.Clear();

        var entries = LoreData.Entries;
        if (_codexHeader != null)
            _codexHeader.text = entries.Count == 0
                ? M("archive.empty", "<b>扫描归档</b>   <color=#d05a5a>本机存储为空</color>",
                    "<b>ARCHIVE</b>   <color=#d05a5a>LOCAL STORAGE EMPTY</color>")
                : string.Format(M("archive.next", "<b>扫描归档</b>   {0}/{1}   <color=#5a6672>下一层会自动补上一条</color>",
                    "<b>ARCHIVE</b>   {0}/{1}   <color=#5a6672>the next entry will be added on the next layer</color>"),
                    LoreData.ReadCount, LoreData.Total);

        // 默认选中：上次看的 → 第一篇已读的 → 第一篇
        int sel = IndexOfCodex(_codexSelected);
        if (sel < 0)
        {
            for (int i = 0; i < entries.Count; i++)
                if (LoreData.IsRead(entries[i].id)) { sel = i; break; }
        }
        if (sel < 0 && entries.Count > 0) sel = 0;
        if (sel >= 0) _codexSelected = entries[sel].id;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            bool read = LoreData.IsRead(e.id);
            bool selected = e.id == _codexSelected;
            int idx = i;

            TextMeshProUGUI label, value;
            var btn = AddRow(_codexListContent, "Row" + i, 40f, selected ? RowBgSel : RowBgIdle,
                             out label, out value);

            label.text = read
                ? $"{i + 1:00}  {(string.IsNullOrEmpty(e.kind) ? "" : $"[{e.kind}] ")}{e.title}"
                : $"{i + 1:00}  <color=#5a6672>" + M("archive.locked", "未解锁", "LOCKED") + "</color>";
            label.color = read ? (selected ? AccentColor : BodyText) : DimText;
            label.fontSize = 15;
            // ★ 行高是固定的 40px：标题一长就会自动换行，第二行直接压到下一行条目上。
            //   列表里不换行、超出用省略号，行与行就永远不会叠。
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            value.text = "";
            value.rectTransform.sizeDelta = new Vector2(0, 0);

            btn.onClick.AddListener(() => SelectCodex(idx));

            _codexRows.Add(btn);
            _codexRowTexts.Add(label);
        }

        // 让布局本帧就位（否则 ContentSizeFitter 下一帧才算高度，按钮矩形是 0）
        try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_codexListContent); } catch { }

        ShowCodexEntry();
    }

    private static int IndexOfCodex(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        var entries = LoreData.Entries;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].id == id) return i;
        return -1;
    }

    // 点击：只换高亮 + 正文，**不重建列表**
    private static void SelectCodex(int idx)
    {
        var entries = LoreData.Entries;
        if (idx < 0 || idx >= entries.Count) return;

        _codexSelected = entries[idx].id;
        PlayUiSound(SoundMiniClick);

        for (int i = 0; i < _codexRows.Count && i < entries.Count; i++)
        {
            var b = _codexRows[i];
            if (b == null) continue;
            var e = entries[i];
            bool read = LoreData.IsRead(e.id);
            bool selected = e.id == _codexSelected;

            var img = b.GetComponent<UnityEngine.UI.Image>();
            if (img != null) img.color = selected ? RowBgSel : RowBgIdle;

            var t = _codexRowTexts[i];
            if (t != null) t.color = read ? (selected ? AccentColor : BodyText) : DimText;
        }

        ShowCodexEntry();
    }

    private static void ShowCodexEntry()
    {
        ClearCodexImages();
        if (_codexReaderText == null) return;
        var entry = LoreData.Get(_codexSelected);
        if (entry == null)
        {
            _codexReaderText.text = M("archive.empty2", "<color=#5a6672>本机存储为空。</color>",
                "<color=#5a6672>Local storage is empty.</color>");
            ResetCodexScroll();
            return;
        }

        if (!LoreData.IsRead(entry.id))
        {
            _codexReaderText.text =
                M("archive.locked.title", "<size=22><color=#5a6672>■■■ 未解锁 ■■■</color></size>",
                    "<size=22><color=#5a6672>■■■ LOCKED ■■■</color></size>") + "\n\n" +
                M("archive.locked.body", "<color=#5a6672>这条只有编号，没有内容。\n往下走，下一层的时候它会自己补上。\n（停在这一层不会推进。）</color>",
                    "<color=#5a6672>This entry has only a number, no content.\nGo deeper; it will fill itself in on the next layer.\n(Staying on this layer will not advance it.)</color>");
            ResetCodexScroll();
            return;
        }

        var sb = new System.Text.StringBuilder();
        // ★ 标题行必须留足行高：TMP 的 <size> 只放大字形，**不改行高**，
        //   大字号挤在小字号的行里就会压到下一行（标题一换行就叠成一团）。
        //   所以标题外面套一层 <line-height>，换行时也按 20 号字撑开。
        if (!string.IsNullOrEmpty(entry.kind))
            sb.Append("<size=14><color=#33c6b8>[").Append(entry.kind).Append("]</color></size>\n\n");
        sb.Append("<line-height=150%><size=20><color=#e6efef>").Append(entry.title)
          .Append("</color></size></line-height>\n\n");
        if (!string.IsNullOrEmpty(entry.subtitle))
            sb.Append("<size=14><color=#5a6672>").Append(entry.subtitle).Append("</color></size>\n\n");
        // 正文整体丢进 noparse：正文里可能有 < > 之类会被 TMP 当成标签
        sb.Append("<size=17><color=#c8d4d4><noparse>").Append(LoreData.BodyFor(entry)).Append("</noparse></color></size>");

        // 主角批注：和原文并列，靠一条竖线分隔（永远是"另一个人的声音"）
        // ★ 后补的部分（noteLater）要下到对应层才出现，避免第 1 层就看到"第 3 层补记"
        string note = LoreData.NoteFor(entry);
        if (!string.IsNullOrEmpty(note))
        {
            sb.Append("\n\n<size=14><color=#4a5560>────────────────────────</color></size>\n\n");
            sb.Append("<size=17><color=#8fd6c4><noparse>").Append(note).Append("</noparse></color></size>");
        }
        _codexReaderText.text = sb.ToString();

        BuildCodexImages(entry);
        ResetCodexScroll();
    }

    // --------------------------------------------------------------------------
    //  把阅读区滚回顶部
    //  ---------------------------------------------------------------------------
    //  ScrollRect 的滚动位置是持久的：上一篇看到一半切到下一篇，新条目会从
    //  中间开始显示（内容短的时候甚至直接停在底部）。每次换条目都复位一次。
    //  必须先让 ContentSizeFitter 把高度算出来，否则归一化位置是按旧高度算的。
    // --------------------------------------------------------------------------
    private static void ResetCodexScroll()
    {
        if (_codexScroll == null || _codexReaderContent == null) return;
        try
        {
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_codexReaderContent);
            _codexScroll.verticalNormalizedPosition = 1f;
        }
        catch { }
    }

    // --------------------------------------------------------------------------
    //  配图（lore_img/<名字>.png）
    //  ---------------------------------------------------------------------------
    //  ★ 为什么不塞进 TMP 文本：游戏的 ScrollableText 根本不读 sprites 字段
    //    （反汇编确认），TMP 内联 <sprite> 又要现造 TMP_SpriteAsset，脆。
    //    这里直接把 Image 挂在正文下面的 VerticalLayoutGroup 里 —— 位置、
    //    尺寸都归我们管，图片一定显示得出来。
    // --------------------------------------------------------------------------
    private static readonly List<GameObject> _codexImgs = new List<GameObject>();

    private static void ClearCodexImages()
    {
        for (int i = 0; i < _codexImgs.Count; i++)
        {
            var go = _codexImgs[i];
            if (go == null) continue;
            go.transform.SetParent(null, false);
            UnityEngine.Object.Destroy(go);
        }
        _codexImgs.Clear();
    }

    private static void BuildCodexImages(LoreData.Entry entry)
    {
        if (_codexReaderContent == null) return;
        List<Sprite> sprites;
        try { sprites = LoreData.LoadSprites(entry); } catch { return; }
        if (sprites == null || sprites.Count == 0) return;

        // 内容宽度：布局还没算过的话给个保守值，下一次选中就准了
        float w = 0f;
        try
        {
            var holder = _codexReaderContent.parent as RectTransform;
            if (holder != null) w = holder.rect.width;
        }
        catch { }
        if (w <= 8f) w = 600f;

        for (int i = 0; i < sprites.Count; i++)
        {
            var sp = sprites[i];
            if (sp == null) continue;

            float aspect = sp.rect.height / Mathf.Max(1f, sp.rect.width);
            float h = Mathf.Min(w * aspect, 460f);      // 单张最高 460，别把正文挤没

            var rt = NewRect("LoreImg" + i, _codexReaderContent);
            rt.sizeDelta = new Vector2(0, h);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = h;
            le.minHeight = h;
            le.flexibleHeight = 0f;

            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sp;
            img.preserveAspect = true;                  // 宽高比不动，多余的地方留空
            img.raycastTarget = false;
            _codexImgs.Add(rt.gameObject);
        }

        try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_codexReaderContent); } catch { }
    }

    // ==========================================================================
    //  链路页（设备自检）
    //  ---------------------------------------------------------------------------
    //  剧情前提：终端和颅内芯片是**一套**——芯片采，终端显，中间一根皮下数据线。
    //  真正的发送端在芯片里（背景辐射自充电，拆不掉），终端只是一块屏。
    //  所以"上行"始终是通的，公司一直在收遥测；主角关不掉，只能看着。
    //  这一页只放主角自己能读到的东西：链路状态、最后一次来自上面的指令、
    //  以及偶尔出现的、格式不对的一行字（不解释）。
    // ==========================================================================
    private static void BuildLinkPage()
    {
        // ★ 必须可滚动：设备状态条目会随层数/状态变化，静态文本会溢出到面板外
        RectTransform content;
        AddScroll(_linkRoot, "LinkScroll", out content);

        _linkText = NewText("LinkText", content, "", 18, BodyText);
        var rt = _linkText.rectTransform;
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
        _linkText.alignment = TextAlignmentOptions.TopLeft;
        _linkText.enableWordWrapping = true;
        _linkText.lineSpacing = 12;
    }

    private static void RefreshLink()
    {
        if (_linkText == null) return;
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0;

            var sb = new System.Text.StringBuilder();

            // ★ 这一页只放"设备读数"，不放任何内心活动。
            //   主角的感想、吐槽、判断全部在「归档」页里（那是他的笔记本），
            //   链路页是他摊开手掌看的仪表——仪表不会抒情。
            sb.Append("<size=22><color=#33c6b8>" + M("tab.link", "链路", "LINK") + "</color></size>\n\n");
            sb.Append("<color=#5a6672>" + M("link.up", "上行", "UPLINK") + "</color>        <color=#d8a83e>" + M("link.chip", "芯片直连", "CHIP DIRECT") + "</color>\n");
            sb.Append("<color=#5a6672>" + M("link.bus", "本地总线", "LOCAL BUS") + "</color>    <color=#4fc3a1>" + M("link.normal", "正常", "NORMAL") + "</color>\n");
            sb.Append("<color=#5a6672>" + M("link.telemetry", "遥测回报", "TELEMETRY") + "</color>    <color=#d05a5a>" + M("link.continuous", "持续", "CONTINUOUS") + "</color>\n");
            sb.Append("<color=#5a6672>" + M("link.position", "定位", "POSITION") + "</color>        <color=#d8a83e>" + M("link.active", "活动", "ACTIVE") + "</color>\n");
            sb.Append($"<color=#5a6672>{M("link.cut", "本机切断", "LOCAL CUT")}</color>    <color=#d05a5a>{M("link.unavailable", "不可用", "UNAVAILABLE")}</color>\n");
            sb.Append($"<color=#5a6672>{M("link.power", "外设电源", "AUX POWER")}</color>    <color=#e6efef>{(BodyPowerWord())}</color>\n");

            sb.Append("\n<size=22><color=#33c6b8>" + M("link.timer", "作业计时", "WORK TIMER") + "</color></size>\n\n");
            sb.Append($"<color=#5a6672>{M("link.layer", "当前层", "CURRENT LAYER")}</color>      <color=#e6efef>{depth}</color>\n");
            float spent = 0f, limit = 0f;
            try
            {
                if (WorldGeneration.world != null)
                {
                    spent = WorldGeneration.world.layerTimeSpent;
                    limit = WorldGeneration.world.maxTimePerLayer;
                }
            }
            catch { }
            if (limit > 0f)
            {
                float left = Mathf.Max(0f, limit - spent);
                string col = left <= 0f ? "d05a5a" : (left < limit * 0.25f ? "d8a83e" : "8fd6c4");
                sb.Append($"<color=#5a6672>{M("link.spent", "本层已用", "TIME IN LAYER")}</color>    <color=#e6efef>{FmtTime(spent)}</color>\n");
                sb.Append($"<color=#5a6672>{M("link.left", "本层剩余", "TIME LEFT")}</color>    <color=#{col}>{FmtTime(left)}</color>\n");
            }
            else
            {
                sb.Append($"<color=#5a6672>本层已用</color>    <color=#e6efef>{FmtTime(spent)}</color>\n");
            }

            if (body != null)
            {
                sb.Append("\n<size=22><color=#33c6b8>" + M("link.chipunit", "颅内单元", "CRANIAL UNIT") + "</color></size>\n\n");
                sb.Append($"<color=#5a6672>{M("link.status", "状态", "STATUS")}</color>        <color=#4fc3a1>{M("link.online", "在线", "ONLINE")}</color>\n");
                sb.Append($"<color=#5a6672>{M("link.carrier", "载体", "CARRIER")}</color>        <color=#e6efef>{VitalsWord(body)}</color>\n");
                sb.Append($"<color=#5a6672>{M("link.slots", "扩展插槽", "EXPANSION SLOTS")}</color>    <color=#5a6672>{M("link.used", "0 / 2 已用", "0 / 2 USED")}</color>\n");
            }

            sb.Append("\n<size=22><color=#33c6b8>" + M("link.cache", "本地缓存", "LOCAL CACHE") + "</color></size>\n\n");
            sb.Append("<color=#5a6672>" + M("link.source", "来源    [损坏]", "SOURCE    [CORRUPTED]") + "</color>\n");
            sb.Append("<color=#c8d4d4>" + M("link.cache1", "「继续下潜。样本采集未达配额。」",
                "\"Continue descending. Sample quota not met.\"") + "</color>\n");
            sb.Append("<color=#5a6672>" + M("link.cache2", "「深度不达标。已记录。」",
                "\"Depth requirement not met. Logged.\"") + "</color>\n");
            sb.Append("<color=#5a6672>" + M("link.cache3", "「无需回复。回复通道不为此编号开放。」",
                "\"No reply required. Reply channel is not open to this number.\"") + "</color>\n");
            sb.Append("<color=#5a6672>" + M("link.cache4", "——以上三条之后再无新条目。",
                "—No further entries after these three.") + "</color>\n");

            sb.Append("\n<color=#4a5560>──────────────</color>\n");
            sb.Append("<color=#5a6672>" + M("link.signal", "未登记信号　1 条（无法解析，格式不属于本设备）",
                "Unregistered signals: 1 (unreadable; format does not belong to this device)") + "</color>\n");

            _linkText.text = sb.ToString();
        }
        catch (Exception e)
        {
            if (_linkText != null) _linkText.text = "<color=#d05a5a>" + M("link.selffail", "自检失败：", "SELF-CHECK FAILED: ") + e.Message + "</color>";
        }
    }

    private static string FmtTime(float sec)
    {
        if (sec < 0f) sec = 0f;
        int h = (int)(sec / 3600f);
        int m = (int)((sec - h * 3600f) / 60f);
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }

    private static string BodyPowerWord()
    {
        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return "——";
            return M("link.selfpowered", "自持", "SELF-POWERED");
        }
        catch { return "——"; }
    }

    // 一句人话的载体状态（读不到就说不知道）
    private static string VitalsWord(Body body)
    {
        try
        {
            if (body.consciousness < 25f) return M("state.confused", "意识模糊", "CONFUSED");
            if (body.bloodVolume < 55f) return M("state.bleeding", "失血", "BLEEDING");
            if (body.temperature < 34.5f) return M("state.hypothermia", "体温过低", "HYPOTHERMIA");
            if (body.temperature > 39f) return M("state.hyperthermia", "体温过高", "HYPERTHERMIA");
            if (body.totalBleedSpeed > 0.01f) return M("state.stillbleeding", "还在流", "STILL BLEEDING");
            if (body.totalEncumberance > body.maxEncumberance) return M("state.overloaded", "背得太多了", "OVERLOADED");
            return M("link.walking", "还能走", "STILL WALKING");
        }
        catch { return "——"; }
    }


    // ==========================================================================
    //  原版 UI 音效（UISoundType 是 PlayerCamera 的嵌套枚举，反射调用最稳）
    // ==========================================================================
    private const int SoundClick = 0;
    private const int SoundMiniClick = 1;
    private const int SoundClose = 2;
    private const int SoundHealthPanelOn = 3;

    private static Type _soundEnumType;
    private static MethodInfo _playEnum, _playStr;
    private static bool _soundLookupDone;

    private static void PlayUiSound(int type)
    {
        try
        {
            var pc = PlayerCamera.main;
            if (pc == null) return;

            if (!_soundLookupDone)
            {
                _soundLookupDone = true;
                _soundEnumType = AccessTools.Inner(typeof(PlayerCamera), "UISoundType");
                if (_soundEnumType != null)
                    _playEnum = AccessTools.Method(typeof(PlayerCamera), "PlayUISound", new[] { _soundEnumType, typeof(float) });
                if (_playEnum == null)
                    _playStr = AccessTools.Method(typeof(PlayerCamera), "PlayUISound", new[] { typeof(string), typeof(float) });
            }

            if (_playEnum != null && _soundEnumType != null)
            {
                _playEnum.Invoke(pc, new object[] { Enum.ToObject(_soundEnumType, type), 1f });
                return;
            }
            if (_playStr != null)
                _playStr.Invoke(pc, new object[] { "click", 1f });
        }
        catch { /* 音效失败不影响功能 */ }
    }

    // ==========================================================================
    //  小工具
    // ==========================================================================
    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go.GetComponent<RectTransform>();
    }

    private static Image AddImage(GameObject go, Color c)
    {
        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.color = c;
        img.raycastTarget = true;
        return img;
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, Color color)
    {
        var rt = NewRect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = GetFont();
        if (font != null) t.font = font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.raycastTarget = false;
        return t;
    }

    // 字体：优先 TMP 默认字体；游戏若没设默认字体，就从场景里已有 TMP 组件借一个
    private static TMP_FontAsset _font;
    private static TMP_FontAsset GetFont()
    {
        if (_font != null) return _font;
        try
        {
            if (TMP_Settings.defaultFontAsset != null)
                return _font = TMP_Settings.defaultFontAsset;
        }
        catch { }
        try
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                if (t != null && t.font != null)
                {
                    _font = t.font;
                    Plugin.Log.LogInfo($"[STEMFishDLC] 借用游戏 TMP 字体: {t.font.name}");
                    return _font;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 取字体失败: {e.Message}");
        }
        Plugin.Log.LogWarning("[STEMFishDLC] 没找到可用 TMP 字体，面板文字可能不显示");
        return null;
    }
}
