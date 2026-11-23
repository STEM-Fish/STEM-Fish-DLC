using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace STEMFishDLC;

// ============================================================================
//  HealthPanel - 把原版「健康面板」改成 STEM-Fish 的档案 + 天蓝配色
//  ---------------------------------------------------------------------------
//  反汇编确认（PlayerCamera.Start）：
//      if (!SaveSystem.loadedRun)
//          WoundView.view.SetCharDetails(
//              Random.Range(130,161),    // 身高
//              Random.Range(10,26),      // 年龄
//              Random.Range(1,100000),   // 编号
//              Random.Range(0,10));      // 固件版本
//
//  SetCharDetails 内部只做三件事：
//      bodyStatText.text = height + "cm | " + age + "y | #" + id
//      versionText.text  = "UNI-HEALTH v2.0" + ver
//      cInfo = {height, age, id, ver};  specimenId = id
//  → 身高/年龄/编号都是 int，所以 "#AAA01" 这种十六进制编号**没法**通过
//    SetCharDetails 传进去（它只会 ToString()）。只能直接改文本。
//
//  ★ 为什么每帧盯着改，而不是 patch 一次：
//    读档走 SaveSystem.TryLoadGame，它也会调 SetCharDetails（用存档里的值），
//    会把我们改过的文本盖回 "155cm | 19y | #13863"。
//    所以做成"发现不对就改写"，TMP 的 set_text 对相同字符串有 early-out。
//
//  ★ 配色：反汇编里 WoundView 只有零星几处 set_color，说明**绿色写在 prefab 的
//    Graphic.color 上**（图片和文字共用同一套绿色调）。所以只能运行时遍历
//    WoundView 下所有 Graphic，把"绿色系"换成天蓝。红色系（受伤警告）保留。
//
//  ★ v0.13.2 状态（作者确认停在这里，不再继续折腾）：
//    · 图标贴图的重着色是好的（见 SpriteRecolor，整张图集读入后内存裁剪）
//    · 面板内绝大多数图标/文字/色块已经是天蓝
//    · 仍然为绿的少数几个（心情笑脸、右侧医疗/护甲切换按钮）属于
//      PlayerCamera / 独立字段那一挂，不在 WoundView 层级里，暂不再处理
//    · 面板背景保持原版（不加光晕、不做透明化）
// ============================================================================

public static class HealthPanel
{
    public const string SubjectName = "STEM-Fish";
    public static string BodyStat => $"{BodyProportion.HeightCm:0}cm | 19y | #AAA01";

    public static bool Enabled = true;
    public static bool Recolor = true;

    private static readonly List<Graphic> _graphics = new List<Graphic>();
    private static WoundView _cachedView;
    private static Transform _cachedRoot;
    private static float _colorT;
    private static float _listT;
    private static bool _logged;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("HUD", "HealthPanelSkin", true,
                "把健康面板改成 STEM-Fish 档案（姓名 / 171cm / #AAA01）").Value;
            Recolor = plugin.Config.Bind("HUD", "HealthPanelSkyBlue", true,
                "健康面板配色由绿色改为天蓝色（红色警告色保留）").Value;
            SpriteRecolor.Enabled = plugin.Config.Bind("HUD", "HealthPanelRecolorIcons", true,
                "把面板里的绿色图标贴图复制一份改成蓝色（关闭=图标保持原色）").Value;
            SpriteRecolor.FlipY = plugin.Config.Bind("HUD", "HealthPanelIconFlipY", false,
                "图标上下颠倒时打开这个（应对不同图形 API 的贴图读回方向差异，需重启）").Value;
        }
        catch { }
    }

    public static void Tick()
    {
        if (!Enabled && !Recolor) return;

        var v = WoundView.view;
        if (v == null) return;

        bool active = false;
        try { active = v.gameObject.activeInHierarchy; } catch { }
        if (!active) { _cachedView = null; return; }

        if (Enabled) ApplyText(v);
        if (Recolor) ApplyColor(v);
    }

    // ------------------------------------------------------------------------
    //  文本：姓名 + 身高/年龄/编号
    // ------------------------------------------------------------------------
    private static void ApplyText(WoundView v)
    {
        try
        {
            if (v.nameText != null && v.nameText.text != SubjectName)
                v.nameText.text = SubjectName;

            if (v.bodyStatText != null && v.bodyStatText.text != BodyStat)
                v.bodyStatText.text = BodyStat;
        }
        catch (Exception e) { Once($"[STEMFishDLC] 健康面板改文本失败: {e.Message}"); }
    }

    // ------------------------------------------------------------------------
    //  配色：绿色系 → 天蓝
    // ------------------------------------------------------------------------
    private static void ApplyColor(WoundView v)
    {
        // 图标重着色是分帧做的（每帧几个），这里推进
        SpriteRecolor.Pump();

        _colorT += Time.unscaledDeltaTime;
        if (_colorT < 0.10f) return;         // 10Hz 足够，避免每帧扫 UI 顶点
        _colorT = 0f;

        try
        {
            // 面板里会动态生成图标（GenerateIcon），列表要定期重建
            _listT -= 0.10f;
            if (_cachedView != v || _cachedRoot != v.transform || _listT <= 0f)
            {
                _cachedView = v;
                _cachedRoot = v.transform;
                _listT = 2.0f;
                v.GetComponentsInChildren(true, _graphics);
            }

            // 文本的"正常色"字段也要改，否则 FlashText 会把绿色写回去
            v.normalTextColor = RecolorIfGreen(v.normalTextColor);

            for (int i = 0; i < _graphics.Count; i++)
            {
                var g = _graphics[i];
                if (g == null) continue;

                // ① 贴图：游戏自带的绿色图标 → 蓝色副本（注入式，游戏文件一个字节不动）
                //    Image.color 是乘法，绿图乘蓝只会变暗绿，只能换像素
                var img = g as Image;
                if (img != null && img.sprite != null)
                {
                    var blue = SpriteRecolor.Get(img.sprite);
                    if (blue != null && !ReferenceEquals(blue, img.sprite))
                    {
                        img.sprite = blue;
                    }
                }

                // ② 顶点色：绿色系 → 天蓝
                var c = g.color;
                if (!SpriteRecolor.IsGreenish(c)) continue;
                g.color = SpriteRecolor.ToSky(c);
            }

            // 心情笑脸：不在面板子层级里，是 WoundView 的独立字段
            RecolorFace(v);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"[STEMFishDLC] 健康面板配色: 绿色→天蓝（{_graphics.Count} 个 Graphic）");
            }
        }
        catch (Exception e) { Once($"[STEMFishDLC] 健康面板配色失败: {e.Message}"); }
    }

    // ------------------------------------------------------------------------
    //  心情笑脸
    //  ---------------------------------------------------------------------------
    //  happinessIcon 是 WoundView 的独立 Image 字段，挂在面板层级之外，
    //  所以遍历子物体扫不到它。而且游戏会根据心情从 moodIcons[] 里挑一张
    //  重新赋给它，所以**数组和 Image 都要处理**：
    //      · moodIcons[] 换成蓝色版  → 游戏之后赋过去的就是蓝的
    //      · happinessIcon 当场也刷一次 → 不用等下一次心情变化
    //  贴图和顶点色两样都试：贴图本身是绿的换图；贴图是白的（靠 color 染绿）就改色。
    // ------------------------------------------------------------------------
    private static void RecolorFace(WoundView v)
    {
        try
        {
            var moods = v.moodIcons;
            if (moods != null)
            {
                for (int i = 0; i < moods.Length; i++)
                {
                    var s = moods[i];
                    if (s == null) continue;
                    var blue = SpriteRecolor.Get(s);
                    if (blue != null && !ReferenceEquals(blue, s)) moods[i] = blue;
                }
            }

            var img = v.happinessIcon;
            if (img == null) return;

            if (img.sprite != null)
            {
                var blue = SpriteRecolor.Get(img.sprite);
                if (blue != null && !ReferenceEquals(blue, img.sprite)) img.sprite = blue;
            }

            var c = img.color;
            if (SpriteRecolor.IsGreenish(c)) img.color = SpriteRecolor.ToSky(c);
        }
        catch (Exception e) { Once($"[STEMFishDLC] 心情图标改色失败: {e.Message}"); }
    }

    private static Color RecolorIfGreen(Color c) => SpriteRecolor.IsGreenish(c) ? SpriteRecolor.ToSky(c) : c;

    private static string _lastWarn;
    private static void Once(string msg)
    {
        if (_lastWarn == msg) return;
        _lastWarn = msg;
        Plugin.Log.LogWarning(msg);
    }
}
