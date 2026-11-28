using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace STEMFishDLC;

// ============================================================================
//  Jukebox - 顶部的音乐按钮（急速下坠模式）
//  ---------------------------------------------------------------------------
//  反汇编确认的原版音乐系统：
//    MusicManager.main          —— 静态单例（Awake 里赋值）
//    MusicManager.biomeMusic    —— SongList[]，按 WorldGeneration.biomeDepth 取
//    SongList.songs             —— AudioClip[]，每层几首
//    MusicManager.PlaySong(clip) —— 停当前曲、换 clip、立刻播
//    MusicManager.StopSong()
//
//  所以"放某首歌"不需要自己搞 AudioSource，把 clip 交给 PlaySong 就行 ——
//  音量、混音器、渐入渐出全都沿用原版设置。
//
//  歌曲来源：把 biomeMusic 里所有 SongList 的 songs 收齐、去重，然后按名字
//  模糊匹配 PATHWAY / CHASE / RUN FOR / ASCENT 四首。匹配不到就退回全部曲目，
//  至少按钮仍然能用。
// ============================================================================

public static class Jukebox
{
    public static bool Enabled = true;
    public static float OffsetX = 0f;
    public static float OffsetY = -28f;

    private const float BtnSize = 44f;
    private const float RowW = 240f;
    private const float RowH = 30f;

    private static readonly string[] Wanted = { "pathway", "chase", "run for", "ascent" };

    private static GameObject _root, _btnGo, _listGo;
    private static Canvas _canvas;
    private static TextMeshProUGUI _btnLabel;
    private static readonly List<TextMeshProUGUI> _rowLabels = new List<TextMeshProUGUI>();
    private static readonly List<GameObject> _rows = new List<GameObject>();

    private static List<AudioClip> _clips;
    private static readonly List<string> _names = new List<string>();
    private static int _playing = -1;
    private static bool _open;
    private static bool _logged;
    private static WorldGeneration _lastWorld;

    private static readonly Color PanelBg = new Color(0.05f, 0.06f, 0.08f, 0.94f);
    private static readonly Color RowBg = new Color(0.09f, 0.11f, 0.14f, 0.96f);
    private static readonly Color RowSel = new Color(0.10f, 0.28f, 0.26f, 0.98f);
    private static readonly Color Body = new Color(0.90f, 0.94f, 0.94f, 1f);
    private static readonly Color Dim = new Color(0.62f, 0.70f, 0.72f, 1f);
    private static readonly Color Accent = new Color(0.20f, 0.78f, 0.72f, 1f);

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("HUD", "Jukebox", true,
                "急速下坠模式顶部的音乐按钮（原版四首曲子）").Value;
            OffsetX = plugin.Config.Bind("HUD", "JukeboxX", 0f,
                "音乐按钮水平位置（屏幕像素，0 = 屏幕顶部居中）").Value;
            OffsetY = plugin.Config.Bind("HUD", "JukeboxY", -28f,
                "音乐按钮垂直位置（屏幕像素，负数向下）").Value;
        }
        catch { }
    }

    public static void Update()
    {
        try
        {
            if (!Enabled) { Hide(); return; }

            // 只有急速下坠有这排按钮（其它模式音乐跟着层走，别乱动）
            bool freefall = RunConfig.Current != null && RunConfig.Current.code == RunConfig.FreefallCode;
            if (!freefall) { Hide(); return; }

            var pc = PlayerCamera.main;
            if (pc == null || pc.body == null) { Hide(); return; }

            // 换局（WorldGeneration 实例变了）→ 收起列表
            if (WorldGeneration.world != _lastWorld)
            {
                _lastWorld = WorldGeneration.world;
                SetOpen(false);
            }

            if (_root == null) Build();
            if (_root == null) return;

            if (!_root.activeSelf) _root.SetActive(true);
            RefreshLabels();
        }
        catch (Exception e)
        {
            Once($"[STEMFishDLC] 音乐按钮异常: {e.Message}");
        }
    }

    private static void Hide()
    {
        if (_root != null && _root.activeSelf) _root.SetActive(false);
    }

    // ======================================================================
    //  歌曲收集
    // ======================================================================
    private static void EnsureClips()
    {
        if (_clips != null) return;

        var list = new List<AudioClip>();
        var names = new List<string>();
        var seen = new HashSet<int>();

        try
        {
            var mm = MusicManager.main;
            if (mm != null && mm.biomeMusic != null)
            {
                for (int i = 0; i < mm.biomeMusic.Length; i++)
                {
                    var sl = mm.biomeMusic[i];
                    if (sl == null || sl.songs == null) continue;
                    for (int j = 0; j < sl.songs.Length; j++)
                    {
                        var c = sl.songs[j];
                        if (c == null) continue;
                        if (!seen.Add(c.GetInstanceID())) continue;
                        list.Add(c);
                        names.Add(c.name);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读取原版曲库失败: {e.Message}");
        }

        _clips = list;

        if (!_logged)
        {
            _logged = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 原版曲库: {list.Count} 首 → {string.Join(" | ", names)}");
        }

        // 按名字挑四首；挑不全就把剩下的补在后面
        var ordered = new List<AudioClip>();
        var orderedNames = new List<string>();
        for (int k = 0; k < Wanted.Length; k++)
        {
            int hit = -1;
            for (int i = 0; i < _clips.Count; i++)
            {
                if (ordered.Contains(_clips[i])) continue;
                string low = (_clips[i].name ?? "").ToLowerInvariant();
                if (low.Contains(Wanted[k])) { hit = i; break; }
            }
            if (hit >= 0) { ordered.Add(_clips[hit]); orderedNames.Add(names[hit]); }
        }
        for (int i = 0; i < _clips.Count; i++)
        {
            if (ordered.Contains(_clips[i])) continue;
            ordered.Add(_clips[i]); orderedNames.Add(names[i]);
        }

        _clips = ordered;
        _names.Clear();
        _names.AddRange(orderedNames);
    }

    // ======================================================================
    //  播放
    // ======================================================================
    private static void Play(int idx)
    {
        try
        {
            if (_clips == null || idx < 0 || idx >= _clips.Count) return;
            var mm = MusicManager.main;
            if (mm == null) return;

            if (_playing == idx)
            {
                mm.StopSong();
                _playing = -1;
                Plugin.Log.LogInfo("[STEMFishDLC] 音乐: 停止");
            }
            else
            {
                mm.PlaySong(_clips[idx]);
                _playing = idx;
                Plugin.Log.LogInfo($"[STEMFishDLC] 音乐: {_names[idx]}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 播放失败: {e.Message}");
        }
        SetOpen(false);
    }

    private static void SetOpen(bool open)
    {
        _open = open;
        if (_listGo != null && _listGo.activeSelf != open) _listGo.SetActive(open);
        if (_btnLabel != null) _btnLabel.color = open ? Accent : Body;
    }

    private static void RefreshLabels()
    {
        for (int i = 0; i < _rowLabels.Count; i++)
        {
            var t = _rowLabels[i];
            if (t == null) continue;
            bool playing = i == _playing;
            t.color = playing ? Accent : Body;
            var bg = _rows[i] != null ? _rows[i].GetComponent<Image>() : null;
            if (bg != null) bg.color = playing ? RowSel : RowBg;
        }
    }

    // ======================================================================
    //  UI
    // ======================================================================
    private static void Build()
    {
        EnsureClips();
        if (_clips == null || _clips.Count == 0) return;

        try
        {
            _root = new GameObject("STEMFishDLC_Jukebox");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 26000;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // ---- 音符按钮 ----
            _btnGo = NewRect("MusicBtn", _root.transform, BtnSize, BtnSize);
            var rt = (RectTransform)_btnGo.transform;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(OffsetX, OffsetY);

            var img = _btnGo.AddComponent<Image>();
            img.color = PanelBg;
            var btn = _btnGo.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.16f, 0.22f, 0.26f, 1f);
            colors.pressedColor = new Color(0.10f, 0.28f, 0.26f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(() => { SetOpen(!_open); });

            _btnLabel = NewText("Icon", _btnGo.transform, "♪", 28f, Body);
            Stretch(_btnLabel.rectTransform);

            // ---- 弹出列表 ----
            _listGo = NewRect("MusicList", _root.transform, RowW, 0f);
            var lrt = (RectTransform)_listGo.transform;
            lrt.anchorMin = new Vector2(0.5f, 1f);
            lrt.anchorMax = new Vector2(0.5f, 1f);
            lrt.pivot = new Vector2(0.5f, 1f);
            lrt.anchoredPosition = new Vector2(OffsetX, OffsetY - BtnSize - 6f);

            var lv = _listGo.AddComponent<VerticalLayoutGroup>();
            lv.spacing = 3f;
            lv.childControlHeight = false;
            lv.childControlWidth = false;
            lv.childForceExpandHeight = false;
            lv.childForceExpandWidth = false;
            lv.childAlignment = TextAnchor.UpperCenter;

            var fitter = _listGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            for (int i = 0; i < _clips.Count; i++)
            {
                int idx = i;
                var row = NewRect("Row" + i, _listGo.transform, RowW, RowH);
                var rim = row.AddComponent<Image>();
                rim.color = RowBg;
                var rbtn = row.AddComponent<Button>();
                rbtn.targetGraphic = rim;
                rbtn.onClick.AddListener(() => Play(idx));

                string label = _names[i];
                if (label.Length > 28) label = label.Substring(0, 28);
                var txt = NewText("T", row.transform, label, 15f, Body);
                var trt = txt.rectTransform;
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(8f, 0f);
                trt.offsetMax = new Vector2(-6f, 0f);
                txt.alignment = TextAlignmentOptions.Left;
                txt.enableWordWrapping = false;

                _rows.Add(row);
                _rowLabels.Add(txt);
            }

            SetOpen(false);
            _root.SetActive(false);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 音乐按钮构建失败: {e.Message}");
            _root = null;
        }
    }

    private static GameObject NewRect(string name, Transform parent, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(w, h);
        return go;
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, Color color)
    {
        var go = NewRect(name, parent, 10f, 10f);
        var t = go.AddComponent<TextMeshProUGUI>();
        try { if (DlcPanel.Font != null) t.font = DlcPanel.Font; } catch { }
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = TextAlignmentOptions.Center;
        t.raycastTarget = false;
        return t;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static readonly HashSet<string> _once = new HashSet<string>();
    private static void Once(string msg)
    {
        if (_once.Add(msg)) Plugin.Log.LogWarning(msg);
    }
}
