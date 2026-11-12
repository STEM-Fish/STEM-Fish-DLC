using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace STEMFishDLC;

// ============================================================================
//  EngineTick - 引擎级每帧驱动（不依赖任何 MonoBehaviour 的生命周期）
//  ---------------------------------------------------------------------------
//  背景（v0.11.3 实测）：
//    LogOutput.log 在 `Chainloader startup complete` 之后立刻出现
//      [STEMFishDLC] !! 插件对象被销毁
//      [STEMFishDLC] !! TickRunner 被销毁（每帧逻辑将停止）
//    也就是说 BepInEx 的 ManagerObject 和我们自建的 DontDestroyOnLoad 对象
//    **都在启动瞬间被外部销毁了**。Harmony 补丁仍然生效（后续日志还能打出
//    「主菜单按钮已注入」），但 MonoBehaviour 的 Update 全部停止 →
//    游戏内所有模块不工作 → 表现成"MOD 管理页还在、游戏是原版"。
//
//  解法：不再把"每帧"寄托在 MonoBehaviour 上，改用 Unity 的**静态/委托事件**：
//    ① Application.onBeforeRender    —— 每帧渲染前，纯引擎回调
//    ② Camera.onPreRender            —— 任一相机渲染前（备份）
//    ③ Harmony patch 游戏自己的 Update —— 终极兜底（见 TickRunner.PatchGameUpdate）
//  三者都只是"调用同一个 Pump"，Pump 内部用 Time.frameCount 去重，
//  所以同一帧只会真正执行一次，不会重复。
//
//  另外挂 SceneManager.sceneLoaded：换场景时尝试把 TickRunner 重建回来
//  （有 Update 会比 onBeforeRender 更早拿到输入）。
// ============================================================================

public static class EngineTick
{
    private static int _pumpedFrame = -1;
    private static int _destroyLogLeft = 4;

    public static int FramesPumped { get; private set; }

    public static void Install()
    {
        // ★ 每次都重挂（不 early-return）：
        //   Unity 在某些版本/时机（尤其是换场景）会把 Application.onBeforeRender /
        //   Camera.onPreRender 的静态委托清空。先 -= 再 += 保证既不留空、也不重复。
        try { Application.onBeforeRender -= OnBeforeRender; Application.onBeforeRender += OnBeforeRender; }
        catch (Exception e) { Log($"[EngineTick] 订阅 onBeforeRender 失败: {e.Message}"); }

        try { Camera.onPreRender -= OnCameraPreRender; Camera.onPreRender += OnCameraPreRender; }
        catch (Exception e) { Log($"[EngineTick] 订阅 Camera.onPreRender 失败: {e.Message}"); }

        if (_sceneHooked) return;
        _sceneHooked = true;
        try { SceneManager.sceneLoaded -= OnSceneLoaded; SceneManager.sceneLoaded += OnSceneLoaded; }
        catch (Exception e) { Log($"[EngineTick] 订阅 sceneLoaded 失败: {e.Message}"); }

        Log("[EngineTick] 引擎级驱动器已安装（onBeforeRender + Camera.onPreRender + sceneLoaded）");
    }

    private static bool _sceneHooked;

    // ---- ① 主驱动 ----
    private static void OnBeforeRender()
    {
        Pump("onBeforeRender");
    }

    // ---- ② 备份驱动（某些情况下 onBeforeRender 不触发）----
    private static void OnCameraPreRender(Camera cam)
    {
        Pump("cameraPreRender");
    }

    // ---- 统一入口：同一帧只执行一次 ----
    public static void Pump(string source)
    {
        int f = Time.frameCount;
        if (f == _pumpedFrame) return;
        _pumpedFrame = f;
        FramesPumped++;
        try { Plugin.TickAll(source); }
        catch (Exception e) { Log($"[EngineTick] TickAll 异常({source}): {e.Message}"); }
    }

    // ---- ③ 换场景：重挂静态委托 + 把 TickRunner 重建回来 ----
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            Install();                      // ★ 重挂：Unity 换场景可能已经把静态委托清空

            // ★ 等**第一个场景加载完**再建 TickRunner：
            //   Unity 的行为是——在第一个场景加载完成之前调用 DontDestroyOnLoad 的对象，
            //   会在那次场景加载时被一并清掉（v0.11.5 日志里的「TickRunner 被销毁」就是它）。
            if (TickRunner.Instance == null) TickRunner.EnsureInstance();

            Log($"[EngineTick] 场景 {scene.name} 就绪 | 静态委托已重挂 | " +
                $"TickRunner={((TickRunner.Instance != null) ? "在" : "缺")} | 已泵 {FramesPumped} 帧");
        }
        catch (Exception e) { Log($"[EngineTick] sceneLoaded 处理失败: {e.Message}"); }
    }

    // OnDestroy 里打调用栈 —— 用来查明到底是谁销毁了对象
    public static void LogDestroy(string what)
    {
        if (_destroyLogLeft <= 0) return;
        _destroyLogLeft--;
        string trace = "";
        try { trace = Environment.StackTrace ?? ""; } catch { }
        if (trace.Length > 1600) trace = trace.Substring(0, 1600);
        Log($"[!!] {what} 被销毁，调用栈:\n{trace}");
    }

    private static void Log(string msg)
    {
        try { Plugin.Log.LogWarning(msg); } catch { }
    }
}
