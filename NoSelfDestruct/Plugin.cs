using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NoSelfDestructOnQuit
{
    // ========================================================================
    //  停用「退出时自毁」
    //  ------------------------------------------------------------------------
    //  原版行为（正式版 v1.0 反汇编确认）：
    //    PauseHandler.TryQuit() 里按点击次数 1→4 递进，第 4 次点会：
    //        if (!PlayerCamera.main.selfDestructActive) TogglePause();
    //        PlayerCamera.main.StartSelfDestruct();      ← 芯片自毁（丢档）
    //  试用版的 No Self-Destruct on Quit.dll 做法是**整段跳过**：
    //  前缀先取消暂停、把退出按钮恢复成普通退出样式，再直接 ToMainMenu()，
    //  然后 return false 让原方法一行都不执行。
    //  正式版这些成员都还在，所以原样移植。
    // ========================================================================
    [BepInPlugin(Guid, "No Self-Destruct on Quit", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "GSR.noselfdestructonquit";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                var harmony = new Harmony(Guid);

                // ★ 不用 PatchAll()：这个环境下 PatchAll 会静默 0 patch（已验证过），
                //   必须手动 Patch + 回查 GetPatchInfo。
                var tryQuit = AccessTools.Method(typeof(PauseHandler), "TryQuit");
                if (tryQuit != null)
                {
                    var pre = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TryQuit_Prefix),
                        BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(tryQuit, prefix: pre);
                    var info = Harmony.GetPatchInfo(tryQuit);
                    Logger.LogInfo($"[NoSelfDestruct] TryQuit 已挂载 | prefix={info?.Prefixes?.Count ?? -1}");
                }
                else Logger.LogWarning("[NoSelfDestruct] 找不到 PauseHandler.TryQuit");

                var start = AccessTools.Method(typeof(PauseHandler), "Start");
                if (start != null)
                {
                    var post = new HarmonyMethod(typeof(Plugin).GetMethod(nameof(Start_Postfix),
                        BindingFlags.Static | BindingFlags.NonPublic));
                    harmony.Patch(start, postfix: post);
                    var info = Harmony.GetPatchInfo(start);
                    Logger.LogInfo($"[NoSelfDestruct] PauseHandler.Start 已挂载 | postfix={info?.Postfixes?.Count ?? -1}");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[NoSelfDestruct] 打补丁失败: " + e);
            }
        }

        private static bool TryQuit_Prefix(PauseHandler __instance)
        {
            try
            {
                if (__instance == null) return true;

                // 先取消暂停，否则主菜单上还挂着暂停状态
                // ★ 正式版里 paused / TogglePause 是**静态**的（试用版是实例成员），
                //   直接用 PauseHandler.xxx 访问，靠实例调用会编译不过。
                try { if (PauseHandler.paused) PauseHandler.TogglePause(); } catch { }

                RestoreNormalExitButton(__instance);
                __instance.timesQuitPressed = 0;

                var pc = PlayerCamera.main;
                if (pc != null) pc.ToMainMenu();
                else return true;      // 没有相机就当没发生过，交给原版

                Log.LogInfo("[NoSelfDestruct] 直接回主菜单（跳过自毁）");
                return false;          // 跳过原版 TryQuit
            }
            catch (Exception e)
            {
                Log.LogWarning("[NoSelfDestruct] 前缀异常，交回原版: " + e.Message);
                return true;
            }
        }

        // 进暂停菜单时把「自毁」样式改回普通「退出」
        private static void Start_Postfix(PauseHandler __instance)
        {
            try
            {
                if (__instance == null) return;
                __instance.timesQuitPressed = 0;
                RestoreNormalExitButton(__instance);
            }
            catch { }
        }

        private static void RestoreNormalExitButton(PauseHandler ph)
        {
            try
            {
                var btn = ph.quitButton;
                if (btn == null) return;

                if (ph.exitSprite != null) btn.sprite = ph.exitSprite;
                btn.color = Color.white;

                var tip = btn.GetComponent<UITooltip>();
                if (tip != null)
                {
                    tip.tipName = Locale.GetOther("pauseexit");
                    tip.tipDesc = "";
                    tip.skipLocale = true;
                }
            }
            catch { }
        }
    }
}
