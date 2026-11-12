using System;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  TickRunner - 可重建的每帧驱动器（加速器，不是唯一依赖）
//  ---------------------------------------------------------------------------
//  v0.11.3 实测：本对象在 `Chainloader startup complete` 之后**立刻被销毁**
//  （日志有 "!! TickRunner 被销毁"）。所以它现在只是**可选加速器**：
//    真正的保底驱动在 EngineTick（Application.onBeforeRender），
//    即使本对象不存在，游戏内功能也照常工作。
//
//  另外加了一个终极兜底：Harmony patch 游戏自己的每帧方法
//  （PlayerCamera.Update / PauseHandler.Update）——
//  只要游戏在跑，这两个方法就一定在跑，不受任何对象生命周期影响。
// ============================================================================

public class TickRunner : MonoBehaviour
{
    internal static TickRunner Instance;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            try { Destroy(gameObject); } catch { }
            return;
        }
        Instance = this;
        try { DontDestroyOnLoad(gameObject); } catch { }
        try { Plugin.Log.LogInfo("[STEMFishDLC] TickRunner 已启动"); } catch { }
    }

    private void Update()
    {
        EngineTick.Pump("runnerUpdate");
    }

    private void LateUpdate()
    {
        try { Plugin.TickLate(); } catch { }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        EngineTick.LogDestroy("TickRunner");
    }

    // 确保实例存在（被销毁后由 EngineTick.sceneLoaded 调用）
    internal static void EnsureInstance()
    {
        if (Instance != null) return;
        try
        {
            var go = new GameObject("STEMFishDLC_Runner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TickRunner>();
        }
        catch (Exception e) { try { Plugin.Log.LogWarning($"[STEMFishDLC] 重建 TickRunner 失败: {e.Message}"); } catch { } }
    }

    // ==========================================================================
    //  终极兜底：patch 游戏自己的每帧方法
    //  ---------------------------------------------------------------------------
    //  PlayerCamera.Update  —— 游戏内每帧必跑
    //  PauseHandler.Update  —— 暂停菜单每帧必跑
    //  这两个方法由游戏自己的对象调用，我们只挂 postfix，不持任何引用。
    // ==========================================================================
    internal static void PatchGameUpdate()
    {
        try
        {
            var harmony = new Harmony("com.stemfish.dlc.tickpatch");
            int n = 0;

            // 主菜单（一进游戏那个画面）—— 此时 PlayerCamera / PauseHandler 都还没工作
            n += PatchOne(harmony, typeof(PreRunScript), "Update", "PreRunUpdate_Postfix");
            // 游戏内
            n += PatchOne(harmony, typeof(PlayerCamera), "Update", "PlayerCameraUpdate_Postfix");
            // 暂停菜单
            n += PatchOne(harmony, typeof(PauseHandler), "Update", "PauseUpdate_Postfix");
            // 世界每帧（保底：万一上面几个都不在）
            n += PatchOne(harmony, typeof(WorldGeneration), "Update", "WorldUpdate_Postfix");

            Plugin.Log.LogInfo($"[STEMFishDLC] 游戏每帧兜底 patch 完成 | postfix 合计={n}（主菜单/游戏内/暂停/世界）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 每帧兜底 patch 失败: {e.Message}");
        }
    }

    private static int PatchOne(Harmony harmony, Type type, string method, string postfixName)
    {
        try
        {
            var m = AccessTools.Method(type, method);
            if (m == null) return 0;
            var post = typeof(TickRunner).GetMethod(postfixName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (post == null) return 0;
            harmony.Patch(m, postfix: new HarmonyMethod(post));
            return Harmony.GetPatchInfo(m)?.Postfixes?.Count ?? 0;
        }
        catch { return 0; }
    }

    private static void PreRunUpdate_Postfix() => EngineTick.Pump("preRunUpdate");
    private static void PlayerCameraUpdate_Postfix() => EngineTick.Pump("playerCameraUpdate");
    private static void PauseUpdate_Postfix() => EngineTick.Pump("pauseUpdate");
    private static void WorldUpdate_Postfix() => EngineTick.Pump("worldUpdate");
}
