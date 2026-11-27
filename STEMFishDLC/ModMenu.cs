using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  ModMenu - 主菜单右上角的「MOD 管理」（给玩家的，不是给角色的）
//  ---------------------------------------------------------------------------
//  两个界面严格分工：
//    战术终端（DlcPanel） = 剧情内设备，只有 体征 / 归档 / 链路
//    MOD 管理（本文件）   = 模块开关、热键、存档、启用/隔离
//
//  入口：
//    · 主菜单（PreRunScript）右上角按钮 → 本面板
//    · 暂停菜单（ESC）顶部「存档」按钮  → 本面板的「存档」页
//    · 主菜单底部一行状态文字（绿=启用 / 红=停用）
//
//  ★ 面板容器 _root 下有两个子物体：Dim（半透明遮罩）+ Panel。
//    关闭必须 `_root.SetActive(false)` —— 只关 Panel 会把 Dim 留在屏幕上，
//    那张全屏 Image 带 raycastTarget，会吃掉之后所有的点击（"关一次就再也点不开"就是这个）。
// ============================================================================

public static class ModMenu
{
    // ---------------- 模块开关（运行时可切） ----------------
    public class Module
    {
        public string key;
        public string name;
        public string desc;
        public Func<bool> get;
        public Action<bool> set;
    }

    private static readonly List<Module> _modules = new List<Module>();

    // ---------------- UI ----------------
    private static GameObject _root, _panel;
    private static Canvas _canvas;
    private static CanvasGroup _group;
    private static RectTransform _content;
    private static TextMeshProUGUI _header, _footer;
    private static int _tab;
    private static bool _visible;
    private static readonly string[] TabNames = { "Modules", "Hotkeys", "Saves" };
    private static readonly TextMeshProUGUI[] _tabTexts = new TextMeshProUGUI[3];

    private static float _animT = 1f;
    private const float AnimDuration = 0.14f;

    private static readonly Color PanelBg = new Color(0.055f, 0.065f, 0.085f, 0.97f);
    private static readonly Color HeaderBg = new Color(0.09f, 0.11f, 0.14f, 0.99f);
    private static readonly Color Accent = new Color(0.20f, 0.78f, 0.72f, 1f);
    private static readonly Color DimTxt = new Color(0.72f, 0.78f, 0.80f, 1f);
    private static readonly Color Body = new Color(0.90f, 0.94f, 0.94f, 1f);
    private static readonly Color RowBg = new Color(0.10f, 0.125f, 0.155f, 1f);
    private static readonly Color RowSel = new Color(0.13f, 0.26f, 0.26f, 1f);

    public static bool Visible => _visible;

    // 本页的 UI 文案：ui.<lang>.json 的 strings 段，键前缀 modmenu.
    private static string M(string key, string zh, string en)
    {
        return UiText.T("modmenu." + key, Lang.T(zh, en));
    }

    private static void RefreshTabNames()
    {
        TabNames[0] = M("tab.modules", "模块", "Modules");
        TabNames[1] = M("tab.hotkeys", "热键", "Hotkeys");
        TabNames[2] = M("tab.saves", "存档", "Saves");
    }

    // 需要重启才生效的项：先记在 pending 里，点"应用并重启"才落盘
    private static bool _pEnabled, _pIsolate;
    private static void SyncPending()
    {
        _pEnabled = ModState.Enabled;
        _pIsolate = ModState.Isolate;
    }

    // ==========================================================================
    //  初始化
    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        RefreshTabNames();
        RegisterModules();

        foreach (var m in _modules)
        {
            try
            {
                m.set(plugin.Config.Bind("Modules", m.key, m.get(), m.desc).Value);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 模块 {m.key} 初始化失败: {e.Message}"); }
        }

        try
        {
            var harmony = new Harmony("com.stemfish.dlc.modmenu");
            int n = 0;

            // 主菜单（一进游戏那个画面）= PreRunScript.Start
            var pre = AccessTools.Method(typeof(PreRunScript), "Start");
            if (pre != null)
            {
                harmony.Patch(pre, postfix: new HarmonyMethod(
                    typeof(ModMenu).GetMethod("PreRunStart_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                n += Harmony.GetPatchInfo(pre)?.Postfixes?.Count ?? 0;
            }
            else Plugin.Log.LogError("[STEMFishDLC] 找不到 PreRunScript.Start");

            // 暂停菜单（ESC）：这里**只**放一个「存档」按钮，不放 MOD 管理
            var t2 = AccessTools.Method(typeof(PauseHandler), "Start");
            if (t2 != null)
            {
                harmony.Patch(t2, postfix: new HarmonyMethod(
                    typeof(ModMenu).GetMethod("PauseStart_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));
                n += Harmony.GetPatchInfo(t2)?.Postfixes?.Count ?? 0;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] MOD 管理页注入点就绪 | postfix 合计={n}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] MOD 管理页 hook 失败: {e}");
        }
    }

    private static void RegisterModules()
    {
        _modules.Clear();
        _modules.Add(new Module
        {
            key = "Voice", name = M("module.voice.name", "自言自语", "Voice"),
            desc = M("module.voice.desc", "角色内心独白（F9）", "Inner monologue (F9)"),
            get = () => FishVoice.Enabled, set = v => FishVoice.Enabled = v,
        });
        _modules.Add(new Module
        {
            key = "VoiceExpand", name = M("module.expand.name", "原版台词扩充", "Base-line expansion"),
            desc = M("module.expand.desc", "原版台词扩充（F10）", "Expand the base game's contextual lines (F10)"),
            get = () => FishVoice.ExpandEnabled, set = v => FishVoice.ExpandEnabled = v,
        });
        _modules.Add(new Module
        {
            key = "WeaponHud", name = M("module.weapon.name", "武器状态 HUD", "Weapon HUD"),
            desc = M("module.weapon.desc", "右下角武器状态", "Weapon status in the lower right"),
            get = () => WeaponHud.Enabled, set = v => WeaponHud.Enabled = v,
        });
        _modules.Add(new Module
        {
            key = "LoreNotes", name = M("module.lore.name", "档案投放到世界", "Lore notes in the world"),
            desc = M("module.lore.desc", "每层生成一份档案", "One terminal entry per layer"),
            get = () => LoreNote.SpawnEnabled, set = v => LoreNote.SpawnEnabled = v,
        });
        _modules.Add(new Module
        {
            key = "StarterKit", name = M("module.starter.name", "开局物资", "Starter kit"),
            desc = M("module.starter.desc", "新开局发放全套装备", "Full kit on a new run"),
            get = () => StarterKit.Enabled, set = v => StarterKit.Enabled = v,
        });
        _modules.Add(new Module
        {
            key = "Cursor", name = M("module.cursor.name", "准星替换", "Crosshair"),
            desc = M("module.cursor.desc", "内置准星替换", "Replace the crosshair"),
            get = () => CursorFeature.Enabled, set = v => CursorFeature.Toggle(v),
        });
        _modules.Add(new Module
        {
            key = "TailFlow",
            name = M("module.tail.name", "尾巴飘动", "Flowing tail"),
            desc = M("module.tail.desc", "SF-07 的尾巴网格飘动（关掉 = 原版整根旋转）",
                     "Soft tail physics for SF-07 (off = vanilla rigid swing)"),
            get = () => TailFlow.Enabled, set = v => TailFlow.SetEnabled(v),
        });
        _modules.Add(new Module
        {
            key = "HealthDollVanilla",
            name = M("module.doll.name", "原版角色显示人体图", "Health doll on vanilla"),
            desc = M("module.doll.desc", "选原版角色时也显示右上角人体图（关掉 = 只在 SF-07 局显示）",
                     "Keep the health doll visible when you play a vanilla character"),
            get = () => HealthDoll.ShowOnVanilla, set = v => HealthDoll.SetShowOnVanilla(v),
        });
    }

    // ==========================================================================
    //  主菜单注入：右上角按钮 + 底部状态文字
    // ==========================================================================
    private const string MainBtnName = "STEMFishDLC_MainMenuButton";
    private const string StatusName = "STEMFishDLC_StatusLabel";

    private static void PreRunStart_Postfix(PreRunScript __instance)
    {
        try
        {
            if (__instance == null || __instance.loadButton == null) return;
            var parent = __instance.transform;

            InjectMainButton(__instance, parent);
            InjectStatusLabel(parent);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 主菜单按钮注入失败: {e.Message}");
        }
    }

    private static void InjectMainButton(PreRunScript pre, Transform parent)
    {
        for (int i = 0; i < parent.childCount; i++)
            if (parent.GetChild(i).name == MainBtnName) return;

        var go = UnityEngine.Object.Instantiate(pre.loadButton.gameObject, parent, false);
        go.name = MainBtnName;
        go.SetActive(true);

        var btn = go.GetComponent<Button>();
        if (btn != null)
        {
            int pc = btn.onClick.GetPersistentEventCount();
            for (int i = 0; i < pc; i++) btn.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            btn.onClick.RemoveAllListeners();
            btn.interactable = true;
            btn.onClick.AddListener(() => Toggle(0));
        }

        CleanupClonedButton(go);
        ReplaceTexts(go, M("button", "MOD 管理", "MOD SETTINGS"));

        var rt = go.GetComponent<RectTransform>();
        var srcRt = pre.loadButton.transform as RectTransform;
        if (rt != null)
        {
            float w = 200f, h = 56f;
            if (srcRt != null)
            {
                w = Mathf.Clamp(srcRt.rect.width * 0.72f, 140f, 260f);
                h = Mathf.Clamp(srcRt.rect.height * 0.62f, 40f, 72f);
            }
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(-28f, -28f);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
        }
        go.transform.SetAsLastSibling();

        foreach (var tip in go.GetComponentsInChildren<UITooltip>(true))
        {
            tip.skipLocale = true;
            tip.tipName = M("tip.name", "MOD 管理", "MOD SETTINGS");
            tip.tipDesc = M("tip.desc", "模块 / 热键 / 存档 / 启用", "Modules, hotkeys, saves, enable");
            tip.localeName = "";
            tip.localeDesc = "";
        }

        Plugin.Log.LogInfo("[STEMFishDLC] 主菜单「MOD 管理」按钮已注入（右上角）");
    }

    // 顶部状态文字：绿色=已启用，红色=已停用
    //  v0.18.8：从"底部居中"移到"屏幕顶端居中"（作者要求，底部会挡到原版按钮）
    private static void InjectStatusLabel(Transform parent)
    {
        for (int i = 0; i < parent.childCount; i++)
            if (parent.GetChild(i).name == StatusName) { RefreshStatusLabel(); return; }

        var t = NewText(StatusName, parent, ModState.StatusLine, 20, ModState.StatusColor);
        var rt = t.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.sizeDelta = new Vector2(600f, 30f);
        rt.anchoredPosition = new Vector2(0f, -14f);
        t.alignment = TextAlignmentOptions.Center;
        t.fontStyle = FontStyles.Bold;
        rt.SetAsLastSibling();

        Plugin.Log.LogInfo($"[STEMFishDLC] 主菜单状态文字: {ModState.StatusLine}");
    }

    private static void RefreshStatusLabel()
    {
        try
        {
            var pre = PreRunScript.instance;
            if (pre == null) return;
            for (int i = 0; i < pre.transform.childCount; i++)
            {
                var c = pre.transform.GetChild(i);
                if (c == null || c.name != StatusName) continue;
                var t = c.GetComponent<TextMeshProUGUI>();
                if (t == null) continue;
                t.text = ModState.StatusLine;
                t.color = ModState.StatusColor;
            }
        }
        catch { }
    }

    // ==========================================================================
    //  暂停菜单注入：顶部「存档」按钮（不是 MOD 管理）
    //  ---------------------------------------------------------------------------
    //  ★ 不克隆原版按钮了：克隆体会继承那个齿轮/设置图标，而且尺寸来自原按钮
    //    （v0.11.x 里它一直是"设置"图标的样子）。改成**自己画一个方框 + 文字**：
    //      外框 Image（青色 1~2px 边） + 内层深色 Image + TMP 文字「存档」
    //    再给它套一个 overrideSorting 的嵌套 Canvas，保证画在暂停菜单最上面。
    // ==========================================================================
    private const string SaveBtnName = "STEMFishDLC_SaveButton";

    private static GameObject _saveBtn;

    private static void PauseStart_Postfix(PauseHandler __instance)
    {
        try
        {
            if (__instance == null || __instance.buttons == null || __instance.buttons.Count == 0) return;
            var src = __instance.buttons[0];
            if (src == null) return;

            var canvas = src.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var parent = canvas.transform;

            // 查重：同一个 canvas 下已经有了就复用
            if (_saveBtn != null && _saveBtn.transform.parent == parent)
            {
                if (!_saveBtn.activeSelf) _saveBtn.SetActive(true);
                return;
            }
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == SaveBtnName)
                {
                    _saveBtn = parent.GetChild(i).gameObject;
                    _saveBtn.SetActive(true);
                    return;
                }

            // ---- 容器 ----
            var rt = NewRect(SaveBtnName, parent);
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(136f, 44f);
            rt.anchoredPosition = new Vector2(-26f, -26f);      // 屏幕右上角
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;

            // 嵌套 Canvas：overrideSorting 保证画在暂停菜单之上
            var nested = rt.gameObject.AddComponent<Canvas>();
            nested.overrideSorting = true;
            nested.sortingOrder = 32000;
            rt.gameObject.AddComponent<GraphicRaycaster>();

            // ---- 边框 ----
            var border = AddImage(rt.gameObject, new Color(0.28f, 0.72f, 0.66f, 0.90f));
            border.raycastTarget = true;

            // ---- 内层底色（半透明黑，扁平）----
            var fillRt = NewRect("Fill", rt);
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(2f, 2f); fillRt.offsetMax = new Vector2(-2f, -2f);
            var fill = AddImage(fillRt.gameObject, new Color(0.05f, 0.07f, 0.08f, 0.94f));
            fill.raycastTarget = false;

            // ---- 文字 ----
            var label = NewText("Label", rt, M("pause.save", "保存", "SAVE"), 22f, new Color(0.62f, 0.94f, 0.88f, 1f));
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.fontStyle = FontStyles.Bold;

            // ---- 按钮 ----
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = border;
            btn.transition = Selectable.Transition.ColorTint;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.9f, 1.9f, 1.9f, 1f);
            colors.pressedColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.4f, 0.4f, 0.4f, 0.6f);
            colors.fadeDuration = 0.08f;
            btn.colors = colors;
            // v0.18.1：只做「存进当前存档」这一件事，不再顺手把 MOD 管理面板拉出来
            //（管理面板仍然可以用 F8 打开；作者要求游戏内只留一个纯净的保存入口）
            btn.onClick.AddListener(() => { try { SaveSlots.SaveCurrent(false); } catch { } });

            _saveBtn = rt.gameObject;
            rt.SetAsLastSibling();

            Plugin.Log.LogInfo($"[STEMFishDLC] 暂停菜单「存档」按钮已注入（自绘方框）parent={parent.name} size={rt.sizeDelta}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 暂停菜单按钮注入失败: {e.Message}");
        }
    }

    private static void CleanupClonedButton(GameObject go)
    {
        try
        {
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var tn = c.GetType().Name;
                if (tn.IndexOf("Localiz", StringComparison.Ordinal) >= 0 ||
                    tn.IndexOf("LocaleText", StringComparison.Ordinal) >= 0 ||
                    tn.IndexOf("UITooltip", StringComparison.Ordinal) >= 0)
                    UnityEngine.Object.Destroy(c);
            }

            var t = go.transform;
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                bool hasText = false;
                foreach (var comp in child.GetComponentsInChildren<Component>(true))
                {
                    if (comp != null && comp.GetType().FullName != null &&
                        comp.GetType().FullName.StartsWith("TMPro.TextMesh", StringComparison.Ordinal))
                    { hasText = true; break; }
                }
                if (!hasText) UnityEngine.Object.Destroy(child.gameObject);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 清理克隆按钮失败: {e.Message}");
        }
    }

    private static void ReplaceTexts(GameObject go, string text)
    {
        try
        {
            foreach (var t in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if (t != null) t.text = text;
            foreach (var t in go.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t != null) t.text = text;
        }
        catch { }
    }

    // ==========================================================================
    //  开关
    // ==========================================================================
    public static void Toggle() => Toggle(_tab);

    public static void Toggle(int tab)
    {
        try
        {
            if (_visible)
            {
                // 已经开着：同一个 tab 就关闭，不同 tab 就切换过去
                if (tab == _tab) { Close(); return; }
                _tab = Mathf.Clamp(tab, 0, TabNames.Length - 1);
                Refresh();
                return;
            }
            _tab = Mathf.Clamp(tab, 0, TabNames.Length - 1);
            Open();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 打开 MOD 管理页失败: {e}");
        }
    }

    private static void Close()
    {
        _visible = false;
        // ★ 关掉整个容器（不只是 Panel），否则全屏遮罩会一直吃点击
        if (_root != null) _root.SetActive(false);
    }

    private static void Open()
    {
        var host = ResolveHost();
        bool ownCanvas = false;
        if (host == null)
        {
            host = GetFallbackCanvas();
            ownCanvas = true;
            if (host == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到可用的 Canvas，MOD 管理页无法显示");
                return;
            }
        }

        if (_root != null && _root.transform.parent != host)
        {
            UnityEngine.Object.Destroy(_root);
            _root = null;
            _panel = null;
            _group = null;
        }

        if (_root == null) Build(host, ownCanvas);
        if (_panel == null) return;

        _root.SetActive(true);
        _root.transform.SetAsLastSibling();

        _visible = true;
        _panel.SetActive(true);

        // ★ 不要依赖 Update 里的淡入：只要有任何一处 Update 抛异常，
        //   CanvasGroup.alpha 就会永远停在 0 → 面板"建出来了但看不见"。
        //   直接给满 alpha，动画只做缩放。
        _animT = 1f;
        if (_group != null) _group.alpha = 1f;
        if (_panel != null) _panel.transform.localScale = Vector3.one;
        Refresh();

        try
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        catch { }

        Plugin.Log.LogInfo($"[STEMFishDLC] MOD 管理页已打开 | tab={TabNames[_tab]} host={host.name} " +
                           $"screen={Screen.width}x{Screen.height}");
    }

    private static GameObject _fallback;
    private static Transform GetFallbackCanvas()
    {
        try
        {
            if (_fallback != null) return _fallback.transform;
            _fallback = new GameObject("STEMFishDLC_FallbackCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_fallback);
            var c = _fallback.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 32767;
            var sc = _fallback.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);
            sc.matchWidthOrHeight = 0.5f;
            _fallback.AddComponent<GraphicRaycaster>();
            return _fallback.transform;
        }
        catch { return null; }
    }

    private static Transform ResolveHost()
    {
        try
        {
            var pre = PreRunScript.instance;
            if (pre != null) return pre.mainCanvas != null ? pre.mainCanvas.transform : pre.transform;
        }
        catch { }
        try
        {
            var pc = PlayerCamera.main;
            if (pc != null && pc.mainCanvas != null) return pc.mainCanvas.transform;
        }
        catch { }
        return null;
    }

    /// <summary>强制收掉面板：别的界面要独占鼠标时先调它，别留全屏遮罩。</summary>
    public static void ForceHide()
    {
        _visible = false;
        try { if (_root != null && _root.activeSelf) _root.SetActive(false); } catch { }
    }

    public static void Update()
    {
        if (_root == null) { _visible = false; return; }
        if (!_visible)
        {
            // ★ 关着的时候也要保证根节点是隐藏的：
            //   语言切换会走 Refresh → 可能重建 _root，新节点默认 activeSelf=true，
            //   全屏黑底 + GraphicRaycaster 会一直挡在主菜单前面（整屏变暗、点不动）。
            if (_root.activeSelf) _root.SetActive(false);
            return;
        }

        if (_animT < 1f)
        {
            _animT = Mathf.Min(1f, _animT + Time.unscaledDeltaTime / AnimDuration);
            float e = Mathf.SmoothStep(0f, 1f, _animT);
            if (_group != null) _group.alpha = e;
            if (_panel != null) _panel.transform.localScale = Vector3.Lerp(new Vector3(0.96f, 0.96f, 1f), Vector3.one, e);
        }
        if (Input.GetKeyDown(KeyCode.Escape)) Close();
    }

    // 暂停菜单那个「存档」按钮：不暂停时收起来
    //  （它挂在暂停菜单的 Canvas 下，正常情况下父级会被一起禁用；这里只是保险）
    public static void TickPauseButton()
    {
        try
        {
            if (_saveBtn == null) return;
            bool want = Plugin.IsPaused || _visible;
            if (_saveBtn.activeSelf != want) _saveBtn.SetActive(want);
        }
        catch { }
    }

    // ==========================================================================
    //  构建
    // ==========================================================================
    private static void Build(Transform host, bool ownCanvas)
    {
        _root = new GameObject("STEMFishDLC_ModMenu", typeof(RectTransform));
        _root.transform.SetParent(host, false);

        var rt0 = _root.GetComponent<RectTransform>();
        rt0.anchorMin = Vector2.zero;
        rt0.anchorMax = Vector2.one;
        rt0.offsetMin = Vector2.zero;
        rt0.offsetMax = Vector2.zero;
        rt0.pivot = new Vector2(0.5f, 0.5f);
        rt0.localScale = Vector3.one;

        _canvas = _root.AddComponent<Canvas>();
        if (ownCanvas)
        {
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32767;
            var sc = _root.AddComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);
            sc.matchWidthOrHeight = 0.5f;
        }
        else
        {
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 32767;
        }
        _root.AddComponent<GraphicRaycaster>();

        // 遮罩（同一容器下的兄弟，关闭时随 _root 一起隐藏）
        var dim = NewRect("Dim", _root.transform);
        dim.anchorMin = Vector2.zero; dim.anchorMax = Vector2.one;
        dim.offsetMin = Vector2.zero; dim.offsetMax = Vector2.zero;
        var dimImg = dim.gameObject.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.55f);
        dimImg.raycastTarget = true;

        var panelRt = NewRect("Panel", _root.transform);
        panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(880, 680);
        panelRt.anchoredPosition = Vector2.zero;
        panelRt.localScale = Vector3.one;
        _panel = panelRt.gameObject;
        AddImage(_panel, PanelBg);
        _group = _panel.AddComponent<CanvasGroup>();

        // 顶栏
        var headerRt = NewRect("Header", _panel.transform);
        headerRt.anchorMin = new Vector2(0, 1); headerRt.anchorMax = new Vector2(1, 1);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(0, 52);
        AddImage(headerRt.gameObject, HeaderBg);

        _header = NewText("Title", headerRt, M("title", "STEM Fish DLC · MOD 管理", "STEM Fish DLC · MOD SETTINGS"), 24, Accent);
        var trt = _header.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(20, 0); trt.offsetMax = new Vector2(-70, 0);
        _header.alignment = TextAlignmentOptions.Left;

        // 页签
        var tabRow = NewRect("Tabs", _panel.transform);
        tabRow.anchorMin = new Vector2(0, 1); tabRow.anchorMax = new Vector2(1, 1);
        tabRow.pivot = new Vector2(0.5f, 1f);
        tabRow.sizeDelta = new Vector2(-32, 38);
        tabRow.anchoredPosition = new Vector2(0, -60);
        var hlg = tabRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8; hlg.childForceExpandWidth = true; hlg.childForceExpandHeight = true;

        for (int i = 0; i < TabNames.Length; i++)
        {
            int idx = i;
            var btnRt = NewRect("Tab" + i, tabRow);
            var img = AddImage(btnRt.gameObject, RowBg);
            var btn = btnRt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => { _tab = idx; Refresh(); });
            var txt = NewText("Label", btnRt, TabNames[i], 18, Body);
            var lrt = txt.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            txt.alignment = TextAlignmentOptions.Center;
            _tabTexts[i] = txt;
        }

        // 内容区
        var contentWrap = NewRect("Content", _panel.transform);
        contentWrap.anchorMin = Vector2.zero; contentWrap.anchorMax = Vector2.one;
        contentWrap.offsetMin = new Vector2(24, 46); contentWrap.offsetMax = new Vector2(-24, -108);
        AddImage(contentWrap.gameObject, new Color(0.08f, 0.10f, 0.13f, 1f));

        BuildScroll(contentWrap, out _content);

        // 底部说明
        _footer = NewText("Footer", _panel.transform, "", 14, DimTxt);
        var frt = _footer.rectTransform;
        frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0);
        frt.pivot = new Vector2(0.5f, 0f);
        frt.sizeDelta = new Vector2(-32, 34);
        frt.anchoredPosition = new Vector2(0, 6);
        _footer.alignment = TextAlignmentOptions.Left;

        // 关闭
        var closeRt = NewRect("Close", headerRt);
        closeRt.anchorMin = closeRt.anchorMax = new Vector2(1f, 0.5f);
        closeRt.pivot = new Vector2(1f, 0.5f);
        closeRt.sizeDelta = new Vector2(56, 32);
        closeRt.anchoredPosition = new Vector2(-10, 0);
        var closeImg = AddImage(closeRt.gameObject, new Color(0.35f, 0.16f, 0.14f, 1f));
        var closeBtn = closeRt.gameObject.AddComponent<Button>();
        closeBtn.targetGraphic = closeImg;
        closeBtn.onClick.AddListener(Close);
        var ct = NewText("X", closeRt, UiText.T("common.close", Lang.T("关闭", "Close")), 15, Body);
        var crt = ct.rectTransform;
        crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
        crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
        ct.alignment = TextAlignmentOptions.Center;

        _panel.SetActive(false);
        Plugin.Log.LogInfo("[STEMFishDLC] MOD 管理页构建完成");
    }

    private static void BuildScroll(RectTransform parent, out RectTransform content)
    {
        var rt = NewRect("Viewport", parent);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(6, 6); rt.offsetMax = new Vector2(-6, -6);
        rt.gameObject.AddComponent<RectMask2D>();
        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.004f);

        content = NewRect("Content", rt);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = Vector2.zero;
        content.anchoredPosition = Vector2.zero;

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.spacing = 6; vlg.padding = new RectOffset(4, 4, 4, 4);
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = parent.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = rt; scroll.content = content;
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 28f;
    }

    // ==========================================================================
    //  刷新
    // ==========================================================================
    private static void Refresh()
    {
        for (int i = 0; i < _tabTexts.Length; i++)
        {
            if (_tabTexts[i] == null) continue;
            _tabTexts[i].color = (i == _tab) ? Accent : Body;
            var img = _tabTexts[i].transform.parent.GetComponent<Image>();
            if (img != null) img.color = (i == _tab) ? RowSel : RowBg;
        }

        ClearContent();
        if (_footer != null)
            _footer.text = string.Format(
                UiText.T("modmenu.footer", Lang.T("v{0} · 配置写回 BepInEx/config/com.stemfish.dlc.cfg · 状态：{1}",
                                                   "v{0} · Config: BepInEx/config/com.stemfish.dlc.cfg · Status: {1}")),
                ModState.Version,
                ModState.Enabled ? UiText.T("common.enabled", Lang.T("已启用", "ENABLED"))
                                 : UiText.T("common.disabled", Lang.T("已停用", "DISABLED")));

        if (_tab == 0) BuildModuleTab();
        else if (_tab == 1) BuildKeyTab();
        else BuildSaveTab();
    }

    private static void ClearContent()
    {
        if (_content == null) return;
        for (int i = _content.childCount - 1; i >= 0; i--)
        {
            var c = _content.GetChild(i);
            c.SetParent(null, false);
            UnityEngine.Object.Destroy(c.gameObject);
        }
    }

    // ==========================================================================
    //  页 0：模块（含总开关 / 隔离 / 调试）
    // ==========================================================================
    private static void BuildModuleTab()
    {
        SyncPendingOnce();
        bool dirty = _pEnabled != ModState.Enabled || _pIsolate != ModState.Isolate;

        AddLabel(dirty
            ? M("status.pending", "<b>状态</b>   <color=#d8a83e>待生效</color>",
                              "<b>STATUS</b>   <color=#d8a83e>RESTART REQUIRED</color>")
            : M("status.ok", "<b>状态</b>   <color=#5a6672>正常</color>",
                            "<b>STATUS</b>   <color=#5a6672>OK</color>"), 26);

        AddToggleRow(M("toggle.enabled", "启用本 DLC", "Enable this DLC"), _pEnabled,
            M("toggle.enabled.desc", "关闭后全部功能不加载", "When off, no features are loaded"),
            v => { _pEnabled = v; });

        AddToggleRow(M("toggle.isolate", "屏蔽其他 MOD", "Isolate other mods"), _pIsolate,
            M("toggle.isolate.desc", "只加载本 DLC 与 KrokMP", "Load only this DLC and KrokMP"),
            v => { _pIsolate = v; });

        AddActionRow(M("isolated", "已屏蔽", "Isolated"),
            ModState.IsIsolatedNow
                ? $"<color=#4fc3a1>{ModState.IsolatedFileCount()} " + Lang.T("个", "file(s)") + "</color>"
                : UiText.T("common.none", Lang.T("无", "None")), "", null);

        AddToggleRow(M("debug", "调试功能", "Debug tools"), ModState.DebugTools,
            M("debug.desc", "F6/F7/F11/F12 直接给物品；F5 特效测试",
                            "F6/F7/F11/F12 spawn items; F5 effects test"),
            v =>
            {
                ModState.DebugTools = v;
                try
                {
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.Bind("General", "DebugTools", v, "启用调试功能").Value = v;
                        Plugin.Cfg.Save();
                    }
                }
                catch { }
            });

        AddActionRow(M("othermods", "其他 MOD", "Other mods"), "", ModState.OtherModNames(), null);

        AddActionRow(M("write", "写入配置", "Write config"),
            dirty ? M("write.pending", "<color=#d8a83e>待写入</color>", "<color=#d8a83e>PENDING</color>")
                  : M("write.none", "无需写入", "No change"),
            M("write.hint", "写完后从 Steam 手动重启游戏", "Restart the game from Steam after writing"), () =>
            {
                ModState.ApplyAndTellUser(_pEnabled, _pIsolate, ModState.DebugTools);
                _pEnabled = ModState.Enabled;
                _pIsolate = ModState.Isolate;
                Refresh();
            });

        AddLabel(M("section.general", "<b>通用</b>   <color=#5a6672>对所有存档生效</color>",
                                      "<b>GENERAL</b>   <color=#5a6672>applies to every save</color>"), 26);
        AddActionRow(M("language", "文本语言", "Text language"),
            "<color=#8fd6c4>" + Lang.ModeLabel() + "</color>",
            M("language.desc", "点击切换：跟随游戏 → 中文 → English（完整生效需重启游戏）",
                               "Click to cycle: Follow game → 中文 → English (restart for full effect)"),
            () =>
            {
                Lang.CycleMode();
                Refresh();
                if (_footer != null)
                    _footer.text = string.Format(
                        M("language.saved", "语言已改为 {0} · 重启游戏后全部生效",
                                            "Language set to {0} · restart the game for full effect"),
                        Lang.ModeLabel());
            });

        AddLabel(M("section.modules", "<b>模块</b>   <color=#5a6672>点击切换</color>",
                                      "<b>MODULES</b>   <color=#5a6672>click to toggle</color>"), 26);
        foreach (var m in _modules)
        {
            var mod = m;
            TextMeshProUGUI label, value;
            var btn = AddRow(_content, 56f, out label, out value);
            label.text = $"{mod.name}\n<size=13><color=#5a6672>{mod.desc}</color></size>";
            label.enableWordWrapping = true;
            bool on = false;
            try { on = mod.get(); } catch { }
            value.text = on
                ? "<color=#4fc3a1>" + UiText.T("common.on", Lang.T("开", "ON")) + "</color>"
                : "<color=#d05a5a>" + UiText.T("common.off", Lang.T("关", "OFF")) + "</color>";
            btn.onClick.AddListener(() =>
            {
                try
                {
                    bool now = mod.get();
                    mod.set(!now);
                    if (Plugin.Cfg != null) Plugin.Cfg.Bind("Modules", mod.key, !now, mod.desc).Value = !now;
                    else Plugin.Log.LogWarning("[STEMFishDLC] 配置对象不可用，开关无法持久化");
                    Plugin.Log.LogInfo($"[STEMFishDLC] 模块 {mod.key} = {(!now ? "开" : "关")}");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 切换 {mod.key} 失败: {e.Message}"); }
                Refresh();
            });
        }
    }

    private static bool _pendingInit;
    private static void SyncPendingOnce()
    {
        if (_pendingInit) return;
        _pendingInit = true;
        SyncPending();
    }

    // ==========================================================================
    //  页 1：热键（普通 / 调试 分开）
    // ==========================================================================
    private static void BuildKeyTab()
    {
        AddLabel(M("section.hotkeys", "<b>热键</b>   <color=#5a6672>游戏内随时可用</color>",
                                      "<b>HOTKEYS</b>   <color=#5a6672>available in-game</color>"), 26);
        AddKeyRow(M("key.terminal.name", "战术终端", "Tactical terminal"), "F8",
            M("key.terminal.desc", "打开/关闭战术终端（也可以点左下角图标）",
                                   "Open/close the tactical terminal (or click the icon in the lower left)"), true);
        AddKeyRow(M("key.voice.name", "自言自语开关", "Voice toggle"), "F9",
            M("key.voice.desc", "临时关掉角色主动吐槽", "Temporarily disable the inner monologue"), true);
        AddKeyRow(M("key.expand.name", "台词扩充开关", "Base-line toggle"), "F10",
            M("key.expand.desc", "临时关掉原版情境台词扩充", "Temporarily disable the base-line expansion"), true);

        AddLabel(M("section.debug", "<b>调试</b>   <color=#5a6672>默认关闭；打开后下列按键才会生效</color>",
                                    "<b>DEBUG</b>   <color=#5a6672>off by default; the keys below only work when enabled</color>"), 26);

        AddToggleRow(M("key.debug.name", "启用调试", "Enable debug"), ModState.DebugTools,
            M("key.debug.desc", "调试热键只在本机测试时用，正式游玩建议关闭",
                                "Debug keys are for local testing; keep them off for normal play"),
            v =>
            {
                ModState.DebugTools = v;
                try
                {
                    if (Plugin.Cfg != null)
                    {
                        Plugin.Cfg.Bind("General", "DebugTools", v, "启用调试功能").Value = v;
                        Plugin.Cfg.Save();
                    }
                }
                catch { }
            });

        bool dbg = ModState.DebugTools;
        AddKeyRow(M("key.jammer.name", "屏蔽器", "Jammer"), "F3",
            M("key.jammer.desc", "给一个屏蔽器（等同太阳耀斑 + 拆芯片抉择）",
                                 "Spawn a jammer (solar flare + chip choice)"), dbg);
        AddKeyRow(M("key.unstick.name", "解除禁足", "Unstick"), "F2",
            M("key.unstick.desc", "剧情卡住 / forceNoMovement 未复位时手动解除",
                                  "Clear a stuck story state / forceNoMovement"), dbg);
        AddKeyRow(M("key.mdrz9.name", "给予 MDRZ9", "Give MDRZ9"), "F6",
            M("key.mdrz9.desc", "直接给一把 MDRZ9", "Spawn an MDRZ9"), dbg);
        AddKeyRow(M("key.ammo.name", "给予弹药", "Give ammo"), "F7",
            M("key.ammo.desc", "两个 7.62 弹匣 + 20 发子弹（自动进弹挂甲）",
                               "Two 7.62 magazines + 20 rounds (auto into the rig)"), dbg);
        AddKeyRow(M("key.carrier.name", "给予弹挂甲", "Give chest rig"), "F11",
            M("key.carrier.desc", "战术弹挂甲", "Tactical chest rig"), dbg);
        AddKeyRow(M("key.helmet.name", "给予头盔", "Give helmet"), "F12",
            M("key.helmet.desc", "战术头盔", "Tactical helmet"), dbg);

        AddLabel(M("debug.hint", "<color=#5a6672>提示：F6~F12 里除 F8/F9/F10 之外的按键都受「启用调试」控制。</color>",
                                 "<color=#5a6672>Note: F6~F12 (except F8/F9/F10) require Debug tools to be enabled.</color>"), 30);
    }

    private static void AddKeyRow(string name, string key, string desc, bool enabled)
    {
        TextMeshProUGUI label, value;
        var btn = AddRow(_content, 40f, out label, out value);
        string col = enabled ? "e6efef" : "4a5560";
        label.text = $"<color=#{col}>{name}</color>\n<size=13><color=#5a6672>{desc}</color></size>";
        value.text = enabled ? $"<color=#8fd6c4>{key}</color>" : $"<color=#4a5560>{key}</color>";
        btn.interactable = false;
    }

    // ==========================================================================
    //  页 2：存档（备注 + 层数 + 备份）
    // ==========================================================================
    private static string SaveDir => Application.persistentDataPath;
    private static string BackupDir => Path.Combine(SaveDir, "STEMFishDLC_Backups");
    private static string NotePath => Path.Combine(DLCDataDir, "save_notes.json");
    private static string DLCDataDir => Path.Combine(Paths.PluginPath, "STEMFishDLC");

    private static void BuildSaveTab()
    {
        AddLabel(M("section.saves", "<b>存档</b>   <color=#5a6672>点一行可以重命名/删除；备注只记在本地，不改存档内容</color>",
                                      "<b>SAVES</b>   <color=#5a6672>click a row to rename/delete; notes are local only</color>"), 26);

        // ---- 当前存档（v0.18.0 起走命名槽位）----
        string cur = SaveSlots.HasCurrent
            ? $"<color=#8fd6c4>{SaveSlots.CurrentName}</color>   <size=12><color=#5a6672>{SaveSlots.CurrentId}</color></size>"
            : M("currentsave.none", "<color=#c98a4a>未选（急速下坠）</color>   <size=12><color=#5a6672>保存时会现场新建一个</color></size>",
                                    "<color=#c98a4a>None (Freefall)</color>   <size=12><color=#5a6672>a new slot will be created on save</color></size>");
        AddActionRow(M("currentsave", "当前存档", "Current save"), cur,
            string.Format(M("currentsave.desc", "存档键 {0} · 存档点自动存档 {1}", "Save key {0} · checkpoint auto-save {1}"),
                SaveSlots.SaveKey,
                UiText.T(SaveSlots.AutoSaveAtCheckpoint ? "common.on" : "common.off",
                         Lang.T(SaveSlots.AutoSaveAtCheckpoint ? "开" : "关",
                                SaveSlots.AutoSaveAtCheckpoint ? "ON" : "OFF"))), () =>
        {
            SaveSlots.SaveCurrent(false);
            if (_footer != null) _footer.text = SaveSlots.HasCurrent
                ? string.Format(M("currentsave.saved", "已保存到「{0}」", "Saved to \"{0}\""), SaveSlots.CurrentName)
                : M("currentsave.notinrun", "没在局内", "Not in a run");
            Refresh();
        });

        var saves = new List<string>();
        try
        {
            if (Directory.Exists(SaveDir))
                foreach (var f in Directory.GetFiles(SaveDir, "*.sv", SearchOption.AllDirectories))
                {
                    // 命名槽位是我们的文件，不算"原版存档"，别在这里混着列
                    if (f.Replace('/', '\\').Contains("STEMFishDLC_Saves")) continue;
                    saves.Add(f);
                }
        }
        catch { }

        if (saves.Count == 0) AddLabel(M("saves.empty", "<color=#5a6672>没有找到存档文件。</color>",
                                                       "<color=#5a6672>No save files found.</color>"), 30);

        var notes = LoadNotes();
        saves.Sort();
        saves.Reverse();

        foreach (var f in saves)
        {
            var fi = new FileInfo(f);
            string rel = MakeRel(f);
            string note = "";
            notes.TryGetValue(rel, out note);

            string title = string.IsNullOrEmpty(note) ? rel : $"{note}   <size=13><color=#5a6672>{rel}</color></size>";
            string desc = $"{fi.LastWriteTime:yyyy-MM-dd HH:mm} · {fi.Length / 1024} KB · {LayerText(f)}";

            var file = f;
            var relKey = rel;
            var noteVal = note ?? "";
            AddActionRow(title, "<color=#8fd6c4>" + UiText.T("common.rename", Lang.T("改名", "Rename")) + "</color>",
                desc, () => ShowSaveDialog(file, relKey, noteVal));
        }

        AddLabel(M("section.actions", "<b>操作</b>", "<b>ACTIONS</b>"), 26);

        AddActionRow(M("backup", "备份当前所有存档", "Back up all saves"),
            "<color=#8fd6c4>" + UiText.T("common.browse", Lang.T("复制", "Copy")) + "</color>",
            M("backup.desc", "复制到 STEMFishDLC_Backups，不改原存档", "Copy to STEMFishDLC_Backups; originals untouched"), () =>
        {
            int n = BackupAll();
            if (_footer != null) _footer.text = n > 0
                ? string.Format(M("backup.done", "已备份 {0} 个存档。", "Backed up {0} saves."), n)
                : M("backup.fail", "备份失败，看日志。", "Backup failed; check the log.");
            Refresh();
        });

        AddActionRow(M("opendir", "打开存档目录", "Open save folder"),
            "<color=#8fd6c4>" + UiText.T("common.browse", Lang.T("浏览", "Browse")) + "</color>", SaveDir, () =>
        {
            try { Application.OpenURL("file://" + SaveDir.Replace("\\", "/")); } catch { }
        });

        AddActionRow(M("openbackup", "打开备份目录", "Open backup folder"),
            "<color=#8fd6c4>" + UiText.T("common.browse", Lang.T("浏览", "Browse")) + "</color>", BackupDir, () =>
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                Application.OpenURL("file://" + BackupDir.Replace("\\", "/"));
            }
            catch { }
        });

        // 备份列表
        var backups = new List<string>();
        try
        {
            if (Directory.Exists(BackupDir)) backups.AddRange(Directory.GetFiles(BackupDir, "*.sv"));
        }
        catch { }

        if (backups.Count > 0)
        {
            AddLabel(M("section.backups", "<b>已有备份</b>   <color=#5a6672>点一行还原（会先把当前存档再备份一次）</color>",
                                          "<b>BACKUPS</b>   <color=#5a6672>click a row to restore (the current save is backed up first)</color>"), 26);
            backups.Sort();
            backups.Reverse();
            foreach (var b in backups)
            {
                var file = b;
                var name = Path.GetFileName(file);
                AddActionRow(name, "<color=#8fd6c4>" + Lang.T("还原", "Restore") + "</color>",
                    File.GetLastWriteTime(file).ToString("MM-dd HH:mm"), () => Restore(file));
            }
        }
    }

    // 备注输入行（TMP_InputField，支持中文 IME）
    private static void AddNoteEditor(string rel, string initial)
    {
        var rt = NewRect("NoteRow", _content);
        rt.sizeDelta = new Vector2(0, 40f);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = 40f; le.minHeight = 40f;

        var cap = NewText("Cap", rt, M("note", "备注", "Note"), 15, DimTxt);
        var crt = cap.rectTransform;
        crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(0, 1);
        crt.pivot = new Vector2(0, 0.5f);
        crt.sizeDelta = new Vector2(56, 0);
        crt.anchoredPosition = new Vector2(10, 0);
        cap.alignment = TextAlignmentOptions.Left;

        var bg = AddImage(NewRect("InputBg", rt).gameObject, new Color(0.13f, 0.16f, 0.19f, 1f));
        var brt = bg.rectTransform;
        brt.anchorMin = new Vector2(0, 0); brt.anchorMax = new Vector2(1, 1);
        brt.offsetMin = new Vector2(66, 4); brt.offsetMax = new Vector2(-10, -4);

        var area = NewRect("TextArea", brt);
        area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(8, 2); area.offsetMax = new Vector2(-8, -2);
        area.gameObject.AddComponent<RectMask2D>();

        var txt = NewText("Text", area, "", 15, Body);
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        txt.enableWordWrapping = false;
        txt.alignment = TextAlignmentOptions.Left;
        txt.raycastTarget = false;

        var ph = NewText("Placeholder", area, M("note.placeholder", "点击输入备注…", "Click to add a note..."), 15, new Color(0.42f, 0.47f, 0.52f, 1f));
        var prt = ph.rectTransform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
        ph.enableWordWrapping = false;
        ph.alignment = TextAlignmentOptions.Left;
        ph.raycastTarget = false;

        try
        {
            var input = rt.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;
            input.textViewport = area;
            input.textComponent = txt;
            input.placeholder = ph;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 60;
            input.text = initial ?? "";
            input.onEndEdit.AddListener(v =>
            {
                SaveNote(rel, v);
                if (_footer != null) _footer.text = string.Format(M("note.saved", "备注已保存：{0}", "Note saved: {0}"), rel);
            });
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 创建备注输入框失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  存档弹窗（重命名 / 删除）
    //  ---------------------------------------------------------------------------
    //  作者要求：重命名必须点按钮才弹出来，不要每行常驻一个输入框。
    //  所以列表里每行只留一个「重命名」入口，点开这个浮层再改。
    //  删除也收进浮层里（还要点两次确认，避免手滑删档）。
    // ==========================================================================
    private static GameObject _saveDlg;
    private static string _pendingDelete;

    private static void CloseSaveDialog()
    {
        if (_saveDlg == null) return;
        try { UnityEngine.Object.Destroy(_saveDlg); } catch { }
        _saveDlg = null;
        _pendingDelete = null;
    }

    private static void ShowSaveDialog(string fullPath, string rel, string note)
    {
        CloseSaveDialog();
        if (_root == null) return;

        var root = NewRect("SaveDlg", _root.transform);
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;
        root.SetAsLastSibling();

        var mask = AddImage(root.gameObject, new Color(0f, 0f, 0f, 0.70f));
        mask.raycastTarget = true;      // 吃掉点击，别让下面板子被误触

        var panel = NewRect("Box", root);
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(460f, 230f);
        panel.anchoredPosition = Vector2.zero;
        AddImage(panel.gameObject, new Color(0.06f, 0.075f, 0.09f, 0.99f));

        // 顶部一条青色细边（和面板风格一致）
        var edge = NewRect("Edge", panel);
        edge.anchorMin = new Vector2(0f, 1f); edge.anchorMax = new Vector2(1f, 1f);
        edge.pivot = new Vector2(0.5f, 1f);
        edge.sizeDelta = new Vector2(0f, 2f);
        edge.anchoredPosition = Vector2.zero;
        AddImage(edge.gameObject, new Color(0.28f, 0.72f, 0.66f, 0.9f));

        var title = NewText("Title", panel, M("rename.title", "重命名存档", "Rename save"), 20, new Color(0.62f, 0.94f, 0.88f, 1f));
        var trt = title.rectTransform;
        trt.anchorMin = trt.anchorMax = new Vector2(0f, 1f);
        trt.pivot = new Vector2(0f, 1f);
        trt.sizeDelta = new Vector2(400f, 28f);
        trt.anchoredPosition = new Vector2(24f, -16f);
        title.alignment = TextAlignmentOptions.Left;

        var sub = NewText("Sub", panel, rel, 13, new Color(0.42f, 0.47f, 0.52f, 1f));
        var srt = sub.rectTransform;
        srt.anchorMin = srt.anchorMax = new Vector2(0f, 1f);
        srt.pivot = new Vector2(0f, 1f);
        srt.sizeDelta = new Vector2(400f, 18f);
        srt.anchoredPosition = new Vector2(24f, -44f);
        sub.alignment = TextAlignmentOptions.Left;

        // ---- 输入框 ----
        var input = MakeInput(panel, new Vector2(24f, -76f), new Vector2(412f, 42f), note);

        // ---- 按钮 ----
        DlgButton(panel, 24f, 120f, M("rename.note", "保存备注", "Save note"), new Color(0.13f, 0.34f, 0.31f, 1f), () =>
        {
            string v = input != null ? input.text : "";
            SaveNote(rel, v);
            if (_footer != null) _footer.text = $"备注已保存：{rel}";
            CloseSaveDialog();
            Refresh();
        });

        DlgButton(panel, 156f, 120f, M("delete.title", "删除存档", "Delete save"), new Color(0.32f, 0.13f, 0.13f, 1f), () =>
        {
            if (_pendingDelete != fullPath)
            {
                _pendingDelete = fullPath;
                if (_footer != null) _footer.text = M("delete.confirm", "再点一次「删除存档」确认（不可恢复）", "Click \"Delete save\" again to confirm (cannot be undone)");
                return;
            }
            DeleteSave(fullPath);
            CloseSaveDialog();
            Refresh();
        });

        DlgButton(panel, 288f, 100f, UiText.T("common.cancel", Lang.T("取消", "Cancel")), new Color(0.14f, 0.16f, 0.19f, 1f), CloseSaveDialog);

        _saveDlg = root.gameObject;
    }

    private static void DlgButton(RectTransform parent, float x, float w, string label, Color bg, Action act)
    {
        var rt = NewRect("Btn_" + label, parent);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(w, 40f);
        rt.anchoredPosition = new Vector2(x, 18f);

        var img = AddImage(rt.gameObject, bg);
        var txt = NewText("L", rt, label, 16, new Color(0.92f, 0.96f, 0.96f, 1f));
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        txt.alignment = TextAlignmentOptions.Center;

        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() => { try { act(); } catch (Exception e) { Plugin.Log.LogWarning(e.Message); } });
    }

    private static TMP_InputField MakeInput(RectTransform parent, Vector2 pos, Vector2 size, string initial)
    {
        var bg = NewRect("Input", parent);
        bg.anchorMin = bg.anchorMax = new Vector2(0f, 1f);
        bg.pivot = new Vector2(0f, 1f);
        bg.sizeDelta = size;
        bg.anchoredPosition = pos;
        var img = AddImage(bg.gameObject, new Color(0.13f, 0.16f, 0.19f, 1f));

        var area = NewRect("TextArea", bg);
        area.anchorMin = Vector2.zero; area.anchorMax = Vector2.one;
        area.offsetMin = new Vector2(8f, 2f); area.offsetMax = new Vector2(-8f, -2f);
        area.gameObject.AddComponent<RectMask2D>();

        var txt = NewText("Text", area, "", 15, new Color(0.92f, 0.96f, 0.96f, 1f));
        var trt = txt.rectTransform;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
        txt.enableWordWrapping = false;
        txt.alignment = TextAlignmentOptions.Left;
        txt.raycastTarget = false;

        var ph = NewText("Placeholder", area, UiText.T("newrun.placeholder", Lang.T("给它起个名字…", "Give it a name...")), 15, new Color(0.42f, 0.47f, 0.52f, 1f));
        var prt = ph.rectTransform;
        prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
        prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
        ph.enableWordWrapping = false;
        ph.alignment = TextAlignmentOptions.Left;
        ph.raycastTarget = false;

        try
        {
            var input = bg.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = img;
            input.textViewport = area;
            input.textComponent = txt;
            input.placeholder = ph;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 60;
            input.text = initial ?? "";
            // 自动聚焦，点开就能打字
            try { input.ActivateInputField(); } catch { }
            return input;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 创建输入框失败: {e.Message}");
            return null;
        }
    }

    private static void DeleteSave(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            var side = fullPath + ".json";
            if (File.Exists(side)) File.Delete(side);
            if (_footer != null) _footer.text = M("delete.done", "已删除：", "Deleted: ") + Path.GetFileName(fullPath);
            Plugin.Log.LogInfo($"[STEMFishDLC] 已删除存档 {fullPath}");
        }
        catch (Exception e)
        {
            if (_footer != null) _footer.text = M("delete.fail", "删除失败：", "Delete failed: ") + e.Message;
            Plugin.Log.LogWarning($"[STEMFishDLC] 删除存档失败: {e.Message}");
        }
    }

    // 存档路径 → 相对路径（当 key）
    private static string MakeRel(string full)
    {
        try
        {
            var root = SaveDir.TrimEnd('/', '\\');
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
        }
        catch { }
        return Path.GetFileName(full);
    }

    // 读 save.sv.json 拿层数（biome）+ 最后游玩时间
    private static string LayerText(string svPath)
    {
        try
        {
            var side = svPath + ".json";
            if (!File.Exists(side)) return M("layer.unknown", "层数未知", "Layer unknown");
            var root = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(side));
            if (root == null) return M("layer.unknown", "层数未知", "Layer unknown");

            int biome = -1;
            if (root.TryGetValue("biome", out var bv) && bv != null)
                int.TryParse(bv.ToString(), out biome);

            long ms = 0;
            if (root.TryGetValue("lastPlayedAtUnixMs", out var lv) && lv != null)
                long.TryParse(lv.ToString(), out ms);

            string layer = biome >= 0 ? string.Format(M("layer.n", "第 {0} 层", "Layer {0}"), biome + 1)
                                       : M("layer.unknown", "层数未知", "Layer unknown");
            if (ms > 0)
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                layer += $" · {t:MM-dd HH:mm}";
            }
            return layer;
        }
        catch { return M("layer.unknown", "层数未知", "Layer unknown"); }
    }

    private static Dictionary<string, string> _notesCache;
    private static Dictionary<string, string> LoadNotes()
    {
        if (_notesCache != null) return _notesCache;
        _notesCache = new Dictionary<string, string>();
        try
        {
            if (File.Exists(NotePath))
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(NotePath));
                if (d != null) _notesCache = d;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 读取存档备注失败: {e.Message}"); }
        return _notesCache;
    }

    private static void SaveNote(string rel, string text)
    {
        try
        {
            var notes = LoadNotes();
            if (string.IsNullOrEmpty(text)) notes.Remove(rel);
            else notes[rel] = text;

            Directory.CreateDirectory(DLCDataDir);
            File.WriteAllText(NotePath, JsonConvert.SerializeObject(notes, Formatting.Indented));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 保存备注失败: {e.Message}"); }
    }

    private static int BackupAll()
    {
        int n = 0;
        try
        {
            Directory.CreateDirectory(BackupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            foreach (var f in Directory.GetFiles(SaveDir, "*.sv", SearchOption.AllDirectories))
            {
                var tag = Path.GetFileName(Path.GetDirectoryName(f));
                if (string.IsNullOrEmpty(tag)) tag = "root";
                File.Copy(f, Path.Combine(BackupDir, $"{stamp}_{tag}_{Path.GetFileName(f)}"), true);
                n++;
            }
            Plugin.Log.LogInfo($"[STEMFishDLC] 已备份 {n} 个存档 → {BackupDir}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 备份失败: {e.Message}"); }
        return n;
    }

    private static void Restore(string backupFile)
    {
        try
        {
            BackupAll();
            var name = Path.GetFileName(backupFile);
            int idx = name.LastIndexOf("_save.sv", StringComparison.Ordinal);
            string target = idx >= 0 ? name.Substring(idx + 1) : name;
            int done = 0;
            foreach (var f in Directory.GetFiles(SaveDir, target, SearchOption.AllDirectories))
            {
                File.Copy(backupFile, f, true);
                done++;
            }
            if (_footer != null)
                _footer.text = done > 0
                    ? string.Format(M("restore.done", "已还原到 {0} 处（原存档已先备份）。重启游戏生效。",
                                                       "Restored {0} file(s); the current save was backed up first. Restart the game."), done)
                    : M("restore.none", "没找到对应存档，未改动。", "No matching save found; nothing changed.");
            Plugin.Log.LogInfo($"[STEMFishDLC] 还原 {backupFile} → {done} 处");
            Refresh();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 还原失败: {e.Message}"); }
    }

    // ==========================================================================
    //  小工具
    // ==========================================================================
    private static void AddLabel(string text, float height)
    {
        var rt = NewRect("Label", _content);
        rt.sizeDelta = new Vector2(0, height);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (DlcPanel.Font != null) t.font = DlcPanel.Font;
        t.text = text;
        t.fontSize = 16;
        t.color = Body;
        t.raycastTarget = false;
        t.enableWordWrapping = true;
        t.alignment = TextAlignmentOptions.Left;
    }

    private static Button AddRow(Transform parent, float height, out TextMeshProUGUI label, out TextMeshProUGUI value)
    {
        var rt = NewRect("Row", parent);
        rt.sizeDelta = new Vector2(0, height);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight = height;

        var img = AddImage(rt.gameObject, RowBg);
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;

        label = NewText("Label", rt, "", 17, Body);
        var lrt = label.rectTransform;
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(10, 0); lrt.offsetMax = new Vector2(-100, 0);
        label.alignment = TextAlignmentOptions.Left;

        value = NewText("Value", rt, "", 16, DimTxt);
        var vrt = value.rectTransform;
        vrt.anchorMin = new Vector2(1, 0); vrt.anchorMax = new Vector2(1, 1);
        vrt.pivot = new Vector2(1, 0.5f);
        vrt.sizeDelta = new Vector2(92, 0);
        vrt.anchoredPosition = new Vector2(-10, 0);
        value.alignment = TextAlignmentOptions.Right;
        return btn;
    }

    // 开关行：点击取反 → 立刻回调（写内存值，重启生效的那类由调用方自己写 cfg）
    private static void AddToggleRow(string name, bool initial, string desc, Action<bool> onChange)
    {
        TextMeshProUGUI label, value;
        // ★ 行高必须跟着"是否两行"走：固定 44px 塞两行文字 → 上下行重叠
        bool twoLine = !string.IsNullOrEmpty(desc);
        var btn = AddRow(_content, twoLine ? 58f : 42f, out label, out value);
        label.text = twoLine
            ? $"{name}\n<size=13><color=#5a6672>{desc}</color></size>"
            : name;
        label.enableWordWrapping = twoLine;

        bool state = initial;
        value.text = state ? "<color=#4fc3a1>开</color>" : "<color=#d05a5a>关</color>";

        btn.onClick.AddListener(() =>
        {
            state = !state;
            try { onChange(state); } catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] {name} 切换失败: {e.Message}"); }
            Refresh();
        });
    }

    private static void AddActionRow(string name, string valueText, string desc, Action onClick)
    {
        TextMeshProUGUI label, value;
        bool twoLine = !string.IsNullOrEmpty(desc);
        var btn = AddRow(_content, twoLine ? 58f : 42f, out label, out value);
        label.text = twoLine
            ? $"{name}\n<size=13><color=#5a6672>{desc}</color></size>"
            : name;
        label.enableWordWrapping = twoLine;
        value.text = valueText;
        if (onClick != null) btn.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Plugin.Log.LogWarning(e.Message); } });
        else btn.interactable = false;
    }

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
        if (DlcPanel.Font != null) t.font = DlcPanel.Font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.raycastTarget = false;
        return t;
    }
}
