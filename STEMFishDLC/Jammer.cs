using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  Jammer - 剧情道具「屏蔽器」
//  ---------------------------------------------------------------------------
//  v0.20 起它**不再是一件可以随手用的道具**，而是第 10 层的一个岔路口：
//
//    · 平时（任何其它层）右键 → 没有反应，不消耗
//    · 身上带着它下到第 10 层（拿到货物那一层）→ 进入该层后立刻定住玩家，
//      播几段自言自语，然后把「拆 / 不拆」摆在你面前
//        · 拆   → 中度脑损伤 + 头部出血 + 受伤音效，芯片取出来，
//                 runSettings["unchipped"]=true → 此后永久无芯片（自由结局线）
//        · 不拆 → 芯片留着，屏蔽器还在兜里；**本层之内**还能再手动按一次
//    · 一旦离开第 10 层，屏蔽器彻底失效（芯片换层就换密钥，它匹配不上）
//
//  为什么要定住玩家：这是个不可逆的选择，得让人站着看完再点。
//  实现上直接借游戏自己的暂停（`PlayerCamera.SetTimeScale(Paused)`），
//  时间停住 = 物理停住 = 谁也动不了，不用去 patch 输入。
// ============================================================================

public static class Jammer
{
    public const string Id = "jammer";

    public static bool Enabled = true;
    public static bool LootEnabled = true;
    public static int LootWeight = 2;
    public static int TriggerDepth = 10;

    private enum Phase { Idle, Lines, Choice }

    private static Phase _phase = Phase.Idle;
    private static List<string> _script;
    private static int _line;
    private static float _nextAt;

    private static bool _open;
    private static float _openAt;
    private const float AutoKeepAfter = 120f;   // 保底：别把人锁死在暂停里

    private static GameObject _root;
    private static TextMeshProUGUI _log;
    private static GameObject _btnRemove, _btnKeep;

    private static Item _item;
    private static Body _body;
    private static WorldGeneration _world;
    private static int _forcedFor = int.MinValue;   // 已经播过开场白的层（每次换世界重置）

    private static PlayerCamera.SpeedType _prevSpeed = PlayerCamera.SpeedType.Normal;
    private static bool _touchedSpeed;
    private static WorldGeneration _uiSynced;
    private static bool _poolInjected;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Items", "Jammer", true,
                "启用「屏蔽器」（第 10 层的拆芯片抉择）").Value;
            LootEnabled = plugin.Config.Bind("Items", "JammerLoot", true,
                "屏蔽器像普通物品一样出现在战利品里（商人出售的物品 / 打碎的物资箱 / 尸体遗留）").Value;
            LootWeight = Mathf.Clamp(plugin.Config.Bind("Items", "JammerWeight", 2,
                "屏蔽器在战利品池里的权重（1 = 很稀有，5 = 常见；上限 20）").Value, 1, 20);
            TriggerDepth = Mathf.Clamp(plugin.Config.Bind("Items", "JammerDepth", 10,
                "带着屏蔽器下到第几层时触发抉择").Value, 1, 10);

            Plugin.Log.LogInfo($"[STEMFishDLC] 屏蔽器：{(Enabled ? $"第 {TriggerDepth} 层触发抉择" : "停用")} | " +
                               $"战利品 {(LootEnabled ? $"权重 {LootWeight}" : "关")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器配置读取失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  右键使用：只有第 10 层有效
    // ==========================================================================
    public static void Use(Body body, Item item)
    {
        if (!Enabled || body == null || item == null || _open) return;

        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }

        int depth = -1;
        try { if (w != null) depth = w.biomeDepth; } catch { }

        if (w == null || depth != TriggerDepth)
        {
            // 不可用：既不消耗也不打开界面（走完第 10 层它就彻底没用了）
            SaveSlots.Toast(UiText.T("jammer.wronglayer", Lang.T("没有反应。它只对一层有效，而这里不是那一层。",
                "Nothing happens. It only works on one layer, and this is not that layer.")));
            Plugin.Log.LogInfo($"[STEMFishDLC] 屏蔽器：当前层 {depth} 不是可用层，无反应");
            return;
        }

        _body = body;
        _item = item;
        OpenPanel(false);
    }

    // ==========================================================================
    //  每帧
    // ==========================================================================
    public static void Tick()
    {
        InjectPool();
        SyncChipUi();

        if (!Enabled) return;

        if (_phase == Phase.Lines) { PumpLines(); return; }

        if (_phase == Phase.Choice)
        {
            if (Time.unscaledTime - _openAt > AutoKeepAfter)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 屏蔽器界面超时，按「不拆」处理");
                ChooseKeep();
            }
            return;
        }

        TryTrigger();
    }

    private static void TryTrigger()
    {
        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }

        if (w == null)
        {
            if (_world != null) { _world = null; _forcedFor = int.MinValue; }
            return;
        }
        if (!ReferenceEquals(w, _world))          // 换局 / 读档：标记归零
        {
            _world = w;
            _forcedFor = int.MinValue;
        }

        int depth;
        try { depth = w.biomeDepth; } catch { return; }
        if (depth != TriggerDepth) return;
        if (_forcedFor == depth) return;

        // 等世界生成完、别卡在生成/过场里
        try { if (w.timeSinceFinishedGeneration < 4f) return; } catch { return; }
        try { if (!w.worldExists) return; } catch { return; }
        if (Plugin.IsPaused) return;
        try { if (MinigameBase.main != null && MinigameBase.main.currentMinigame != null) return; } catch { }

        Body body = null;
        try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
        if (body == null) return;

        Item it = null;
        try { if (!body.FindByIdThorough(Id, out it) || it == null) return; } catch { return; }

        _body = body;
        _item = it;
        _forcedFor = depth;

        Plugin.Log.LogInfo("[STEMFishDLC] 屏蔽器：第 10 层开场，定住玩家并开始抉择");
        OpenPanel(true);
    }

    // ==========================================================================
    //  面板
    // ==========================================================================
    private static void OpenPanel(bool withLines)
    {
        if (_open) return;

        _open = true;
        _openAt = Time.unscaledTime;
        _line = 0;
        _nextAt = Time.unscaledTime + 0.5f;

        // 给档案用的状态位：抉择一旦开始就算「没拆」，
        // 真拆了会被 WorldGeneration.unchipped 盖掉（LoreData.ChipState 先判 unchipped）。
        try
        {
            if (WorldGeneration.runSettings != null)
                WorldGeneration.runSettings["stemfish_jammer_declined"] = true;
        }
        catch { }

        if (withLines)
        {
            _script = FishVoice.Sequence("jammer");
            if (_script == null || _script.Count == 0) _script = FallbackScript();
            _phase = Phase.Lines;
        }
        else
        {
            _script = null;
            _phase = Phase.Choice;
        }

        PauseGame();
        BuildUI();

        if (withLines) SetLog("");
        else
        {
            SetLog(UiText.T("jammer.keep.text",
                Lang.T("<color=#c9d3d8>屏蔽器还在兜里，凉的。</color>\n\n芯片还埋在后颈，离你只有一层皮的深度；你还能再按一次，可这一层不会等你太久。",
                       "<color=#c9d3d8>The jammer is still in your pocket, cold.</color>\n\nThe chip is still under your nape, one layer of skin away. You can press it once more, but this layer will not wait long.")));
            ShowButtons();
        }
    }

    private static List<string> FallbackScript()
    {
        if (Lang.IsEnglish)
        {
            return new List<string>
            {
                "When I came into this layer, the box in my pocket was still cold. The thing in my nape was hot. There was no cargo in my belly yet, but I already knew it was below.",
                "The file was clear: to shut the chip up, I need the jammer. It works on one layer only, once only, and this is that layer.",
                "Cut it out and I may not finish the climb. Keep it and I carry their voice up with me, hand over the cargo, and let them decide how much longer I live.",
                "I have lived in the lab all my life and never chosen anything for myself. They told me to stand, I stood. They told me to lie down, I lay down. They told me to go down, I went down.",
                "This time, I want to choose. Cut, or keep.",
            };
        }
        return new List<string>
        {
            "我进这一层的时候，兜里这个盒子还是凉的；后颈里那块东西在发热，肚子里还没有货，可我已经知道它就在下面。",
            "文件上写得很清楚，要让芯片闭嘴，得先有屏蔽器；它只认一层，只够一次，而这一层就是那一层。",
            "拆了它，我可能走不完剩下的台阶；不拆，我就得带着他们的声音上去，把货交到他们手里，再让他们决定我还能活多久。",
            "我在实验室里活了这么久，从来没有自己选过什么；他们让我站我就站，让我躺我就躺，让我往下走我就往下走。",
            "这一次，我想自己选。拆，还是留。",
        };
    }

    private static void PumpLines()
    {
        if (_script == null || _line >= _script.Count) { ToChoice(); return; }

        bool skip = false;
        try { skip = Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0); } catch { }

        if (!skip && Time.unscaledTime < _nextAt) return;

        AppendLog(_script[_line]);
        _line++;
        _nextAt = Time.unscaledTime + 3.2f;

        if (_line >= _script.Count) ToChoice();
    }

    private static void ToChoice()
    {
        if (_phase == Phase.Choice) return;
        _phase = Phase.Choice;
        _openAt = Time.unscaledTime;
        ShowButtons();
    }

    private static void SetLog(string text)
    {
        try { if (_log != null) _log.text = text; } catch { }
    }

    private static void AppendLog(string line)
    {
        try
        {
            if (_log == null) return;
            _log.text = string.IsNullOrEmpty(_log.text) ? line : _log.text + "\n\n" + line;
            if (line == null) return;
            try { Plugin.Log.LogInfo("[STEMFishDLC] 屏蔽器独白: " + line); } catch { }
        }
        catch { }
    }

    private static void ShowButtons()
    {
        try { if (_btnRemove != null) _btnRemove.SetActive(true); } catch { }
        try { if (_btnKeep != null) _btnKeep.SetActive(true); } catch { }
    }

    // ==========================================================================
    //  选择
    // ==========================================================================
    private static void ChooseRemove()
    {
        try
        {
            var body = _body;
            if (body == null)
            {
                try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
            }

            if (body != null)
            {
                body.brainHealth = Mathf.Max(0f, body.brainHealth - 35f);   // 中度脑损伤
                body.shock = Mathf.Max(body.shock, 20f);

                var head = FindHead(body);
                if (head != null)
                {
                    head.bleedAmount += 25f;                                 // 头部出血
                    head.pain += 30f;
                    head.skinHealth = Mathf.Max(0f, head.skinHealth - 8f);
                }

                // 受伤音效：原版的 gore 音（`DoGoreSound` 随机播 gore1..N）
                try { body.DoGoreSound(); } catch { }
            }

            WorldGeneration w = null;
            try { w = WorldGeneration.world; } catch { }
            if (w != null) w.SetUnchipped(true);

            // 永久无芯片：WorldGeneration.Start 每次建层都会读这个键
            try
            {
                if (WorldGeneration.runSettings != null) WorldGeneration.runSettings["unchipped"] = true;
                var pre = PreRunScript.instance;
                if (pre != null && pre.runSettings != null) pre.runSettings["unchipped"] = true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 写入无芯片设置失败: {e.Message}"); }

            try { if (WorldGeneration.runSettings != null) WorldGeneration.runSettings.Remove("stemfish_jammer_declined"); } catch { }
            Plugin.Log.LogInfo("[STEMFishDLC] 芯片已拆除：脑损伤 + 头部出血，永久无芯片模式");
            SaveSlots.Toast(UiText.T("jammer.cut.toast", Lang.T("芯片拆掉了。从现在起，没人给你计时了。",
                "The chip is out. No one is timing you anymore.")));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 拆除芯片失败: {e.Message}\n{e.StackTrace}");
        }

        Close(true);
    }

    private static void ChooseKeep()
    {
        try
        {
            // 芯片还在：把「无芯片」状态按回世界真实值（防止以前留下的耀斑状态挂在这）
            WorldGeneration w = null;
            try { w = WorldGeneration.world; } catch { }
            if (w != null) w.SetUnchipped(false);
        }
        catch { }

        Plugin.Log.LogInfo("[STEMFishDLC] 玩家选择保留芯片：屏蔽器留在背包（本层内仍可再用）");
        SaveSlots.Toast(UiText.T("jammer.keep.toast", Lang.T("你把屏蔽器塞回兜里。它只在这一层有用。",
                "You put the jammer back in your pocket. It only works on this layer.")));
        Close(false);
    }

    private static Limb FindHead(Body body)
    {
        try
        {
            var limbs = body.limbs;
            if (limbs == null) return null;
            for (int i = 0; i < limbs.Length; i++)
                if (limbs[i] != null && limbs[i].isHead) return limbs[i];
        }
        catch { }
        return null;
    }

    // ==========================================================================
    //  战利品池注入（商人出售的物品 / 打碎的箱子 / 尸体遗留）
    // ==========================================================================
    private static void InjectPool()
    {
        if (_poolInjected || !LootEnabled) return;
        try
        {
            var pool = ItemLootPool.pool;
            if (pool == null || pool.Count == 0) return;

            int added = 0;
            foreach (var key in new[] { "utility", "military", "container" })
            {
                if (!pool.TryGetValue(key, out var list) || list == null) continue;
                while (list.Remove(Id)) { }
                for (int i = 0; i < LootWeight; i++) list.Add(Id);
                added++;
            }

            if (added == 0)
            {
                string bestKey = null;
                int best = 0;
                foreach (var kv in pool)
                    if (kv.Value != null && kv.Value.Count > best) { best = kv.Value.Count; bestKey = kv.Key; }
                if (bestKey != null)
                {
                    var list = pool[bestKey];
                    while (list.Remove(Id)) { }
                    for (int i = 0; i < LootWeight; i++) list.Add(Id);
                    added = 1;
                    Plugin.Log.LogInfo($"[STEMFishDLC] 屏蔽器已注入战利品池 '{bestKey}'（兜底，{best} 项）");
                }
            }
            else Plugin.Log.LogInfo($"[STEMFishDLC] 屏蔽器已注入 {added} 个战利品池（每个重复 {LootWeight} 次）");

            if (added > 0) _poolInjected = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器池注入失败: {e.Message}");
            _poolInjected = true;
        }
    }

    private static void SyncChipUi()
    {
        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { return; }
        if (w == null) { _uiSynced = null; return; }
        if (ReferenceEquals(w, _uiSynced)) return;
        _uiSynced = w;
        try { w.SetUnchipped(WorldGeneration.unchipped); } catch { }
    }

    // ==========================================================================
    //  暂停 / 恢复
    // ==========================================================================
    private static void PauseGame()
    {
        try
        {
            var pc = PlayerCamera.main;
            if (pc == null) return;
            _prevSpeed = pc.curTimeScale;
            _touchedSpeed = true;
            pc.SetTimeScale(PlayerCamera.SpeedType.Paused, false, true);
        }
        catch { }
    }

    private static void ResumeGame()
    {
        try
        {
            if (!_touchedSpeed) return;
            _touchedSpeed = false;
            var pc = PlayerCamera.main;
            if (pc == null) return;
            var back = _prevSpeed;
            if (back == PlayerCamera.SpeedType.Paused) back = PlayerCamera.SpeedType.Normal;
            pc.SetTimeScale(back, false, true);
        }
        catch { }
    }

    private static void Close(bool consume)
    {
        try
        {
            if (consume && _item != null)
            {
                try { UnityEngine.Object.Destroy(_item.gameObject); } catch { }
            }
        }
        catch { }

        _item = null;
        _body = null;
        _open = false;
        _phase = Phase.Idle;
        _script = null;

        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;

        ResumeGame();
    }

    // ==========================================================================
    //  UI（自绘，半透明黑 + 扁平，和 MOD 面板同一套风格）
    // ==========================================================================
    /// <summary>
    /// 强制收掉屏蔽器覆盖层（全屏 0.72 黑 + GraphicRaycaster）。
    /// 如果玩家在序列播到一半时退出到主菜单，_root 是 DontDestroyOnLoad 的，
    /// 留在那里就会「整屏变暗 + 点什么都没反应」。
    /// </summary>
    public static void ForceHide()
    {
        try
        {
            if (_root == null) return;
            UnityEngine.Object.Destroy(_root);
            _root = null;
            _phase = Phase.Idle;
            _script = null;
            ResumeGame();
            Plugin.Log.LogInfo("[STEMFishDLC] 屏蔽器覆盖层已强制收起（回主菜单）");
        }
        catch { }
    }

    private static void BuildUI()
    {
        try
        {
            _root = new GameObject("STEMFishDLC_Jammer", typeof(RectTransform));
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30500;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _root.AddComponent<GraphicRaycaster>();

            var dim = NewImage(_root.transform, "Dim", new Color(0f, 0f, 0f, 0.72f));
            dim.raycastTarget = true;

            var panel = NewImage(_root.transform, "Panel", new Color(0.055f, 0.070f, 0.085f, 0.985f));
            panel.raycastTarget = true;
            var prt = panel.rectTransform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(860f, 620f);

            var bar = NewImage(prt, "Bar", new Color(0.30f, 0.81f, 1.00f, 1f));
            SetRect(bar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(3f, 620f));

            var title = MakeText(prt, "Title", UiText.T("jammer.title", Lang.T("第 10 层", "LAYER 10")), 24f, new Color(0.87f, 0.92f, 0.94f, 1f));
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -20f), new Vector2(500f, 32f));

            var hint = MakeText(prt, "Hint", UiText.T("jammer.hint", Lang.T("空格 / 左键 跳过", "SPACE / LMB TO SKIP")), 13f, new Color(0.42f, 0.50f, 0.55f, 1f));
            hint.alignment = TextAlignmentOptions.TopRight;
            SetRect(hint.rectTransform, new Vector2(1f, 1f), new Vector2(-28f, -24f), new Vector2(240f, 24f));

            _log = MakeText(prt, "Log", "", 17f, new Color(0.78f, 0.84f, 0.87f, 1f));
            _log.enableWordWrapping = true;
            _log.lineSpacing = 6f;
            SetRect(_log.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -70f), new Vector2(804f, 420f));

            _btnRemove = MakeButton(prt, UiText.T("jammer.cut", Lang.T("拆掉芯片", "CUT IT OUT")), new Vector2(-190f, -250f), new Vector2(340f, 56f),
                       new Color(0.26f, 0.10f, 0.10f, 1f), new Color(0.95f, 0.62f, 0.58f, 1f), ChooseRemove);
            _btnKeep = MakeButton(prt, UiText.T("jammer.keep", Lang.T("留着它", "KEEP IT")), new Vector2(190f, -250f), new Vector2(340f, 56f),
                       new Color(0.12f, 0.15f, 0.18f, 1f), new Color(0.62f, 0.70f, 0.74f, 1f), ChooseKeep);

            _btnRemove.SetActive(false);
            _btnKeep.SetActive(false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器界面构建失败: {e.Message}");
            ChooseKeep();
        }
    }

    private static GameObject MakeButton(RectTransform parent, string label, Vector2 pos, Vector2 size,
                                         Color bg, Color fg, Action onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var img = go.GetComponent<Image>();
        img.color = bg;
        img.raycastTarget = true;

        var btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        btn.transition = Selectable.Transition.ColorTint;
        var cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
        cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        cb.selectedColor = Color.white;
        cb.fadeDuration = 0.08f;
        btn.colors = cb;

        var t = MakeText(rt, "Text", label, 18f, fg);
        var trt = t.rectTransform;
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;
        t.alignment = TextAlignmentOptions.Center;

        btn.onClick.AddListener(() =>
        {
            try { onClick(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器按钮出错: {e.Message}"); }
        });
        return go;
    }

    private static Image NewImage(Transform parent, string name, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
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

    private static TextMeshProUGUI MakeText(Transform parent, string name, string text, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);

        var t = go.AddComponent<TextMeshProUGUI>();
        try { if (DlcPanel.Font != null) t.font = DlcPanel.Font; } catch { }
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        t.overflowMode = TextOverflowModes.Overflow;
        t.alignment = TextAlignmentOptions.TopLeft;
        return t;
    }

    private static void SetRect(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }
}
