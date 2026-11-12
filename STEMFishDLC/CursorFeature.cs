using System;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  CursorFeature - 内置光标替换（原 STEM_Fish_MOD 的功能并入本 MOD）
//  ---------------------------------------------------------------------------
//  原理（沿用已验证方案）：
//    游戏每帧在 PlayerCamera.HandleCursorIcon 里按 PlayerCamera.cursor 索引调用
//    Cursor.SetCursor(cursors[idx])，索引 >= 6 表示"按住交互/攻击键"的交互态。
//    我们在 postfix 里读这个索引，换成自己的两组贴图。
//    为了让当帧最后一次 SetCursor 一定是我们的，做三层覆盖：
//      Update（Plugin.Update）→ LateUpdate → Application.onBeforeRender
//
//  贴图：plugins/STEMFishDLC/textures/cursor.png = 普通态，cursor_act.png = 交互态
//  开关：Cursor.Replace（默认开）+ Cursor.UseInteractTexture（默认开）
//        （后续可以在主菜单管理界面里切换）
// ============================================================================

public static class CursorFeature
{
    private static Texture2D _normal;
    private static Texture2D _interact;
    private static bool _enabled = true;
    private static bool _useInteract = true;

    private static bool _lastInteract;
    private static FieldInfo _cursorField;
    private static bool _diagDone;

    public static bool Enabled => _enabled;

    public static void Init(Plugin plugin)
    {
        try
        {
            _enabled = plugin.Config.Bind("Cursor", "Replace", true, "替换系统光标为 STEM Fish 准星").Value;
            _useInteract = plugin.Config.Bind("Cursor", "UseInteractTexture", true, "交互状态下使用 cursor_act.png").Value;

            _normal = LoadPng("cursor.png");
            _interact = LoadPng("cursor_act.png");

            if (_normal == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 未找到 textures/cursor.png，光标替换不可用");
                return;
            }

            var harmony = new Harmony("com.stemfish.dlc.cursor");
            var target = AccessTools.Method(typeof(PlayerCamera), "HandleCursorIcon");
            if (target == null)
            {
                Plugin.Log.LogError("[STEMFishDLC] 找不到 PlayerCamera.HandleCursorIcon");
                return;
            }
            var postfix = typeof(CursorFeature).GetMethod("HandleCursorIcon_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            var info = Harmony.GetPatchInfo(target);
            Plugin.Log.LogInfo($"[STEMFishDLC] 光标替换 hook 完成 | postfix={info?.Postfixes?.Count ?? -1} | 普通贴图={_normal.width}x{_normal.height}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] 光标 hook 失败: {e}");
        }
    }

    public static void Toggle(bool on)
    {
        _enabled = on;
        Plugin.Log.LogInfo($"[STEMFishDLC] 光标替换: {(_enabled ? "开" : "关")}");
    }

    // 三层覆盖：Update / LateUpdate / onBeforeRender
    public static void TickUpdate() => Apply();
    public static void TickLateUpdate() => Apply();
    public static void TickBeforeRender() => Apply();

    private static void Apply()
    {
        if (!_enabled || _normal == null) return;
        try
        {
            var tex = Pick(_lastInteract);
            Cursor.SetCursor(tex, new Vector2(tex.width / 2f, tex.height / 2f), CursorMode.Auto);
        }
        catch { }
    }

    private static Texture2D Pick(bool interacting)
        => (interacting && _useInteract && _interact != null) ? _interact : _normal;

    // 游戏每帧切完光标后，我们读它的状态并覆盖
    private static void HandleCursorIcon_Postfix(PlayerCamera __instance)
    {
        if (!_enabled || _normal == null) return;
        _lastInteract = IsInteracting(__instance);
        Apply();
    }

    private static bool IsInteracting(PlayerCamera cam)
    {
        try
        {
            if (_cursorField == null)
                _cursorField = AccessTools.Field(typeof(PlayerCamera), "cursor");
            if (_cursorField == null)
            {
                if (!_diagDone) { Plugin.Log.LogError("[STEMFishDLC] 诊断: 找不到 PlayerCamera.cursor"); _diagDone = true; }
                return false;
            }
            int idx = (int)_cursorField.GetValue(cam);
            return idx >= 6;                     // 游戏：交互态索引 = 普通索引 + 6
        }
        catch (Exception e)
        {
            if (!_diagDone) { Plugin.Log.LogError($"[STEMFishDLC] 诊断: 读取 cursor 失败: {e.Message}"); _diagDone = true; }
            return false;
        }
    }

    private static Texture2D LoadPng(string fileName)
    {
        try
        {
            string path = Path.Combine(Paths.PluginPath, "STEMFishDLC", "textures", fileName);
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(bytes))
            {
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                Plugin.Log.LogInfo($"[STEMFishDLC] 光标贴图: {fileName} ({tex.width}x{tex.height})");
                return tex;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 光标贴图加载失败 {fileName}: {e.Message}");
        }
        return null;
    }
}
