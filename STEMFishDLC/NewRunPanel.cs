using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  NewRunPanel - 进入游戏界面（左：新的一局 / 右：载入存档）
//  ---------------------------------------------------------------------------
//  这是一个**给玩家看的设置界面**，不是剧情设备，所以不写任何世界观文案。
//
//  实现方式：不删不改编原版 UI，在 PreRunScript.mainCanvas 下铺一层全屏面板盖住它。
//    · 背景 Image raycastTarget=true → 拦住所有穿透点击
//    · 每次显示 SetAsLastSibling（原版封面图 cover 会把兄弟节点提前）
//    · 显示条件 = _wants && runSettingsScreen.activeInHierarchy
//      _wants 只由 AdaptiveButton.Clicked(action==0) 置位 —— 不靠猜 prefab 的初始激活状态，
//      否则万一它是常驻的，这层全屏面板会把整个主菜单盖掉。
//    · 原版返回按钮被盖住，所以自己放一个「← 返回」
//
//  v0.18.0 起：
//    左栏 = 模式卡片，点一下 → 弹「给存档起名」→ 新建槽位 → 开局
//           （急速下坠不建存档，直接开）
//    右栏 = 存档列表，每行可 载入 / 改名 / 删除
//  存档本身由 SaveSlots 负责（见那边的注释：游戏只有单槽 save.sv，我们在外面包一层）
// ============================================================================

public static class NewRunPanel
{
    private static GameObject _root;
    private static CanvasGroup _group;
    private static float _fade;
    private static bool _busy;
    private static float _busyT;
    private static int _failBuild;
    private static readonly List<Button> _cards = new List<Button>();
    private static RectTransform _slotContent;
    private static RectTransform _stage;      // 1920×1080 设计舞台（等比缩放到父 Canvas）

    // ---------------- 配色 ----------------
    private static readonly Color BgCol = new Color(0.024f, 0.031f, 0.041f, 0.988f);
    private static readonly Color CardCol = new Color(0.071f, 0.090f, 0.114f, 1f);
    private static readonly Color RowCol = new Color(0.086f, 0.106f, 0.130f, 1f);
    private static readonly Color TextCol = new Color(0.87f, 0.92f, 0.94f, 1f);
    private static readonly Color DimCol = new Color(0.50f, 0.57f, 0.61f, 1f);
    private static readonly Color FaintCol = new Color(0.32f, 0.37f, 0.40f, 1f);
    private static readonly Color SkyCol = new Color(0.30f, 0.81f, 1.00f, 1f);
    private static readonly Color GoodCol = new Color(0.56f, 0.84f, 0.77f, 1f);
    private static readonly Color BadCol = new Color(0.85f, 0.42f, 0.40f, 1f);

    // 布局常量（相对屏幕中心；按 1920×1080 的参考分辨率排）
    private const float LeftX = -900f;        // 左栏左边缘
    private const float CardW = 820f;
    private const float CardH = 112f;
    private const float CardGap = 8f;
    private const float CardsTop = 350f;      // 第一张卡片顶边
    private const float RightX = 40f;         // 右栏左边缘
    private const float RightW = 860f;

    // ★ 只在「玩家点过开始新游戏」之后才显示。
    private static bool _wants;

    // ---------------- 弹窗状态 ----------------
    private static GameObject _dialog;
    private static TMP_InputField _dialogInput;
    private static RunConfig.Difficulty _pendingMode;   // 非空 = 新建存档
    private static string _pendingRename;               // 非空 = 重命名
    private static Action _pendingConfirm;              // 确认框回调

    public static void Init(Plugin plugin)
    {
        try
        {
            var m = AccessTools.Method(typeof(AdaptiveButton), "Clicked");
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 AdaptiveButton.Clicked，模式界面改为只按界面激活状态判断");
                return;
            }
            var post = typeof(NewRunPanel).GetMethod("Clicked_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.newrun").Patch(m, postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(m);
            Plugin.Log.LogInfo($"[STEMFishDLC] 模式界面入口 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 模式界面入口 hook 失败: {e.Message}");
        }
    }

    private static void Clicked_Postfix(AdaptiveButton __instance)
    {
        try
        {
            // 0 = 开始新游戏（原版这个分支里就是 runSettingsScreen.SetActive(true)）
            if (__instance != null && Convert.ToInt32(__instance.action) == 0) _wants = true;
        }
        catch { }
    }

    /// <summary>强制收掉模式面板 + 弹窗（弹窗是全屏 dim，留着会压暗并吞掉点击）。</summary>
    public static void ForceHide()
    {
        try { if (_root != null && _root.activeSelf) _root.SetActive(false); } catch { }
        try { if (_dialog != null) UnityEngine.Object.Destroy(_dialog); } catch { }
        _dialog = null;
        _dialogInput = null;
        _pendingMode = null;
        _pendingRename = null;
        _pendingConfirm = null;
        _busy = false;
        _wants = false;
    }

    public static void Tick()
    {
        if (!RunConfig.Enabled) return;

        var pre = PreRunScript.instance;
        if (pre == null) { _root = null; return; }

        var host = pre.mainCanvas != null ? pre.mainCanvas.transform : pre.transform;
        if (host == null) { _root = null; return; }

        var screen = pre.runSettingsScreen;
        bool active = screen != null && screen.activeInHierarchy;
        if (!active) _wants = false;         // 界面关掉（返回/开局）→ 下次要重新点按钮

        // 选角页打开时 / 选的是原版角色 → 不显示我们的界面
        // （原版角色走原版 runSettingsScreen：难度预设 + 原版存档）
        if (CharacterSelect.Current != CharacterSelect.Sf07)
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            // 弹窗（命名/确认）也要一起清：它是全屏 dim，留着就是"整屏变暗 + 点不动"
            if (_dialog != null) UnityEngine.Object.Destroy(_dialog);
            _dialog = null;
            _dialogInput = null;
            _pendingMode = null;
            _pendingRename = null;
            _pendingConfirm = null;
            _busy = false;
            return;
        }

        if (!(_wants && active))
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            _busy = false;
            if (_dialog != null) UnityEngine.Object.Destroy(_dialog);   // 面板留着，弹窗得扔掉
            _dialog = null;
            _dialogInput = null;
            _pendingMode = null;
            _pendingRename = null;
            _pendingConfirm = null;
            return;
        }

        if (_root == null || _root.transform.parent != host)
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_failBuild < 3) Build(pre, host);
            else if (_failBuild == 3)
            {
                _failBuild = 4;
                Plugin.Log.LogWarning("[STEMFishDLC] 模式界面构建连续失败，已停止重试（原版界面仍然可用）");
            }
        }
        if (_root == null) return;

        if (!_root.activeSelf)
        {
            _root.SetActive(true);
            _fade = 0f;
            _busy = false;
            _busyT = 0f;
            SetCards(true);
            RebuildSlots();
        }

        FitStage();      // 窗口/分辨率变化后保持等比缩放

        // 选好了但世界没起来（StartRun 抛异常/被拦）→ 别把玩家卡在灰按钮里
        if (_busy)
        {
            _busyT += Time.unscaledDeltaTime;
            if (_busyT > 5f)
            {
                _busy = false;
                SetCards(true);
            }
        }

        _fade = Mathf.MoveTowards(_fade, 1f, Time.unscaledDeltaTime * 8f);
        if (_group != null) _group.alpha = _fade;

        // 原版封面图 cover 会把兄弟节点提到前面，这里守住最上层
        var parent = _root.transform.parent;
        if (parent != null && _root.transform.GetSiblingIndex() != parent.childCount - 1)
            _root.transform.SetAsLastSibling();
    }

    // ==========================================================================
    //  舞台等比缩放：1920×1080 设计稿 → 父 Canvas 的实际逻辑尺寸
    // ==========================================================================
    private static void FitStage()
    {
        if (_stage == null || _root == null) return;
        try
        {
            var p = _root.transform.parent as RectTransform;
            if (p == null) return;
            float w = p.rect.width, h = p.rect.height;
            if (w < 1f || h < 1f) return;

            float k = Mathf.Min(w / 1920f, h / 1080f);
            if (float.IsNaN(k) || k <= 0.01f) k = 1f;
            var s = new Vector3(k, k, 1f);
            if ((_stage.localScale - s).sqrMagnitude > 0.000001f) _stage.localScale = s;
        }
        catch { }
    }

    // ==========================================================================
    //  构建
    // ==========================================================================
    private static void Build(PreRunScript pre, Transform host)
    {
        try
        {
            _cards.Clear();
            _slotContent = null;
            _dialog = null;
            _dialogInput = null;

            var go = new GameObject("STEMFishDLC_NewRun", typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(host, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();

            _root = go;
            _group = go.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            var bg = NewImage(rt, "Bg", BgCol);
            bg.raycastTarget = true;

            // ---- 舞台：所有内容按 1920×1080 设计，再整体等比缩放 ----
            //   ★ 笔记本 / 小屏 / 非 16:9 时，父 Canvas 的逻辑尺寸可能只有 1800 甚至更小，
            //     直接铺 1800 宽的绝对坐标会被裁掉两边。这里固定设计尺寸 + min(W/1920, H/1080)。
            var stageGo = new GameObject("Stage", typeof(RectTransform));
            var stageRt = stageGo.GetComponent<RectTransform>();
            stageRt.SetParent(rt, false);
            stageRt.anchorMin = stageRt.anchorMax = new Vector2(0.5f, 0.5f);
            stageRt.pivot = new Vector2(0.5f, 0.5f);
            stageRt.sizeDelta = new Vector2(1920f, 1080f);
            stageRt.anchoredPosition = Vector2.zero;
            stageRt.localScale = Vector3.one;
            _stage = stageRt;

            var ui = stageRt;

            // ---- 顶部 ----
            var title = MakeText(ui, "Title", UiText.Get("newrun", "title", "STEM-FISH DLC"), 36f, TextCol, TextAlignmentOptions.Left);
            PlaceL(title.rectTransform, LeftX, 492f, new Vector2(600f, 46f));

            var sub = MakeText(ui, "Sub", UiText.Get("newrun", "sub", "选择一局新游戏，或者继续之前的存档"), 15f, DimCol, TextAlignmentOptions.Left);
            PlaceL(sub.rectTransform, LeftX, 462f, new Vector2(900f, 24f));

            var line0 = NewImage(ui, "Line0", new Color(SkyCol.r, SkyCol.g, SkyCol.b, 0.22f));
            Place(line0.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 440f), new Vector2(1800f, 1f));

            // ---- 左栏标题 ----
            var lh = MakeText(ui, "LeftHead", UiText.Get("newrun", "leftHead", "新的一局"), 22f, TextCol, TextAlignmentOptions.Left);
            PlaceL(lh.rectTransform, LeftX, 404f, new Vector2(420f, 30f));

            var ld = MakeText(ui, "LeftDesc", UiText.Get("newrun", "leftDesc", "选一个模式，给存档起个名字，立刻出发。急速下坠不建存档。"),
                              12f, FaintCol, TextAlignmentOptions.Left);
            PlaceL(ld.rectTransform, LeftX, 378f, new Vector2(CardW, 20f));

            // ---- 模式卡片 ----
            for (int i = 0; i < RunConfig.All.Length; i++)
            {
                float y = CardsTop - CardH * 0.5f - i * (CardH + CardGap);
                BuildCard(ui, RunConfig.All[i], y);
            }

            float footY = CardsTop - RunConfig.All.Length * (CardH + CardGap) - 8f;
            var foot = MakeText(ui, "Foot",
                string.Format(UiText.T("newrun.statline",
                    Lang.T("所有模式的开局装备相同，初始技能固定为 力量 {0} / 耐力 {1} / 智力 {2}。      负重上限 ×{3}",
                           "All modes share the same starter kit. Starting stats: STR {0} / RES {1} / INT {2}.      Carry cap x{3}")),
                    PlayerSkills.STR, PlayerSkills.RES, PlayerSkills.INT, RunConfig.EncumbranceCap),
                12f, FaintCol, TextAlignmentOptions.Left);
            PlaceL(foot.rectTransform, LeftX, footY, new Vector2(CardW, 20f));

            // ---- 左下：存档说明（顺便把左栏下半截填满）----
            var box = NewImage(ui, "SaveHint", new Color(0.043f, 0.055f, 0.068f, 1f));
            PlaceL(box.rectTransform, LeftX, -378f, new Vector2(CardW, 156f));

            var bh = MakeText(box.transform, "H", UiText.Get("newrun", "saveHintTitle", "存档"), 15f, SkyCol, TextAlignmentOptions.TopLeft);
            Place(bh.rectTransform, new Vector2(0f, 1f), new Vector2(16f, -10f), new Vector2(200f, 20f));

            var b1 = MakeText(box.transform, "B",
                UiText.Get("newrun", "saveHintBody",
                    "· 每个模式开局都会新建一个命名存档（急速下坠除外）\n" +
                    $"· 到层底存档点自动保存；<color=#8fd6c4>{SaveSlots.SaveKey}</color> 随时保存\n" +
                    "· 右侧列表可以载入 / 改名 / 删除"),
                12f, DimCol, TextAlignmentOptions.TopLeft);
            b1.enableWordWrapping = true;
            Place(b1.rectTransform, new Vector2(0f, 1f), new Vector2(16f, -34f), new Vector2(CardW - 32f, 90f));

            var bp = MakeText(box.transform, "P",
                string.Format(UiText.T("newrun.position", Lang.T("位置：{0}", "Location: {0}")), SaveSlots.Root),
                10f, FaintCol, TextAlignmentOptions.TopLeft);
            Place(bp.rectTransform, new Vector2(0f, 0f), new Vector2(16f, 8f), new Vector2(CardW - 32f, 14f));

            BuildBackButton(ui, pre, -494f);

            // ---- 右栏：存档 ----
            BuildSlotsColumn(ui);

            FitStage();
            Plugin.Log.LogInfo($"[STEMFishDLC] 进入游戏界面已构建（{RunConfig.All.Length} 个模式 · 舞台 1920×1080 · " +
                               $"父画布 {rt.rect.width:0}×{rt.rect.height:0} · 缩放 {_stage.localScale.x:0.000}）");
            _failBuild = 0;
        }
        catch (Exception e)
        {
            _failBuild++;
            Plugin.Log.LogWarning($"[STEMFishDLC] 构建进入游戏界面失败({_failBuild}): {e.Message}\n{e.StackTrace}");
            _root = null;
        }
    }

    private static void BuildCard(RectTransform root, RunConfig.Difficulty d, float y)
    {
        var go = new GameObject("Card_" + d.preset + "_" + d.code, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(root, false);
        PlaceL(rt, LeftX, y, new Vector2(CardW, CardH));

        var img = go.GetComponent<Image>();
        img.color = CardCol;
        img.raycastTarget = true;

        var btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        btn.transition = Selectable.Transition.ColorTint;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.10f, 1.10f, 1.10f, 1f);
        cb.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        cb.selectedColor = Color.white;
        cb.disabledColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        cb.fadeDuration = 0.08f;
        btn.colors = cb;

        // 左侧竖条（模式色）
        var bar = NewImage(rt, "Bar", d.accent);
        Place(bar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(3f, CardH));

        var name = MakeText(rt, "Name", d.label, 21f, TextCol, TextAlignmentOptions.TopLeft);
        Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(22f, -12f), new Vector2(320f, 28f));

        var code = MakeText(rt, "Code", d.code, 13f, d.accent, TextAlignmentOptions.TopRight);
        Place(code.rectTransform, new Vector2(1f, 1f), new Vector2(-18f, -16f), new Vector2(220f, 20f));

        var desc = MakeText(rt, "Desc", d.desc, 12f, DimCol, TextAlignmentOptions.TopLeft);
        desc.enableWordWrapping = true;
        Place(desc.rectTransform, new Vector2(0f, 1f), new Vector2(22f, -40f), new Vector2(CardW - 300f, 36f));

        var perks = MakeText(rt, "Perks", d.perks, 12f, FaintCol, TextAlignmentOptions.TopLeft);
        Place(perks.rectTransform, new Vector2(0f, 1f), new Vector2(22f, -76f), new Vector2(460f, 18f));

        // 右下：这一模式的实际数值
        string nums = d.dynamicDensity > 0.01f
            ? string.Format(UiText.T("newrun.stats.dynamic",
                Lang.T("怪物 生成×{0} + 动态×{1}      陷阱 ×{2}",
                       "Creatures spawn x{0} + dynamic x{1}      Traps x{2}")),
                d.enemySpawnMul * RunConfig.EnemyDensityScale,
                d.dynamicDensity * RunConfig.EnemyDensityScale,
                d.trapMul)
            : string.Format(UiText.T("newrun.stats.static",
                Lang.T("怪物 ×{0}      陷阱 ×{1}", "Creatures x{0}      Traps x{1}")),
                d.enemySpawnMul * RunConfig.EnemyDensityScale,
                d.trapMul);
        var stat = MakeText(rt, "Stat", nums, 12f, SkyCol, TextAlignmentOptions.BottomRight);
        Place(stat.rectTransform, new Vector2(1f, 0f), new Vector2(-18f, 10f), new Vector2(460f, 18f));

        var captured = d;
        btn.onClick.AddListener(() => Choose(captured));
        _cards.Add(btn);
    }

    // ==========================================================================
    //  右栏：存档列表
    // ==========================================================================
    private static void BuildSlotsColumn(RectTransform root)
    {
        var lh = MakeText(root, "RightHead", UiText.Get("newrun", "rightHead", "载入存档"), 22f, TextCol, TextAlignmentOptions.Left);
        PlaceL(lh.rectTransform, RightX, 404f, new Vector2(420f, 30f));

        var ld = MakeText(root, "RightDesc", UiText.Get("newrun", "rightDesc", "点「载入」继续那一局；也可以改名或删除。"),
                          12f, FaintCol, TextAlignmentOptions.Left);
        PlaceL(ld.rectTransform, RightX, 378f, new Vector2(RightW - 120f, 20f));

        // 右上角：打开存档目录
        var openBtn = MakeButton(root, UiText.T("newrun.opendir", Lang.T("打开目录", "Open folder")), RightX + RightW - 96f, 410f, 96f, 24f,
                                 new Color(0.12f, 0.15f, 0.18f, 1f), DimCol, () =>
        {
            try { Application.OpenURL("file://" + SaveSlots.Root.Replace("\\", "/")); } catch { }
        });
        openBtn.name = "OpenSaveDir";

        // 列表容器
        var list = NewImage(root, "SlotListBg", new Color(0.043f, 0.055f, 0.068f, 1f));
        list.raycastTarget = true;
        list.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        list.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        list.rectTransform.pivot = new Vector2(0f, 1f);
        list.rectTransform.anchoredPosition = new Vector2(RightX, CardsTop);
        list.rectTransform.sizeDelta = new Vector2(RightW, 808f);

        BuildScroll(list.rectTransform, out _slotContent);
        RebuildSlots();

    }

    private static void BuildScroll(RectTransform parent, out RectTransform content)
    {
        var view = new GameObject("Viewport", typeof(RectTransform));
        var vrt = (RectTransform)view.transform;
        vrt.SetParent(parent, false);
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = new Vector2(8f, 8f);
        vrt.offsetMax = new Vector2(-8f, -8f);
        view.AddComponent<RectMask2D>();
        var vimg = view.AddComponent<Image>();
        vimg.color = new Color(0f, 0f, 0f, 0.004f);

        var cgo = new GameObject("Content", typeof(RectTransform));
        content = (RectTransform)cgo.transform;
        content.SetParent(vrt, false);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        content.anchoredPosition = Vector2.zero;

        var vlg = cgo.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.spacing = 6f;
        vlg.padding = new RectOffset(0, 0, 0, 0);

        var fit = cgo.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = parent.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = vrt;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;
    }

    private static void RebuildSlots()
    {
        if (_slotContent == null) return;

        for (int i = _slotContent.childCount - 1; i >= 0; i--)
        {
            var c = _slotContent.GetChild(i);
            c.SetParent(null, false);
            UnityEngine.Object.Destroy(c.gameObject);
        }

        List<SaveSlots.Slot> list;
        try { list = SaveSlots.All(); }
        catch { list = new List<SaveSlots.Slot>(); }

        if (list.Count == 0)
        {
            var empty = MakeText(_slotContent, "Empty",
                UiText.Get("newrun", "emptyList", "<color=#5a6672>还没有存档。左边选一个模式，开新的一局 —— 到层底的存档点会自动写入。</color>"),
                14f, DimCol, TextAlignmentOptions.TopLeft);
            empty.enableWordWrapping = true;
            var ert = empty.rectTransform;
            ert.sizeDelta = new Vector2(0f, 70f);
            var ele = empty.gameObject.AddComponent<LayoutElement>();
            ele.preferredHeight = 70f;
            ele.minHeight = 70f;
            return;
        }

        for (int i = 0; i < list.Count; i++) BuildSlotRow(list[i]);
    }

    private static void BuildSlotRow(SaveSlots.Slot s)
    {
        var go = new GameObject("Slot_" + s.id, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_slotContent, false);
        rt.sizeDelta = new Vector2(0f, 78f);

        var img = go.GetComponent<Image>();
        img.color = RowCol;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 78f;
        le.minHeight = 78f;

        bool isCurrent = s.id == SaveSlots.CurrentId;
        var bar = NewImage(rt, "Bar", isCurrent ? GoodCol : new Color(1f, 1f, 1f, 0.10f));
        Place(bar.rectTransform, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(3f, 78f));

        string tag = isCurrent ? "  <size=12><color=#8fd6c4>" +
            UiText.T("common.current", Lang.T("· 当前", "· CURRENT")) + "</color></size>" : "";
        string head = string.IsNullOrEmpty(s.name) ? s.id : s.name;
        var name = MakeText(rt, "Name", head + tag, 17f, TextCol, TextAlignmentOptions.TopLeft);
        Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(16f, -10f), new Vector2(RightW - 240f, 24f));

        var info = MakeText(rt, "Info", SaveSlots.SlotInfo(s), 12f, DimCol, TextAlignmentOptions.TopLeft);
        Place(info.rectTransform, new Vector2(0f, 1f), new Vector2(16f, -38f), new Vector2(RightW - 240f, 20f));

        if (!s.hasData)
        {
            var hint = MakeText(rt, "Hint", UiText.Get("newrun", "neverSaved", "还没到过存档点"), 11f, FaintCol, TextAlignmentOptions.TopLeft);
            Place(hint.rectTransform, new Vector2(0f, 1f), new Vector2(16f, -58f), new Vector2(300f, 16f));
        }

        string id = s.id;
        string curName = head;

        // 载入
        MakeButton(rt, UiText.T("newrun.load", Lang.T("载入", "Load")), 0f, 0f, 64f, 26f, GoodCol, new Color(0.06f, 0.09f, 0.09f, 1f), () =>
        {
            if (_busy) return;
            _busy = true;
            _busyT = 0f;
            SetCards(false);
            try
            {
                var pre = PreRunScript.instance;
                if (pre != null && pre.runSettingsScreen != null) pre.runSettingsScreen.SetActive(false);
            }
            catch { }
            if (!SaveSlots.Load(id))
            {
                _busy = false;
                SetCards(true);
            }
        }, anchorRight: true, offsetX: -12f);

        // 改名
        MakeButton(rt, UiText.T("newrun.rename", Lang.T("改名", "Rename")), 0f, 0f, 56f, 26f, new Color(0.15f, 0.18f, 0.22f, 1f), DimCol, () =>
        {
            if (_busy) return;
            OpenNameDialog(null, id,
                UiText.T("newrun.rename.title", Lang.T("重命名存档", "Rename save")),
                UiText.T("newrun.rename.desc", Lang.T("只改名字，不动存档内容。", "Only the name changes; the save itself is untouched.")),
                curName);
        }, anchorRight: true, offsetX: -84f);

        // 删除
        MakeButton(rt, UiText.T("newrun.delete", Lang.T("删除", "Delete")), 0f, 0f, 56f, 26f, new Color(0.20f, 0.12f, 0.12f, 1f), BadCol, () =>
        {
            if (_busy) return;
            OpenConfirm(UiText.T("newrun.delete.title", Lang.T("删除存档", "Delete save")),
                string.Format(UiText.T("newrun.delete.desc",
                    Lang.T("「{0}」会被永久删除（含档案进度），没法恢复。",
                           "\"{0}\" will be permanently deleted, including terminal progress. This cannot be undone.")), curName),
                () =>
                {
                    SaveSlots.Delete(id);
                    RebuildSlots();
                });
        }, anchorRight: true, offsetX: -148f);
    }

    // ==========================================================================
    //  返回
    // ==========================================================================
    private static void BuildBackButton(RectTransform root, PreRunScript pre, float y)
    {
        MakeButton(root, UiText.T("newrun.back", Lang.T("← 返回", "← Back")), 0f, 0f, 124f, 30f,
                   new Color(0.10f, 0.12f, 0.15f, 1f), DimCol, () =>
        {
            try { if (pre.runSettingsScreen != null) pre.runSettingsScreen.SetActive(false); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 返回失败: {e.Message}"); }
        }, placeLeft: true, leftX: LeftX, leftY: y);
    }

    // ==========================================================================
    //  选择 → 命名 → 开局
    // ==========================================================================
    private static void Choose(RunConfig.Difficulty d)
    {
        if (_busy || _dialog != null) return;

        // 急速下坠：不建存档，直接开（也会把当前槽清掉，免得存档键覆盖别的档）
        if (d.code == RunConfig.FreefallCode)
        {
            StartRun(d, null);
            return;
        }

        string def = $"{d.label} · {DateTime.Now:MM-dd HH:mm}";
        OpenNameDialog(d, null,
            UiText.T("newrun.name.title", Lang.T("给这个存档起个名字", "Name this save")),
            string.Format(UiText.T("newrun.name.desc",
                Lang.T("{0} · 每层底部的存档点会自动写入，也可以随时按 {1} 手动保存。",
                       "{0} · the checkpoint at the bottom of each layer saves automatically; press {1} to save manually.")),
                d.label, SaveSlots.SaveKey), def);
    }

    private static void StartRun(RunConfig.Difficulty d, string slotName)
    {
        if (_busy) return;
        _busy = true;
        _busyT = 0f;
        SetCards(false);

        bool ok = slotName == null
            ? SaveSlots.StartNoSlot(d)
            : SaveSlots.CreateAndStart(d, slotName);

        if (!ok)
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 开局失败");
            _busy = false;
            SetCards(true);
        }
    }

    private static void SetCards(bool on)
    {
        for (int i = 0; i < _cards.Count; i++)
            if (_cards[i] != null) _cards[i].interactable = on;
    }

    // ==========================================================================
    //  弹窗：命名 / 确认
    // ==========================================================================
    private static void OpenNameDialog(RunConfig.Difficulty mode, string renameId,
                                       string title, string sub, string initial)
    {
        CloseDialog();
        if (_root == null) return;

        _pendingMode = mode;
        _pendingRename = renameId;

        var dim = NewImage(_stage != null ? (Transform)_stage : _root.transform, "DlgDim", new Color(0f, 0f, 0f, 0.78f));
        dim.raycastTarget = true;
        var drt = dim.rectTransform;
        drt.anchorMin = Vector2.zero;
        drt.anchorMax = Vector2.one;
        drt.offsetMin = Vector2.zero;
        drt.offsetMax = Vector2.zero;
        _dialog = dim.gameObject;

        var panel = NewImage(dim.transform, "Panel", new Color(0.075f, 0.094f, 0.118f, 1f));
        panel.raycastTarget = true;
        Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 250f));

        var t = MakeText(panel.transform, "T", title, 22f, TextCol, TextAlignmentOptions.Left);
        PlaceL(t.rectTransform, -300f, 92f, new Vector2(560f, 30f));

        var st = MakeText(panel.transform, "Sub", sub, 13f, DimCol, TextAlignmentOptions.Left);
        st.enableWordWrapping = true;
        PlaceL(st.rectTransform, -300f, 60f, new Vector2(580f, 36f));

        _dialogInput = MakeInput(panel.rectTransform, new Vector2(0f, -6f), new Vector2(580f, 42f), initial);

        MakeButton(panel.transform, UiText.T("common.cancel", Lang.T("取消", "Cancel")), 0f, 0f, 100f, 32f, new Color(0.13f, 0.16f, 0.19f, 1f), DimCol,
                   CloseDialog, placeLeft: true, leftX: -290f, leftY: -88f);

        MakeButton(panel.transform, UiText.T("common.start", Lang.T("开始", "Start")), 0f, 0f, 120f, 32f, SkyCol, new Color(0.03f, 0.06f, 0.08f, 1f),
                   ConfirmDialog, placeLeft: true, leftX: 170f, leftY: -88f);

        // 回车 = 确定
        try
        {
            if (_dialogInput != null)
                _dialogInput.onSubmit.AddListener(_ => ConfirmDialog());
        }
        catch { }

        _dialog.transform.SetAsLastSibling();
    }

    private static void OpenConfirm(string title, string sub, Action onYes)
    {
        CloseDialog();
        if (_root == null) return;

        _pendingMode = null;
        _pendingRename = null;
        _pendingConfirm = onYes;

        var dim = NewImage(_stage != null ? (Transform)_stage : _root.transform, "DlgDim", new Color(0f, 0f, 0f, 0.78f));
        dim.raycastTarget = true;
        var drt = dim.rectTransform;
        drt.anchorMin = Vector2.zero;
        drt.anchorMax = Vector2.one;
        drt.offsetMin = Vector2.zero;
        drt.offsetMax = Vector2.zero;
        _dialog = dim.gameObject;

        var panel = NewImage(dim.transform, "Panel", new Color(0.075f, 0.094f, 0.118f, 1f));
        panel.raycastTarget = true;
        Place(panel.rectTransform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600f, 210f));

        var t = MakeText(panel.transform, "T", title, 22f, TextCol, TextAlignmentOptions.Left);
        PlaceL(t.rectTransform, -270f, 66f, new Vector2(520f, 30f));

        var st = MakeText(panel.transform, "Sub", sub, 13f, DimCol, TextAlignmentOptions.Left);
        st.enableWordWrapping = true;
        PlaceL(st.rectTransform, -270f, 20f, new Vector2(540f, 60f));

        MakeButton(panel.transform, UiText.T("common.cancel", Lang.T("取消", "Cancel")), 0f, 0f, 100f, 32f, new Color(0.13f, 0.16f, 0.19f, 1f), DimCol,
                   CloseDialog, placeLeft: true, leftX: -270f, leftY: -62f);

        MakeButton(panel.transform, UiText.T("common.delete", Lang.T("删除", "Delete")), 0f, 0f, 120f, 32f, BadCol, new Color(0.10f, 0.03f, 0.03f, 1f),
                   () =>
                   {
                       var cb = _pendingConfirm;
                       CloseDialog();
                       try { cb?.Invoke(); } catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 确认操作失败: {e.Message}"); }
                   }, placeLeft: true, leftX: 150f, leftY: -62f);

        _dialog.transform.SetAsLastSibling();
    }

    private static void ConfirmDialog()
    {
        string text = null;
        try { text = _dialogInput != null ? _dialogInput.text : null; } catch { }

        var mode = _pendingMode;
        var renameId = _pendingRename;

        // 重命名
        if (!string.IsNullOrEmpty(renameId))
        {
            if (string.IsNullOrWhiteSpace(text)) { return; }
            SaveSlots.Rename(renameId, text);
            CloseDialog();
            RebuildSlots();
            return;
        }

        // 新建存档
        if (mode == null) { CloseDialog(); return; }
        if (string.IsNullOrWhiteSpace(text)) text = $"{mode.label} · {DateTime.Now:MM-dd HH:mm}";

        CloseDialog();
        StartRun(mode, text.Trim());
    }

    private static void CloseDialog()
    {
        if (_dialog != null) UnityEngine.Object.Destroy(_dialog);
        _dialog = null;
        _dialogInput = null;
        _pendingMode = null;
        _pendingRename = null;
        _pendingConfirm = null;
    }

    // ==========================================================================
    //  UI 小工具
    // ==========================================================================
    private static Image NewImage(Transform parent, string name, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var img = go.GetComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string name, string text,
                                            float size, Color color, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);

        var t = go.AddComponent<TextMeshProUGUI>();
        if (DlcPanel.Font != null) t.font = DlcPanel.Font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        return t;
    }

    // 通用小按钮。三种摆法：贴右（anchorRight）、贴左（placeLeft）、或者给定 x/y 左上角
    private static Button MakeButton(Transform parent, string label,
                                     float x, float y, float w, float h,
                                     Color bg, Color fg, Action onClick,
                                     bool anchorRight = false, float offsetX = 0f,
                                     bool placeLeft = false, float leftX = 0f, float leftY = 0f)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);

        if (placeLeft)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(leftX, leftY);
        }
        else if (anchorRight)
        {
            rt.anchorMin = new Vector2(1f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(offsetX, 0f);
        }
        else
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
        }
        rt.sizeDelta = new Vector2(w, h);

        var img = go.GetComponent<Image>();
        img.color = bg;
        img.raycastTarget = true;

        var btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        btn.transition = Selectable.Transition.ColorTint;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
        cb.pressedColor = new Color(0.80f, 0.80f, 0.80f, 1f);
        cb.selectedColor = Color.white;
        cb.fadeDuration = 0.07f;
        btn.colors = cb;

        var t = MakeText(rt, "Label", label, 13f, fg, TextAlignmentOptions.Center);
        var trt = t.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        if (onClick != null) btn.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 按钮 {label} 出错: {e.Message}"); } });
        return btn;
    }

    private static TMP_InputField MakeInput(RectTransform parent, Vector2 centerOffset, Vector2 size, string initial)
    {
        var bgGo = new GameObject("Input", typeof(RectTransform), typeof(Image));
        var bg = (RectTransform)bgGo.transform;
        bg.SetParent(parent, false);
        bg.anchorMin = new Vector2(0.5f, 0.5f);
        bg.anchorMax = new Vector2(0.5f, 0.5f);
        bg.pivot = new Vector2(0.5f, 0.5f);
        bg.anchoredPosition = centerOffset;
        bg.sizeDelta = size;

        var img = bgGo.GetComponent<Image>();
        img.color = new Color(0.13f, 0.16f, 0.19f, 1f);
        img.raycastTarget = true;

        var areaGo = new GameObject("TextArea", typeof(RectTransform));
        var area = (RectTransform)areaGo.transform;
        area.SetParent(bg, false);
        area.anchorMin = Vector2.zero;
        area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(10f, 2f);
        area.offsetMax = new Vector2(-10f, -2f);
        areaGo.AddComponent<RectMask2D>();

        var txt = MakeText(area, "Text", "", 17f, new Color(0.92f, 0.96f, 0.96f, 1f), TextAlignmentOptions.Left);
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        txt.raycastTarget = false;

        var ph = MakeText(area, "Placeholder", "给它起个名字…", 17f, new Color(0.42f, 0.47f, 0.52f, 1f),
                          TextAlignmentOptions.Left);
        var prt = ph.rectTransform;
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero;
        prt.offsetMax = Vector2.zero;
        ph.raycastTarget = false;

        try
        {
            var input = bgGo.AddComponent<TMP_InputField>();
            input.targetGraphic = img;
            input.textViewport = area;
            input.textComponent = txt;
            input.placeholder = ph;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 32;
            input.text = initial ?? "";
            try { input.ActivateInputField(); } catch { }
            return input;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 创建输入框失败: {e.Message}");
            return null;
        }
    }

    // anchor 定在父物体的某一点，pivot 跟随 anchor
    private static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    // 相对屏幕中心、以左边缘为基准（pivot 左中）
    private static void PlaceL(RectTransform rt, float x, float y, Vector2 size)
    {
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = size;
    }
}
