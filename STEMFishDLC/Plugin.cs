using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace STEMFishDLC;

// ============================================================================
//  STEM Fish DLC - Casualties: Unknown 剧情 DLC
//  ---------------------------------------------------------------------------
//  主角: 公司军事线实验体批次 STEM-Fish（个体 SF-07），狼-人基因杂合
//
//  热键: F8 战术终端 / F9 自言自语 / F10 台词扩充
//        调试（默认关闭，MOD 管理里开启）: F6 MDRZ9 / F7 弹药 / F11 弹挂甲 / F12 头盔
//
//  ★ 每帧逻辑不写在这里（见 EngineTick / TickRunner）：
//    实测本插件对象在 `Chainloader startup complete` 之后会被外部销毁，
//    一旦把 Update 写在这里，"游戏内所有功能"就跟着一起死。
// ============================================================================

[BepInPlugin("com.stemfish.dlc", "STEM Fish DLC", "0.18.11")]
[BepInProcess("CasualtiesUnknown.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Plugin Instance;
    // ★ 静态持有 ConfigFile：Plugin 对象被外部销毁后，写配置仍然可用
    //   （ConfigFile 是普通托管对象，不随 GameObject 一起死）
    internal static BepInEx.Configuration.ConfigFile Cfg;

    private void Awake()
    {
        Instance = this;
        // ★ 用**独立日志源**：插件对象被销毁时它不会被 Dispose（见 FishLog 注释）
        FishLog.Init();
        Log = FishLog.Source;
        try { Cfg = Config; } catch { }

        Log.LogInfo("[STEMFishDLC] ===== 加载 v0.18.11 =====");

        // ① 先装引擎级驱动器（最优先：它不依赖本对象，销毁也能活）
        EngineTick.Install();

        // ② 总开关 + MOD 管理界面 —— 无论启用与否都要在
        ModState.Init(this);
        ModMenu.Init(this);

        try
        {
            Diagnostics = Config.Bind("General", "Diagnostics", true,
                "输出运行心跳诊断日志（排查用，稳定后可关）").Value;
        }
        catch { }

        if (!ModState.Enabled)
        {
            Log.LogWarning("[STEMFishDLC] MOD 已停用：除「MOD 管理」界面外不加载任何功能。");
            Log.LogInfo("[STEMFishDLC] 加载完成（停用状态）");
            return;
        }

        // ③ 正常加载全部功能
        FishLocalization.Init();    // 自定义物品的名字/描述（Locale.GetString 补位）
        FishVoice.Init(this);       // 自言自语（台词库 + Locale 扩充）
        GunAudio.Init(this);        // 枪声低通参数（cfg）
        ItemRegistry.Init(this);    // 新物品注册
        DlcPanel.Init(this);        // 游戏内面板
        HudButton.Init(this);       // 左下角 HUD 按钮（点开战术面板）
        GunHandling.Init(this);     // R 换弹匣（无弹匣时拉栓）
        AmmoCompat.Init(this);      // 自定义口径 7.62×51（卸载弹匣/退弹接管）
        SpawnBridge.Init(this);     // 控制台 spawn 接入（spawn mdrz9 player）
        LoreNote.Init(this);        // P3：档案纸片生成 hook
        AmmoBox.Init(this);         // 弹药箱（战利品池注入 + 延迟填充）
        StarterKit.Init(this);      // 开局物资（只在新开局发一次）
        PlayerSkills.Init(this);    // 初始技能（新开局拉高 STR/RES/INT）
        UiText.Init(this);          // 界面文案外置（ui.zh-CN.json，覆盖模式卡文字）
        RunConfig.Init(this);       // 开局难度（含动态刷怪 SpawnDirector）
        NewRunPanel.Init(this);     // 我们自己的投放（难度）界面
        SaveSlots.Init(this);       // 命名多存档（槽位 + 存档点自动存档 + 存档键）
        MapSave.Init(this);         // 存档：把本层地图与角色坐标一起存下来
        LoreProgress.Init(this);    // 档案：每下一层自动解锁一篇
        LoreData.Init(this);        // 档案：进度绑定存档 + 教程全解锁
        HypeMode.Init(this);        // 急速下坠：击杀回心情
        CrateScatter.Init(this);    // 弹药箱：每层撒到地图上
        SpikeCrawler.Init(this);    // 新敌人：尖刺爬行者（远程，保持距离）
        LuckyBlock.Init(this);      // 急速下坠：幸运方块
        FreefallTweaks.Init(this);  // 急速下坠：装备免耐久 + 枪不卡壳
        LaserDrill.Init(this);      // 急速下坠：激光钻无限电量
        Hearing.Init(this);         // 战术耳塞（抗听力损伤 + 闷声）
        Mechanics.Init(this);       // 游泳 / 杀同类惩罚 / 血袋副作用
        WeaponHud.Init(this);       // 右下角武器状态 HUD
        HealthPanel.Init(this);     // 健康面板：STEM-Fish 档案 + 天蓝配色
        HealthDoll.Init(this);      // 右上角人体图（按部位显示受伤）
        GunFx.Init(this);           // 射击打击感（枪口闪光 + 命中粒子）
        FishSkin.Init(this);        // 内置皮肤替换（不依赖 SkinSyncMod，隔离模式下也保留外观）
        LoadingArt.Init(this);      // 加载/选人界面的角色立绘替换
        MenuArt.Init(this);         // 主菜单立绘：悬停轮廓生成 + 文字/点击框适配
        EntityWatchdog.Init(this);  // 诊断：血量负了却没死的实体
        CombatFeel.Init(this);      // 战斗手感：黄血 + 小型生物击退
        CoverArmor.Init(this);      // 贴皮衣物按部位给护甲
        Jammer.Init(this);          // 调试物品「屏蔽器」（太阳耀斑 + 拆芯片抉择）
        BloodRed.Init(this);        // 主角的血改成红色（肢体血渍 + 流血粒子）
        CursorFeature.Init(this);   // 内置准星替换（原 STEM_Fish_MOD 并入）

        // ④ 游戏内每帧兜底（patch 游戏的 Update，不依赖任何对象生命周期）
        //    注意：这里**不**创建 TickRunner —— 此刻 Unity 的第一次场景加载还没完成，
        //    在它之前 DontDestroyOnLoad 的对象会在场景加载时被清掉（v0.11.5 的日志
        //    「TickRunner 被销毁」就是这个）。交给 EngineTick.sceneLoaded 之后创建。
        TickRunner.PatchGameUpdate();

        Log.LogInfo("[STEMFishDLC] 加载完成（F8 面板）");
    }

    // ==========================================================================
    //  每帧逻辑（由 EngineTick.Pump 统一调用，多个来源去重）
    // ==========================================================================
    internal static void TickAll(string source)
    {
        // ★ 第一个调用的模块也必须 try：一旦抛异常，Unity 会中断整个方法，
        //   后面所有模块全部停摆，表现就是"MOD 管理页还在、游戏却是原版"
        Safe(ModMenu.Update);
        Safe(ModMenu.TickPauseButton);

        // 第一次拿到玩家 Body 时打一条显著日志
        if (!_enteredWorld)
        {
            try
            {
                var pc0 = PlayerCamera.main;
                if (pc0 != null && pc0.body != null)
                {
                    _enteredWorld = true;
                    Log.LogInfo($"[STEMFishDLC] 已进入世界，模块开始工作（body={pc0.body.name}，驱动={source}）");
                }
            }
            catch { }
        }

        Heartbeat(source);

        if (!ModState.Enabled) return;

        Safe(FishVoice.Update);
        Safe(DlcPanel.Update);
        Safe(HudButton.Update);         // 进世界后自动挂 HUD 按钮（失败会一直重试）
        Safe(WeaponHud.Update);         // 右下角武器状态
        Safe(GunHandling.Update);       // R：换弹匣 / 拉栓
        Safe(LoreProgress.Tick);        // 档案：每下一层自动解锁一篇（先解锁，纸片才会投下一篇）
        Safe(LoreNote.Update);          // 世界生成后投放未读档案
        Safe(CrateScatter.Tick);        // 弹药箱：每层撒到地图上
        Safe(HypeMode.Tick);            // 急速下坠：击杀去重表维护
        Safe(AmmoBox.Tick);             // 弹药箱：战利品池懒注入
        Safe(StarterKit.Tick);          // 开局物资：等玩家出生后发放
        Safe(PlayerSkills.Tick);        // 初始技能：等玩家出生后应用
        Safe(FishSkin.Tick);            // 内置皮肤：每帧校正角色贴图
        Safe(LoadingArt.Tick);          // 加载界面立绘：每两帧校正一次
        Safe(MenuArt.Tick);             // 主菜单立绘：生成轮廓 / 平移文字
        Safe(EntityWatchdog.Tick);      // 诊断：卡住的实体
        Safe(CursorFeature.TickUpdate); // 光标覆盖第 1 层
        Safe(GunFx.Tick);               // 光照参数热重载（改 cfg 保存即生效）
        Safe(Hearing.Tick);             // 耳塞：检测佩戴 + 平滑过渡
        Safe(HealthPanel.Tick);         // 健康面板改名/改色
        Safe(HealthDoll.Tick);          // 右上角人体图
        Safe(SpikeCrawler.Tick);        // 尖刺爬行者：维持距离 + 射尖刺
        Safe(LuckyBlock.Tick);          // 幸运方块：右键/打碎抽效果
        Safe(LaserDrill.Tick);          // 急速下坠：激光钻电量兜底
        Safe(NewRunPanel.Tick);         // 主菜单：我们自己的投放（难度）界面
        Safe(SpawnDirector.Tick);       // 世界内：按难度持续投放敌对生物
        Safe(SaveSlots.Tick);           // 存档键 + 存档点自动存档（暂停时也要能存）
        Safe(MapSave.Tick);             // 读档后等世界生成完，写回地图 + 传送角色
        Safe(Jammer.Tick);              // 屏蔽器：没拆的话，下一层恢复芯片

        // 暂停时不吃热键（面板开关也交给游戏自己的暂停菜单）
        if (IsPaused) return;

        if (Input.GetKeyDown(KeyCode.F8)) Safe(DlcPanel.Toggle);
        if (Input.GetKeyDown(KeyCode.F9))
        {
            Safe(FishVoice.Toggle);
            Log.LogInfo($"[STEMFishDLC] 自言自语: {(FishVoice.Enabled ? "开" : "关")}");
        }
        if (Input.GetKeyDown(KeyCode.F10))
        {
            Safe(FishVoice.ToggleExpand);
            Log.LogInfo($"[STEMFishDLC] 原版台词扩充: {(FishVoice.ExpandEnabled ? "开" : "关")}");
        }

        // 调试热键：必须先在 MOD 管理里打开「启用调试」
        if (!ModState.DebugTools) return;
        if (Input.GetKeyDown(KeyCode.F2)) Safe(Mechanics.Unstick);                          // 调试：解除禁足/卡死
        if (Input.GetKeyDown(KeyCode.F3)) Safe(() => ItemRegistry.GiveToPlayer("jammer"));   // 调试：屏蔽器
        if (Input.GetKeyDown(KeyCode.F5)) Safe(GunFx.TestAtPlayer);        // 特效链路自检
        if (Input.GetKeyDown(KeyCode.F6)) Safe(() => ItemRegistry.GiveToPlayer("mdrz9"));
        if (Input.GetKeyDown(KeyCode.F7)) Safe(() => SpawnBridge.GiveAmmoToPlayer(2, 20));
        if (Input.GetKeyDown(KeyCode.F11)) Safe(() => ItemRegistry.GiveToPlayer("platecarrier"));
        if (Input.GetKeyDown(KeyCode.F12)) Safe(() => ItemRegistry.GiveToPlayer("tachelmet"));
    }

    internal static void TickLate()
    {
        if (!ModState.Enabled) return;
        try { CursorFeature.TickLateUpdate(); } catch { }
    }

    // 单个模块出错不连坐（Unity 里抛异常会中断整个调用）
    internal static void Safe(Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (_lastError != e.Message)
            {
                _lastError = e.Message;
                try { Log.LogWarning($"[STEMFishDLC] 模块异常: {e.Message}\n{e.StackTrace}"); } catch { }
            }
        }
    }
    private static string _lastError;

    // ==========================================================================
    //  运行心跳诊断
    //  ---------------------------------------------------------------------------
    //  每 1.5 秒一行，最多 60 行（≈90 秒），之后自动静默。
    //  看这几项就能定位问题：driver（谁在驱动）/ pc / body / world / pool
    // ==========================================================================
    internal static bool Diagnostics = true;
    private static bool _enteredWorld;
    private static int _hbFrame = -1;
    private static int _hbLeft = 60;
    private static string _lastSource = "-";

    private static void Heartbeat(string source)
    {
        _lastSource = source;
        if (!Diagnostics || _hbLeft <= 0) return;
        if (_hbFrame < 0) _hbFrame = Time.frameCount;
        if (Time.frameCount - _hbFrame < 90) return;
        _hbFrame = Time.frameCount;
        _hbLeft--;

        try
        {
            var pc = PlayerCamera.main;
            var bd = pc != null ? pc.body : null;

            int poolN = -2;
            try { var p = ItemLootPool.pool; poolN = p != null ? p.Count : -1; } catch { }

            int itemN = -1;
            try { if (Item.allItems != null) itemN = Item.allItems.Count; } catch { }

            Log.LogInfo($"[DIAG] f={Time.frameCount} src={source} runner={(TickRunner.Instance != null)} " +
                        $"enabled={ModState.Enabled} pc={(pc != null)} body={(bd != null)} " +
                        $"world={(WorldGeneration.world != null)} pool={poolN} items={itemN} paused={IsPaused}");
        }
        catch (Exception e) { try { Log.LogWarning($"[DIAG] 心跳异常: {e.Message}"); } catch { } }
    }

    internal static bool IsPaused
    {
        get
        {
            try { return PauseHandler.paused; } catch { return false; }
        }
    }

    private void OnDestroy()
    {
        EngineTick.LogDestroy("插件对象 Plugin");
    }
}
