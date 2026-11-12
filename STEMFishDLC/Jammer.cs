using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  Jammer - 调试物品「屏蔽器」
//  ---------------------------------------------------------------------------
//  效果拆解（都是游戏自己的机制，我们只按顺序调）：
//    ① 等同「太阳耀斑」层修饰器：`WorldGeneration.world.SetUnchipped(true)`
//       （UnchippedModifier.Initialize 做的就是这一句；下一层 Disable 时恢复）
//    ② 弹出「是否拆除颅内芯片」的选择
//         · 不拆 → 芯片只在本层离线，屏蔽器烧毁（下一层自动恢复芯片）
//         · 拆   → 中度脑损伤 + 头部出血，并把 runSettings["unchipped"] 永久置真，
//                  以后每一层（以及读档后）都是无芯片模式 → 走游戏的自由结局
//
//  怎么拿到：控制台 `spawn jammer player`，或调试模式下按 F3。
//  物品定义在 ItemRegistry（template=medkit + stripContainer + useAction=Jammer.Use）。
// ============================================================================

public static class Jammer
{
    public static bool Enabled = true;

    private static int _flareDepth = int.MinValue;   // != MinValue：未拆除，记下使用时的层号
    private static bool _open;
    private static GameObject _root;
    private static Item _item;
    private static Body _body;
    private static PlayerCamera.SpeedType _prevSpeed = PlayerCamera.SpeedType.Normal;
    private static bool _touchedSpeed;
    private static WorldGeneration _uiSynced;   // 每个世界实例只同步一次 UI 状态
    private static float _openAt;       // 界面打开时刻（超时保底，防止把玩家锁死在暂停里）
    private const float AutoKeepAfter = 90f;

    public const string Id = "jammer";

    public static bool LootEnabled = true;
    public static int LootWeight = 2;
    private static bool _poolInjected;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Items", "Jammer", true,
                "启用「屏蔽器」（太阳耀斑效果 + 拆除芯片抉择）").Value;
            LootEnabled = plugin.Config.Bind("Items", "JammerLoot", true,
                "屏蔽器像普通物品一样出现在战利品里（商人货架 / 打碎的物资箱 / 尸体遗留）").Value;
            LootWeight = Mathf.Clamp(plugin.Config.Bind("Items", "JammerWeight", 2,
                "屏蔽器在战利品池里的权重（1 = 很稀有，5 = 常见；上限 20）").Value, 1, 20);
            Plugin.Log.LogInfo($"[STEMFishDLC] 屏蔽器：{(Enabled ? "启用" : "停用")} | 战利品投放 {(LootEnabled ? $"权重 {LootWeight}" : "关")}");
        }
        catch { }
    }

    // ==========================================================================
    //  使用
    // ==========================================================================
    public static void Use(Body body, Item item)
    {
        if (!Enabled || _open || body == null) return;

        try
        {
            var w = WorldGeneration.world;
            if (w != null)
            {
                w.SetUnchipped(true);                 // ① 耀斑同款：芯片离线
                _flareDepth = w.biomeDepth;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器：芯片离线失败 {e.Message}");
        }

        _body = body;
        _item = item;
        _open = true;
        _openAt = Time.unscaledTime;

        PauseGame();
        BuildUI();
        Plugin.Log.LogInfo("[STEMFishDLC] 屏蔽器已启动：芯片离线，等待玩家决定是否拆除");
    }

    // 每帧：没拆的话，到下一层把芯片恢复回来
    public static void Tick()
    {
        InjectPool();

        // 每个新世界同步一次「无芯片」UI：WorldGeneration.Start 只在 unchipped=true 时
        // 调 SetUnchipped(true)，false 时什么都不做 —— 上一局留下的 PlayerCamera 无芯片
        // 界面会一直挂着，看起来就像新存档也是无芯片模式。这里按世界真实状态刷一次。
        SyncChipUi();

        // 保底：界面开着但玩家一直没选（或 UI 没显示出来）→ 90 秒后按"不拆"处理，
        // 绝不能把人永远锁在 timeScale=0 里
        if (_open && Time.unscaledTime - _openAt > AutoKeepAfter)
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 屏蔽器界面超时，自动按「留着它」处理");
            ChooseKeep();
            return;
        }

        if (_flareDepth == int.MinValue) return;

        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { return; }
        if (w == null) return;

        int depth;
        try { depth = w.biomeDepth; } catch { return; }
        if (depth == _flareDepth) return;

        try { w.SetUnchipped(false); } catch { }
        _flareDepth = int.MinValue;
        Plugin.Log.LogInfo("[STEMFishDLC] 屏蔽器效果结束：芯片恢复在线");
    }

    // ==========================================================================
    //  战利品池注入（和弹药箱同一套机制）
    //  ---------------------------------------------------------------------------
    //  ItemLootPool 的三个消费方正好覆盖作者要的三种来源：
    //    · TraderScript.GenerateSingleItemList  → 商人货架
    //    · BuildingEntity.CheckDamage           → 打碎物资箱的掉落
    //    · CorpseScript.Start                   → 尸体上的遗留物
    //  我们的 id 不在 Resources 里，但 SaveSlots 已经挂了 Resources.Load(string)
    //  前缀（返回 ItemRegistry 造的模板），所以这几条路径都能正常生成。
    // ==========================================================================
    private static void InjectPool()
    {
        if (_poolInjected || !LootEnabled) return;
        try
        {
            var pool = ItemLootPool.pool;
            if (pool == null || pool.Count == 0) return;      // 还没初始化

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
                // 池名和预期不同 → 塞进元素最多的那个，至少保证能出现
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
        if (w == _uiSynced) return;
        _uiSynced = w;
        try { w.SetUnchipped(WorldGeneration.unchipped); } catch { }   // 静态属性，读的是 world.unchippedMode
    }

    // ==========================================================================
    //  选择
    // ==========================================================================
    private static void ChooseRemove()
    {
        try
        {
            var body = _body;
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

            _flareDepth = int.MinValue;      // 不再恢复
            Plugin.Log.LogInfo("[STEMFishDLC] 芯片已拆除：脑损伤 + 头部出血，永久无芯片模式");

            SaveSlots.Toast("芯片拆掉了。从现在起，没人给你计时了。");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 拆除芯片失败: {e.Message}\n{e.StackTrace}");
        }

        Close(true);
    }

    private static void ChooseKeep()
    {
        Plugin.Log.LogInfo("[STEMFishDLC] 玩家选择保留芯片：本层离线，屏蔽器烧毁");
        SaveSlots.Toast("屏蔽器烧掉了。芯片还在里面。");
        Close(true);
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

        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;

        ResumeGame();
    }

    // ==========================================================================
    //  UI（自绘，半透明黑 + 扁平，和 MOD 面板同一套风格）
    // ==========================================================================
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

            // 遮罩（吃掉所有点击）
            var dim = NewImage(_root.transform, "Dim", new Color(0f, 0f, 0f, 0.62f));
            dim.raycastTarget = true;

            // 面板
            var panel = NewImage(_root.transform, "Panel", new Color(0.055f, 0.070f, 0.085f, 0.985f));
            panel.raycastTarget = true;
            var prt = panel.rectTransform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(720f, 340f);

            var bar = NewImage(prt, "Bar", new Color(0.30f, 0.81f, 1.00f, 1f));
            SetRect(bar.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(3f, 340f));

            var title = MakeText(prt, "Title", "屏蔽器启动", 26f, new Color(0.87f, 0.92f, 0.94f, 1f));
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -22f), new Vector2(640f, 34f));

            var sub = MakeText(prt, "Sub",
                "一阵强噪声淹没了颅内芯片 —— 它现在离线了，不会再给你计时。\n\n" +
                "要不要趁这段时间把它拆掉？",
                16f, new Color(0.62f, 0.70f, 0.74f, 1f));
            sub.enableWordWrapping = true;
            SetRect(sub.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -66f), new Vector2(664f, 90f));

            var warn = MakeText(prt, "Warn",
                "<color=#c98a4a>拆掉：中度脑损伤 + 头部出血，此后永久进入「无芯片」状态。</color>\n" +
                "<color=#5a6672>不拆：芯片只在本层离线，屏蔽器烧毁。</color>",
                14f, new Color(0.50f, 0.57f, 0.61f, 1f));
            warn.enableWordWrapping = true;
            SetRect(warn.rectTransform, new Vector2(0f, 1f), new Vector2(28f, -160f), new Vector2(664f, 60f));

            MakeButton(prt, "拆掉芯片", new Vector2(-180f, -124f), new Vector2(300f, 52f),
                       new Color(0.26f, 0.10f, 0.10f, 1f), new Color(0.95f, 0.62f, 0.58f, 1f), ChooseRemove);

            MakeButton(prt, "留着它", new Vector2(180f, -124f), new Vector2(300f, 52f),
                       new Color(0.12f, 0.15f, 0.18f, 1f), new Color(0.62f, 0.70f, 0.74f, 1f), ChooseKeep);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器界面构建失败: {e.Message}");
            // 界面建不出来也别把玩家锁死：直接按"不拆"处理
            ChooseKeep();
        }
    }

    private static void MakeButton(RectTransform parent, string label, Vector2 pos, Vector2 size,
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

        btn.onClick.AddListener(() => { try { onClick(); } catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 屏蔽器按钮出错: {e.Message}"); } });
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
