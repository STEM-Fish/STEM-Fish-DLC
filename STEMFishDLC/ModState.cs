using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  ModState - MOD 自身的启用状态 / 隔离模式 / 重启
//  ---------------------------------------------------------------------------
//  【启用 / 停用】
//    cfg [General] Enabled。停用时 Plugin.Awake 只初始化 ModMenu（否则没法再打开界面去启用回来），
//    其余所有模块（patch、物品、HUD、剧情）全部跳过 → 相当于"这个 MOD 没装"。
//    切换必须重启，因为 Harmony patch 一旦打上就撤不干净。
//
//  【隔离其他 MOD】
//    目标：只让本 DLC 生效，方便单独测剧情。
//    做法**不改游戏目录里的任何文件**（重命名 DLL 需要进程退出，风险太高），而是在运行时：
//      ① 找出所有"引用了 BepInEx"的插件程序集（= 别的 MOD），排除自己；
//      ② 对每个程序集执行 Harmony.UnpatchAll(程序集名) —— 撤掉它打过的所有补丁；
//      ③ 销毁它在场景里创建的 MonoBehaviour 实例。
//    时机很关键：必须等别的插件 Awake 跑完才有东西可撤，所以在启动后第 30 / 120 / 300 帧各执行一次。
//    重启游戏即恢复（不做任何持久化破坏）。
// ============================================================================

public static class ModState
{
    public static bool Enabled = true;
    public static bool Isolate = false;
    public static bool DebugTools = false;
    // 隔离白名单：文件名包含这些词的 dll 不屏蔽（逗号分隔）。预加载器直接读 cfg 的这一行。
    public static string IsolateKeep = "No Self-Destruct, KrokMP, Together, CT_AutoUpdate";

    public static string Version =>
        typeof(Plugin).Assembly.GetName().Version != null
            ? typeof(Plugin).Assembly.GetName().Version.ToString(3)
            : "0.0.0";

    // 主菜单底部那行状态文字
    public static string StatusLine =>
        Enabled
            ? $"STEM-Fish-DLC v{Version} available"
            : $"STEM-Fish-DLC v{Version} disabled";

    public static Color StatusColor => Enabled
        ? new Color(0.36f, 0.86f, 0.55f, 1f)
        : new Color(0.90f, 0.36f, 0.36f, 1f);

    private static readonly List<Assembly> _others = new List<Assembly>();

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("General", "Enabled", true,
                "本 MOD 总开关。关闭后除「MOD 管理」界面外全部功能不加载（需重启）。").Value;
            Isolate = plugin.Config.Bind("General", "IsolateOtherMods", false,
                "隔离模式：启动后自动撤销其他 MOD 的 Harmony 补丁并销毁其实例，只保留本 DLC。需重启。").Value;
            DebugTools = plugin.Config.Bind("General", "DebugTools", false,
                "启用调试功能（F6/F7/F11/F12 直接给物品）。").Value;
            IsolateKeep = plugin.Config.Bind("General", "IsolateKeep", "No Self-Destruct, KrokMP, Together, CT_AutoUpdate",
                "隔离白名单：**相对路径**包含这些词的 dll 不屏蔽（逗号分隔）。" +
                "KrokMP 是其他 MOD 的依赖项且自带关闭联机的功能，不该屏蔽；" +
                "No Self-Destruct 屏蔽后退出游戏会触发芯片自毁。").Value;

            _others.Clear();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (IsOtherPlugin(asm)) _others.Add(asm);

            Plugin.Log.LogInfo($"[STEMFishDLC] MOD 状态: {(Enabled ? "启用" : "停用")} | " +
                               $"隔离其他MOD: {(Isolate ? "开" : "关")} | 调试: {(DebugTools ? "开" : "关")}");
            if (_others.Count > 0)
                Plugin.Log.LogInfo($"[STEMFishDLC] 检测到其他插件程序集 {_others.Count} 个: " +
                                   string.Join(", ", _others.Select(a => a.GetName().Name).ToArray()));
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] ModState.Init 失败: {e}");
        }
    }

    // 写着两项配置（不重启不生效）
    public static void Apply(bool enabled, bool isolate, bool debug)
    {
        try
        {
            var cfg = Plugin.Cfg;
            if (cfg == null) { Plugin.Log.LogWarning("[STEMFishDLC] 配置对象不可用，无法写入"); return; }
            cfg.Bind("General", "Enabled", true, "本 MOD 总开关。").Value = enabled;
            cfg.Bind("General", "IsolateOtherMods", false, "隔离模式。").Value = isolate;
            cfg.Bind("General", "DebugTools", false, "启用调试功能。").Value = debug;
            cfg.Bind("General", "IsolateKeep", "No Self-Destruct, KrokMP, Together, CT_AutoUpdate", "隔离白名单（相对路径子串，逗号分隔）").Value = IsolateKeep;
            cfg.Save();

            Enabled = enabled;
            Isolate = isolate;
            DebugTools = debug;
            Plugin.Log.LogInfo($"[STEMFishDLC] 已写入配置: 启用={enabled} 隔离={isolate} 调试={debug}（重启生效）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 写入配置失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  重启游戏
    //  ---------------------------------------------------------------------------
    //  Process.Start 当前 exe → 再 Application.Quit()。
    //  不做文件级操作，所以不会出现"dll 被占用改不动"的问题。
    // ==========================================================================
    // ==========================================================================
    //  重启：**不再自动化**
    //  ---------------------------------------------------------------------------
    //  为什么要放弃自动重启（三次失败的历史）：
    //    · v0.11.1 `timeout /t 3` 在无控制台进程里立刻报错退出 → 0 延迟
    //    · v0.11.6 `steam://rungameid` 只等 5 秒就回退，Steam 冷启动没那么快
    //    · v0.11.8 从 bat 里 `start` exe 起来的实例 —— **Player.log 里完全没有
    //      winhttp/doorstop 的加载痕迹，也没有任何 BepInEx 日志**：
    //      非 Steam 方式启动时 BepInEx 根本不注入，游戏就是纯原版。
    //    结论：任何"绕过 Steam 去拉 exe"的做法都会得到没有 MOD 的游戏，
    //    任何"模拟用户点 Steam"的做法都不可靠。
    //
    //  → 现在只做两件事：**写配置** + **明确告诉用户手动重启**。
    //    稳定、没有副作用，也不会再出现"重启后 MOD 全没了"。
    // ==========================================================================
    public static void ApplyAndTellUser(bool enabled, bool isolate, bool debug)
    {
        Apply(enabled, isolate, debug);
        try
        {
            string exe = null;
            try { exe = Process.GetCurrentProcess().MainModule.FileName; } catch { }
            var dir = !string.IsNullOrEmpty(exe) ? System.IO.Path.GetDirectoryName(exe) : null;
            Plugin.Log.LogWarning(
                "[STEMFishDLC] ============================================================\n" +
                "[STEMFishDLC] 配置已写入。请【从 Steam 退出并重新启动游戏】使改动生效。\n" +
                "[STEMFishDLC] （本 MOD 不再自动重启：非 Steam 方式启动时 BepInEx 不会注入，\n" +
                "[STEMFishDLC]   会导致游戏变成没有任何 MOD 的原版。）\n" +
                "[STEMFishDLC] ============================================================");
        }
        catch { }
    }

    // ==========================================================================
    //  隔离：真正的屏蔽在**预加载阶段**完成
    //  ---------------------------------------------------------------------------
    //  BepInEx/patchers/STEMFishDLC.Patcher.dll 会在插件枚举之前把别的 .dll 改名成
    //  .dll.isolated —— 那些 MOD 根本不会被加载，所以不存在"半死"状态。
    //  运行时不再撤销补丁（上一版就是因此把 CuSaveManager 弄成假死：界面还在、逻辑没了）。
    // ==========================================================================
    public static void Tick() { }

    // 隔离现在是否真的生效（别人的 dll 是否被改名了）
    public static bool IsIsolatedNow
    {
        get
        {
            try
            {
                int iso = 0;
                foreach (var f in System.IO.Directory.GetFiles(Paths.PluginPath, "*.isolated", System.IO.SearchOption.AllDirectories))
                    iso++;
                return iso > 0;
            }
            catch { return false; }
        }
    }

    public static int IsolatedFileCount()
    {
        try
        {
            int iso = 0;
            foreach (var f in System.IO.Directory.GetFiles(Paths.PluginPath, "*.isolated", System.IO.SearchOption.AllDirectories))
                iso++;
            return iso;
        }
        catch { return 0; }
    }

    // 是不是"别的 MOD 插件程序集"：引用了 BepInEx 且不是自己/不是 BepInEx 本家
    private static bool IsOtherPlugin(Assembly asm)
    {
        try
        {
            if (asm == null) return false;
            if (asm == typeof(Plugin).Assembly) return false;
            var name = asm.GetName().Name ?? "";
            if (name.StartsWith("BepInEx", StringComparison.Ordinal)) return false;
            if (name.StartsWith("0Harmony", StringComparison.Ordinal)) return false;
            if (name.StartsWith("MonoMod", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Mono.Cecil", StringComparison.Ordinal)) return false;
            if (name.StartsWith("System", StringComparison.Ordinal)) return false;
            if (name.StartsWith("Unity", StringComparison.Ordinal)) return false;
            if (name == "mscorlib" || name == "netstandard" || name == "Newtonsoft.Json") return false;
            if (name == "Assembly-CSharp" || name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)) return false;

            foreach (var r in asm.GetReferencedAssemblies())
                if (r.Name == "BepInEx") return true;
            return false;
        }
        catch { return false; }
    }

    // 只销毁"插件本体"组件（继承 BaseUnityPlugin），别去碰别人的数据组件

    // 给界面用：其他 MOD 的名字列表
    public static string OtherModNames()
    {
        try
        {
            if (_others.Count == 0) return "（没有检测到其他 MOD）";
            return string.Join("、", _others.Select(a =>
            {
                try { return a.GetName().Name; } catch { return "?"; }
            }).ToArray());
        }
        catch { return "-"; }
    }
}
