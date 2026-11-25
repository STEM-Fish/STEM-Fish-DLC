using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace STEMFishDLC;

// ============================================================================
//  CharacterSelect - 把 SF-07 做成原版选角界面里的第 4 个角色
//  ---------------------------------------------------------------------------
//  反汇编确认（Assembly-CSharp.dll / CasualtiesUnknown 1.0）：
//    · PreRunScript.characterSelected      static int，当前选中的角色
//    · PreRunScript.charImages / charInnerImages / charPodiums
//                                          List<Image>，选角界面那三个立绘
//    · PreRunScript.UpdateCharacterText()  用 Locale.GetOther("char{n}name/desc/quirks")
//                                          和 Skills.BaseSkills(n) 填四个文本
//    · PlayerCamera.Awake() → SpawnCharacter(characterSelected)
//    · PlayerCamera.SpawnCharacter(int)    0=Experiment / 1=Milky / else=Dune
//    · SaveInfo.character                  存档里记的角色号（读档走 LoadRun 恢复）
//
//  所以「第 4 个角色」= 索引 3：
//    · BaseSkills(3)       → 我们的 12/12/18
//    · SpawnCharacter(3)   → 仍然生成 Experiment 骨架（所有肢体/动画都齐），
//                            打一个 Tag 组件，FishSkin 只对带 Tag 的身体换皮
//    · UpdateCharacterText → chr==3 时用我们自己的角色说明覆盖
//    · 选角入口在 NewRunPanel（原版 CharSelect 页被我们的模式页盖住了）
//
//  ★ 关键：SF-07 的 Body.charType 保持 0（Experiment）。
//    charType 在 Body.Eat / WearWearable / HandleBody / WakeUp 里有一堆分支，
//    3 会掉进 else 分支（按 Dune 处理，比如吃水果呕吐）。外观和身份用 Tag 区分，
//    机制继续走 EXPie，只在我们明确要改的地方（可可碱）单独加。
// ============================================================================

public static class CharacterSelect
{
    public const int Sf07 = 3;
    public const int VanillaCount = 3;

    public static bool Enabled = true;

    // 挂在 SF-07 的根物体上（不是 Body 上，Body 可能被替换）
    public class Tag : MonoBehaviour { }

    public class CharInfo
    {
        public string name;
        public string desc;
        public string quirks;
    }

    private static CharInfo _sf07;
    private static Sprite _portrait;
    private static Sprite _outline;

    public static Sprite Portrait => _portrait;
    public static Sprite Outline => _outline;
    public static CharInfo Sf07Info => _sf07;

    // ==========================================================================
    //  初始化
    // ==========================================================================
    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Character", "Enabled", true,
                "把 SF-07 作为独立角色加入选角界面（关闭后 SF-07 不再可选，原版三个角色不变）").Value;
        }
        catch { Enabled = true; }

        LoadText();
        LoadArt();

        try
        {
            var harmony = new Harmony("com.stemfish.dlc.character");

            // 1) 属性
            var baseSkills = AccessTools.Method(typeof(Skills), "BaseSkills", new[] { typeof(int) });
            if (baseSkills != null)
                harmony.Patch(baseSkills, postfix: new HarmonyMethod(
                    typeof(CharacterSelect).GetMethod("BaseSkills_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));

            // 2) 角色说明
            var upd = AccessTools.Method(typeof(PreRunScript), "UpdateCharacterText");
            if (upd != null)
                harmony.Patch(upd, postfix: new HarmonyMethod(
                    typeof(CharacterSelect).GetMethod("UpdateText_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));

            // 3) 生成：chr 3 走 Experiment 骨架 + Tag
            var spawn = AccessTools.Method(typeof(PlayerCamera), "SpawnCharacter", new[] { typeof(int) });
            if (spawn != null)
                harmony.Patch(spawn, prefix: new HarmonyMethod(
                    typeof(CharacterSelect).GetMethod("Spawn_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));

            // 4) 记住选择（原版 Awake 每次都把 characterSelected 清 0）
            var start = AccessTools.Method(typeof(PreRunScript), "Start");
            if (start != null)
                harmony.Patch(start, postfix: new HarmonyMethod(
                    typeof(CharacterSelect).GetMethod("Start_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));

            var startRun = AccessTools.Method(typeof(PreRunScript), "StartRun");
            if (startRun != null)
                harmony.Patch(startRun, prefix: new HarmonyMethod(
                    typeof(CharacterSelect).GetMethod("StartRun_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));

            var sel = AccessTools.Method(typeof(PreRunScript), "SelectCharacter", new[] { typeof(int) });
            if (sel != null)
                harmony.Patch(sel, postfix: new HarmonyMethod(
                    typeof(CharacterSelect).GetMethod("Select_Postfix", BindingFlags.NonPublic | BindingFlags.Static)));

            // 5) 联机模组（Together）：CharacterSpawn.ClampChar 把角色夹在 0..2，
            //    不放开的话联机里选 SF-07 会被当成 Dune 同步。
            TryPatchMpClamp(harmony);

            int n = (Harmony.GetPatchInfo(baseSkills)?.Postfixes?.Count ?? 0) +
                    (Harmony.GetPatchInfo(spawn)?.Prefixes?.Count ?? 0);
            Plugin.Log.LogInfo($"[STEMFishDLC] SF-07 角色接入完成 | 补丁点={n} | 立绘={(_portrait != null ? "有" : "缺")}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[STEMFishDLC] SF-07 角色接入失败: {e}");
        }
    }

    private static void TryPatchMpClamp(Harmony harmony)
    {
        try
        {
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "CasualtiesMP") continue;
                t = a.GetType("Together.CharacterSpawn");
                break;
            }
            if (t == null) return;

            var m = t.GetMethod("ClampChar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) return;

            harmony.Patch(m, prefix: new HarmonyMethod(
                typeof(CharacterSelect).GetMethod("ClampChar_Prefix", BindingFlags.NonPublic | BindingFlags.Static)));
            Plugin.Log.LogInfo("[STEMFishDLC] 检测到 CasualtiesMP：已放开 ClampChar 的 0..2 限制");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 放开 MP 角色限制失败（联机里 SF-07 会显示成 Dune）: {e.Message}");
        }
    }

    // ==========================================================================
    //  文本 / 立绘
    // ==========================================================================
    private static void LoadText()
    {
        try
        {
            string path = Lang.File("chars");
            if (File.Exists(path))
            {
                _sf07 = JsonConvert.DeserializeObject<CharInfo>(File.ReadAllText(path));
                Plugin.Log.LogInfo($"[STEMFishDLC] SF-07 角色说明已载入（{Path.GetFileName(path)}）");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 角色说明读取失败: {e.Message}");
        }

        if (_sf07 == null)
        {
            _sf07 = Lang.IsEnglish
                ? new CharInfo
                {
                    name = "[SF-07] \"STEM-Fish\"",
                    desc = "[Class] -- \"Canid, ichthyic modification\"\n[Last words] -- \"...I'm here. What they want me to see is below.\"\n[Odds] -- \"0.00091%\"\n[Note] -- \"No name in the file. Only a sample number.\"",
                    quirks = "[Canid Mark] -- Your gut can barely process theobromine. Coffee, chocolate and energy drinks hit you far harder.\n[Bioluminescence] -- In an emergency, you are the light source.\n[Gill Structure] -- You can breathe underwater and rest in wet places.\n[Chip] -- There is a chip in your head. It is not on your side.",
                }
                : new CharInfo
                {
                    name = "[SF-07] “STEM-Fish”",
                    desc = "[生物分类] —— “犬科 · 鱼形改造”\n[临行之言] —— “……我到了。他们想让我看到的东西，在下面。”\n[成功几率] —— “0.00091%”\n[备注] —— “档案里没有名字，只有一个样本编号”",
                    quirks = "[犬科烙印] —— 你的胃几乎无法处理可可碱。咖啡、巧克力、能量饮料对你的毒性远比标准实验体严重。\n[生物荧光] —— 在紧急情况下，你自己就是一个光源。\n[鳃状结构] —— 你可以在水下呼吸，也习惯在潮湿的地方休息。\n[芯片] —— 你脑子里有一枚芯片。它不站在你这边。",
                };
            Plugin.Log.LogInfo("[STEMFishDLC] 使用内置 SF-07 角色说明");
        }
    }

    private static void LoadArt()
    {
        try
        {
            string dir = Path.Combine(FishSkin.PackDir, "Loading");
            _portrait = LoadSprite(Path.Combine(dir, "char4.png"));
            _outline = LoadSprite(Path.Combine(dir, "char4outline.png"));
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] SF-07 选角立绘载入失败: {e.Message}");
        }
    }

    private static Sprite LoadSprite(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(tex); return null; }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = Path.GetFileNameWithoutExtension(path);
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sp.name = tex.name;
            return sp;
        }
        catch { return null; }
    }

    // 取第 i 个角色的立绘（0..2 用原版 charImages；3 用我们的）
    public static Sprite PortraitOf(int i)
    {
        if (i == Sf07) return _portrait;
        try
        {
            var pre = PreRunScript.instance;
            if (pre != null && pre.charImages != null && i >= 0 && i < pre.charImages.Count)
            {
                var img = pre.charImages[i];
                if (img != null) return img.sprite;
            }
        }
        catch { }
        return null;
    }

    public static string NameOf(int i)
    {
        if (i == Sf07) return _sf07 != null ? _sf07.name : "SF-07";
        try
        {
            var s = Locale.GetOther("char" + i + "name");
            if (!string.IsNullOrEmpty(s)) return s;
        }
        catch { }
        return "SAW-0" + (i + 1);
    }

    // ==========================================================================
    //  选择 / 判定
    // ==========================================================================
    public static int Current
    {
        get { try { return PreRunScript.characterSelected; } catch { return 0; } }
    }

    public static void Select(int chr)
    {
        try
        {
            chr = Mathf.Clamp(chr, 0, Sf07);
            PreRunScript.characterSelected = chr;
            PlayerPrefs.SetInt("stemfish.char", chr);

            CallUpdateText(PreRunScript.instance);

            try { PlayerCamera.PlayUISound("click", 1f); } catch { }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 选择角色失败: {e.Message}");
        }
    }

    // 教程模式：原版教学关，保持 EXPie（不发装备、不换皮、不改属性）
    public static bool IsTutorial()
    {
        try
        {
            var w = WorldGeneration.world;
            return w != null && (int)w.biomeOverride == 1;
        }
        catch { return false; }
    }

    /// <summary>当前玩家是不是 SF-07（HUD / 台词 / 面板这些"角色专属功能"的门槛）</summary>
    public static bool IsPlayerSf07()
    {
        try
        {
            var pc = PlayerCamera.main;
            return pc != null && IsSf07(pc.body);
        }
        catch { return false; }
    }

    public static bool IsSf07(Body body)
    {
        if (body == null) return false;
        if (IsTutorial()) return false;
        try { if (body.GetComponentInParent<Tag>() != null) return true; } catch { }

        // 兜底：body 可能在主菜单 / 教程场景里就生成好了（Spawn_Prefix 赶不上），
        // 这时只认「选的是 3」+「这个 body 就是玩家」。
        try
        {
            var pc = PlayerCamera.main;
            if (pc != null && ReferenceEquals(pc.body, body))
                return PreRunScript.characterSelected == Sf07;
        }
        catch { }
        return false;
    }

    // ==========================================================================
    //  Harmony
    // ==========================================================================
    // UpdateCharacterText 是 private（probe 里看着像 public，实际编译不过），走反射
    private static void CallUpdateText(PreRunScript pre)
    {
        if (pre == null) return;
        try
        {
            var m = AccessTools.Method(typeof(PreRunScript), "UpdateCharacterText");
            if (m != null) m.Invoke(pre, null);
        }
        catch { }
    }

    private static void BaseSkills_Postfix(int __0, ref int[] __result)
    {
        if (!Enabled) return;
        if (__0 != Sf07) return;
        __result = new[] { PlayerSkills.STR, PlayerSkills.RES, PlayerSkills.INT };
    }

    private static void UpdateText_Postfix(PreRunScript __instance)
    {
        if (!Enabled || __instance == null) return;
        try
        {
            if (PreRunScript.characterSelected != Sf07) return;
            if (_sf07 == null) return;

            if (__instance.charSelectName != null) __instance.charSelectName.text = _sf07.name;
            if (__instance.charSelectNotes != null) __instance.charSelectNotes.text = _sf07.desc;
            if (__instance.charSelectQuirks != null)
            {
                __instance.charSelectQuirks.text = _sf07.quirks;
                // SF-07 的特性有九条，原版字号会顶到框外 → 单独调小（原版角色不受影响）
                __instance.charSelectQuirks.fontSize = 15f;
                __instance.charSelectQuirks.enableWordWrapping = true;
            }
        }
        catch { }
    }

    private static bool Spawn_Prefix(int __0, ref GameObject __result)
    {
        if (!Enabled || __0 != Sf07) return true;

        try
        {
            var go = Utils.Create("Special/Characters/Experiment", Vector2.zero, 0f);
            if (go == null) return true;           // 创建失败 → 交回原版（会生成 Dune）

            go.AddComponent<Tag>();

            var body = go.GetComponentInChildren<Body>(true);
            if (body != null)
            {
                try { body.skills.Setup(Sf07); } catch { }
            }

            __result = go;
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] SF-07 生成失败，回退原版: {e.Message}");
            return true;
        }
    }

    private static void Start_Postfix(PreRunScript __instance)
    {
        if (!Enabled || __instance == null) return;
        try
        {
            // 没主动选过就跟随上次实际玩的角色（lastchar 由 PlayerCamera.Awake 写入）
            int fallback = PlayerPrefs.GetInt("lastchar", Sf07);
            int saved = PlayerPrefs.GetInt("stemfish.char", fallback);
            if (saved < 0 || saved > Sf07) saved = Sf07;
            Picking = false;                   // 回主菜单一定要复位，否则 StartRun 会被误拦
            PreRunScript.characterSelected = saved;
            CallUpdateText(__instance);
            EnsureFourthSlot(__instance);     // 把 SF-07 插进原版选角页第 4 格
            InjectMenuButton(__instance);     // 主菜单「选择角色」独立入口
            HookPresetsStart(__instance);     // 预设页确认 → 直接开局（跳过选角页）
            ApplyMenuPortrait(saved);         // 主菜单立绘跟随上次选择
        }
        catch { }
    }

    // 读档 / 场景重建后补 Tag：存档里 character=3，但 body 是新生成的，
    // Spawn_Prefix 只在 PlayerCamera.Awake 时跑，读档路径不一定经过。
    private static int _tick;
    private static int _loggedFix;

    public static void Tick()
    {
        if (!Enabled) return;
        _tick++;

        // PreRunScript.Awake 每次回主菜单都会把 characterSelected 清 0；
        // 如果玩家上次选的是 SF-07，进游戏后这里补回来（不依赖 Start 的 patch 是否成功）。
        try
        {
            if (PreRunScript.characterSelected != Sf07 &&
                PlayerPrefs.GetInt("stemfish.char", Sf07) == Sf07 &&
                PlayerCamera.main != null)
            {
                PreRunScript.characterSelected = Sf07;
                if (_loggedFix < 3)
                {
                    _loggedFix++;
                    Plugin.Log.LogInfo("[STEMFishDLC] 选角校正：characterSelected → 3（此前被主菜单重置）");
                }
            }
        }
        catch { }

        try
        {
            var pre = PreRunScript.instance;
            if (pre != null && pre.runSettingsScreen != null)
            {
                PickingGuard(pre);
                if (pre.runSettingsScreen.activeInHierarchy) MaintainFourthSlot(pre);
                else
                {
                    if (_startBtn != null) { _startBtn.interactable = true; _startBtn = null; }
                    if (Picking) { Picking = false; RestoreLayout(); }
                }
            }
        }
        catch { }

        if (_tick % 20 != 0) return;        // 每秒约 3 次，别每帧 GetComponentInParent
        try
        {
            if (PreRunScript.characterSelected != Sf07) return;
            var pc = PlayerCamera.main;
            var body = pc != null ? pc.body : null;
            if (body == null) return;
            if (body.GetComponentInParent<Tag>() != null) return;

            var root = body.transform.root != null ? body.transform.root.gameObject : body.gameObject;
            root.AddComponent<Tag>();
            // ★ 不要在这里 Setup 技能：读档时 body 带着已经练过的等级，
            //   Setup 会把它重置回 0 经验。技能只在 Spawn_Prefix（真正新生成）里设。
            Plugin.Log.LogInfo("[STEMFishDLC] 读档/重建后补上 SF-07 标记");
        }
        catch { }
    }

    private static void Select_Postfix(int __0)
    {
        try { PlayerPrefs.SetInt("stemfish.char", Mathf.Clamp(__0, 0, Sf07)); } catch { }
    }

    private static bool ClampChar_Prefix(int __0, ref int __result)
    {
        if (!Enabled || __0 != Sf07) return true;
        __result = Sf07;
        return false;
    }

    // ==========================================================================
    //  主菜单「选择角色」——自建面板
    //  ---------------------------------------------------------------------------
    //  不克隆任何原版组件、不 patch StartRun、不碰 runSettingsScreen：
    //    · 按钮 = 纯 Image + Button + TMP，挂在 PreRunScript 根节点右上角
    //    · 面板 = 自己画的全屏半透明层，4 张立绘卡 + 返回/确认
    //    · 选角 = 写 characterSelected + PlayerPrefs，再 ApplyMenuPortrait 换主菜单立绘
    //  换图后调 MenuArt.Invalidate()：轮廓和文字位置会在下一帧按新立绘重算。
    // ==========================================================================
    private const string CharBtnName = "STEMFishDLC_CharacterButton";

    // ------------------------------------------------------------------
    //  第 4 格：克隆原版第 3 个立绘（含台座），换 SF-07 的图，
    //  位置用「第 3 格 + 一格间距」外推；角色专属子图层关掉防遮挡。
    // ------------------------------------------------------------------
    private static void EnsureFourthSlot(PreRunScript pre)
    {
        try
        {
            if (pre.charImages == null || pre.charImages.Count == 0) return;
            if (pre.charImages.Count > Sf07) return;
            if (pre.charImages.Count < 3) return;

            var src = pre.charImages[2];
            if (src == null) return;

            var go = UnityEngine.Object.Instantiate(src.gameObject, src.transform.parent);
            go.name = "Char4_SF07";
            go.SetActive(true);

            var rt = go.transform as RectTransform;
            var srcRt = src.transform as RectTransform;
            if (rt != null && srcRt != null)
            {
                var r0 = pre.charImages[0].rectTransform;
                var r2 = pre.charImages[2].rectTransform;
                Vector3 step = (r2.position - r0.position) * 0.5f;
                if (Mathf.Abs(step.x) < 10f)
                {
                    float w = r2.rect.width > 20f ? r2.rect.width : 300f;
                    step = new Vector3(w * 1.05f, 0f, 0f);
                }
                rt.position = new Vector3(r2.position.x + step.x, r2.position.y, r2.position.z);

                // rect 改成 char4 自己的比例（克隆体沿用 Dune 的窄长框会被压瘦）
                if (_portrait != null && _portrait.rect.height > 1f)
                {
                    float h = rt.rect.height > 1f ? rt.rect.height : srcRt.rect.height;
                    if (h > 1f) rt.sizeDelta = new Vector2(h * (_portrait.rect.width / _portrait.rect.height), h);
                }
            }

            var img = go.GetComponent<Image>();
            if (img != null && _portrait != null)
            {
                img.sprite = _portrait;
                img.preserveAspect = true;
            }

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() => Select(Sf07));
                btn.interactable = true;            // 不受原版 wonchar 解锁限制
            }

            pre.charImages.Add(img);

            var podium = AddClonedEntry(pre.charPodiums, src.transform, go.transform);
            var inner = AddClonedEntry(pre.charInnerImages, src.transform, go.transform);

            // ★ inner 直接关掉，而且**不**把它挪到父级去。
            //   原版三个角色的 inner 是"描边"图（空心）；我们的 char4outline 是实心剪影，
            //   放上面会把整只立绘盖住、放下面又会在 Slide 层多出一个游离的大白影
            //   （那个白影正是选角页发灰的元凶之一）。没有描边图就不画，主图颜色照旧
            //   由原版 HandleCharacterSelect + MaintainFourthSlot 控制。
            if (inner != null) inner.enabled = false;

            foreach (var sub in go.GetComponentsInChildren<Image>(true))
            {
                if (sub == null || sub == img) continue;
                if (podium != null && sub == podium) continue;
                sub.enabled = false;
            }

            Plugin.Log.LogInfo($"[STEMFishDLC] 第 4 格已插入（共 {pre.charImages.Count}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 插入第 4 格失败: {e.Message}");
        }
    }

    private static Image AddClonedEntry(List<Image> list, Transform srcRoot, Transform newRoot)
    {
        try
        {
            if (list == null || list.Count < 3) return null;
            var old = list[2];
            if (old == null) { list.Add(null); return null; }

            var parts = new List<string>();
            var t = old.transform;
            while (t != null && t != srcRoot) { parts.Insert(0, t.name); t = t.parent; }
            if (parts.Count == 0) { list.Add(old); return old; }

            var cur = newRoot;
            foreach (var part in parts) { if (cur == null) break; cur = cur.Find(part); }
            var img = cur != null ? cur.GetComponent<Image>() : null;
            list.Add(img);
            return img;
        }
        catch { try { list?.Add(null); } catch { } return null; }
    }

    private static Transform FindCharSelectRoot(PreRunScript pre)
    {
        try
        {
            if (pre.charImages != null && pre.charImages.Count > 0 && pre.charImages[0] != null)
            {
                var t = pre.charImages[0].transform;
                while (t != null)
                {
                    if (t.name == "CharSelect") return t;
                    if (pre.runSettingsScreen != null && t == pre.runSettingsScreen.transform) break;
                    t = t.parent;
                }
            }
        }
        catch { }
        try
        {
            if (pre.runSettingsScreen != null)
                return FindDeep(pre.runSettingsScreen.transform, "CharSelect");
        }
        catch { }
        return null;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

    // 原版选角页可见时：守住第 4 格的"可选"视觉。
    // 原版 HandleCharacterSelect 拿 wonchar2 判断 i=3 → 当成未解锁压灰。
    private static void MaintainFourthSlot(PreRunScript pre)
    {
        try
        {
            int cnt = pre.charImages != null ? pre.charImages.Count : 0;
            if (cnt > Sf07)
            {
                bool sel = Current == Sf07;
                var i3 = pre.charImages[Sf07];
                if (i3 != null) i3.color = sel ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);

                if (pre.charInnerImages != null && pre.charInnerImages.Count > Sf07)
                {
                    var inner = pre.charInnerImages[Sf07];
                    if (inner != null) inner.color = sel ? Color.white : Color.black;
                }
                if (pre.charPodiums != null && pre.charPodiums.Count > Sf07)
                {
                    var pod = pre.charPodiums[Sf07];
                    if (pod != null) pod.color = new Color(1f, 1f, 1f, sel ? 1f : 0f);
                }

            // Start 按钮文本每帧守一次：原版 AdaptiveButton 可能会把文字刷回去
            if (_startBtn != null)
            {
                string want = Lang.T("选择角色", "SELECT CHARACTER");
                foreach (var t in _startBtn.GetComponentsInChildren<TMP_Text>(true))
                    if (t != null && t.text != want) t.text = want;
            }
            }
        }
        catch { }
    }


    public static void ApplyMenuPortrait(int chr)
    {
        try
        {
            var img = MenuArt.CurrentImage;
            if (img == null) return;

            Component blink = null;
            foreach (var c in img.GetComponents<Component>())
                if (c != null && c.GetType().Name == "MenuExperimentBlink") { blink = c; break; }
            if (blink == null) return;

            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = blink.GetType();

            var arr = t.GetField("sprites", F)?.GetValue(blink) as Sprite[];
            if (arr == null || arr.Length == 0) return;

            var reserve = t.GetField("reserveCharSprites", F)?.GetValue(blink) as Sprite[];
            if (reserve != null && reserve.Length >= 9)
            {
                int b = chr == 1 ? 3 : chr == 2 ? 6 : 0;
                for (int i = 0; i < arr.Length && i < 3; i++)
                    if (reserve[b + i] != null) arr[i] = reserve[b + i];
            }

            try { t.GetField("chr", F)?.SetValue(blink, chr); } catch { }
            if (arr[0] != null) img.sprite = arr[0];

            // outline/shine：原版角色直接用原版配的那套（零偏移）；
            // SF-07 交给 MenuArt（下一帧按我们的图生成）。
            int idx = chr == 1 ? 1 : chr == 2 ? 2 : 0;
            var outs = t.GetField("outlineSprites", F)?.GetValue(blink) as Sprite[];
            var oimg = t.GetField("outline", F)?.GetValue(blink) as Image;
            if (chr != Sf07 && oimg != null && outs != null && idx < outs.Length && outs[idx] != null)
                oimg.sprite = outs[idx];
            var shines = t.GetField("shineSprites", F)?.GetValue(blink) as Sprite[];
            var simg = t.GetField("shine", F)?.GetValue(blink) as Image;
            if (simg != null && shines != null && idx < shines.Length && shines[idx] != null)
                simg.sprite = shines[idx];

            if (chr == Sf07) MenuArt.Invalidate();
            Plugin.Log.LogInfo($"[STEMFishDLC] 主菜单立绘 → 角色 {chr}（sprite={img.sprite?.name}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 切换主菜单立绘失败: {e.Message}");
        }
    }

    // ==========================================================================
    //  主菜单「选择角色」——独立入口
    //  ---------------------------------------------------------------------------
    //  ★ 绝不改任何原版按钮的监听（上次「进入游戏」失效就是改了 Start 的 UnityEvent：
    //    那个按钮同时也是原版「预设页→选角页→开始游戏」的确认键）。
    //    这次只做两件事：
    //      · 打开时把原版 Start 设成 interactable=false（防误触开局）
    //      · 加一个我们自己的「确认」按钮，点它关页 + 换主菜单立绘
    //    关闭时把 Start 的 interactable 恢复。
    // ==========================================================================
    private static readonly List<GameObject> _screenChildren = new List<GameObject>();
    private static readonly List<bool> _screenChildActive = new List<bool>();
    public static bool Picking { get; private set; }          // 只在「选择角色」入口打开时为 true
    private static Button _startBtn;

    public static void InjectMenuButton(PreRunScript pre)
    {
        if (!Enabled || pre == null || pre.loadButton == null) return;
        try
        {
            var parent = pre.transform;
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == CharBtnName) return;

            var go = new GameObject(CharBtnName, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(200f, 54f);
            rt.anchoredPosition = new Vector2(-28f, -96f);
            rt.localScale = Vector3.one;

            var img = go.GetComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = true;

            try
            {
                var srcImg = pre.loadButton.GetComponent<Image>();
                if (srcImg != null && srcImg.sprite != null)
                {
                    img.sprite = srcImg.sprite;
                    img.type = srcImg.type;
                    img.preserveAspect = srcImg.preserveAspect;
                    img.pixelsPerUnitMultiplier = srcImg.pixelsPerUnitMultiplier;
                }
            }
            catch { }

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.ColorTint;
            var bc = btn.colors;
            bc.normalColor = Color.white;
            bc.highlightedColor = new Color(1.10f, 1.10f, 1.10f, 1f);
            bc.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            bc.selectedColor = Color.white;
            bc.disabledColor = new Color(0.55f, 0.55f, 0.55f, 1f);
            bc.fadeDuration = 0.08f;
            btn.colors = bc;
            btn.onClick.AddListener(OpenPicker);

            var t = NewText(rt, "Label", Lang.T("选择角色", "CHARACTER"), 18f, Color.white, TextAlignmentOptions.Center);
            var trt = t.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            go.transform.SetAsLastSibling();
            Plugin.Log.LogInfo("[STEMFishDLC] 主菜单「选择角色」按钮已注入");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 注入「选择角色」按钮失败: {e.Message}");
        }
    }

    public static void OpenPicker()
    {
        var pre = PreRunScript.instance;
        if (pre == null) return;
        try
        {
            EnsureFourthSlot(pre);

            var root = FindCharSelectRoot(pre);
            if (root == null) { Plugin.Log.LogWarning("[STEMFishDLC] 找不到原版 CharSelect 页"); return; }

            // ★ 进过一次世界再回主菜单时，这些根节点都还活着（DontDestroyOnLoad）：
            //   模式面板 / MOD 管理 / 战术终端 / 模式弹窗（后三个都是全屏半透明 + 吃射线）。
            //   任何一个没关干净，选角页就会被压暗、点什么都没反应。
            try { ModMenu.ForceHide(); } catch { }
            try { NewRunPanel.ForceHide(); } catch { }
            try { DlcPanel.ForceHide(); } catch { }
            try { Jammer.ForceHide(); } catch { }

            if (pre.runSettingsScreen != null)
            {
                pre.runSettingsScreen.SetActive(true);

                // ★ 先记下每个子页面原来的激活状态，关闭时必须逐一还原。
                //   否则「进入游戏 → 预设页」会因为 Presets 被我们关掉而只显示选角页。
                var t = pre.runSettingsScreen.transform;
                bool directChild = root.parent == t;

                if (directChild)
                {
                    _screenChildren.Clear();
                    _screenChildActive.Clear();
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var ch = t.GetChild(i);
                        if (ch == null) continue;
                        _screenChildren.Add(ch.gameObject);
                        _screenChildActive.Add(ch.gameObject.activeSelf);
                        ch.gameObject.SetActive(ch == root);
                    }
                }
                else
                {
                    // ★ CharSelect 不是直接子节点时，绝不能按"只留 root"去裁剪 ——
                    //   那样会把 CharSelect 的祖先一起关掉，屏幕剩下一层背景 = 「灰 + 点不动」。
                    //   这时只把 Presets 收起来（它会盖住页面），其余保持原样。
                    Plugin.Log.LogWarning($"[STEMFishDLC] CharSelect 的父节点是 {root.parent?.name}（不是 runSettingsScreen），" +
                                           "改为只收 Presets");
                    var presets = FindDeep(t, "Presets");
                    if (presets != null && presets != root && !root.IsChildOf(presets))
                    {
                        _screenChildren.Clear();
                        _screenChildActive.Clear();
                        _screenChildren.Add(presets.gameObject);
                        _screenChildActive.Add(presets.gameObject.activeSelf);
                        presets.gameObject.SetActive(false);
                    }
                }
                t.SetAsLastSibling();
            }
            root.gameObject.SetActive(true);
            ForceInteractive(root.gameObject);
            DumpOverlays(pre);          // 页面已经显示出来了，这时列的就是用户实际看到的图层
            DumpPage(pre);              // 结构：active / CanvasGroup / raycast

            var bottom = FindDeep(root, "Bottom");
            if (bottom != null) bottom.gameObject.SetActive(true);

            // 原版 Start 只禁用、不改监听（它是原版流程共用的确认键）
            if (_startBtn == null) _startBtn = FindStartButton(root, bottom);
            if (_startBtn != null)
            {
                Plugin.Log.LogInfo($"[STEMFishDLC] 选角确认键 = {_startBtn.name}");
                _startBtn.interactable = true;
                string label = Lang.T("选择角色", "SELECT CHARACTER");
                foreach (var t in _startBtn.GetComponentsInChildren<TMP_Text>(true)) if (t != null) t.text = label;
                foreach (var t in _startBtn.GetComponentsInChildren<UnityEngine.UI.Text>(true)) if (t != null) t.text = label;
            }

            CallUpdateText(pre);

            // 语言切换后原版设置页的 CanvasGroup/交互状态可能停在关闭态，
            // 直接 SetActive(true) 会得到"灰且点不动"的一页。这里强制拉回正常。
            // 进过一次世界再回主菜单时，这些覆盖层可能被原版重新打开（内容警告/教程提示/片尾）
            try
            {
                if (pre.contentWarning != null) pre.contentWarning.SetActive(false);
                if (pre.tutorialWarning != null) pre.tutorialWarning.SetActive(false);
                if (pre.creditsScreen != null) pre.creditsScreen.SetActive(false);
            }
            catch { }

            ForceInteractive(pre != null ? pre.runSettingsScreen : null);

            SaveAndLayout(pre);
            try { if (pre.cover != null) pre.cover.color = new Color(0f, 0f, 0f, 0f); } catch { }
            Picking = true;
            _beatAt = 0f;
            _beatFrames = 0;
            Plugin.Log.LogInfo($"[STEMFishDLC] 打开原版选角页（当前 {Current}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 打开选角页失败: {e.Message}");
        }
    }

    public static void ClosePicker(bool apply)
    {
        Picking = false;
        RestoreLayout();

        // 还原 runSettingsScreen 的子页面激活状态（否则原版预设页会被我们关掉的）
        try
        {
            for (int i = 0; i < _screenChildren.Count; i++)
                if (_screenChildren[i] != null) _screenChildren[i].SetActive(_screenChildActive[i]);
        }
        catch { }
        _screenChildren.Clear();
        _screenChildActive.Clear();

        try
        {
            var pre = PreRunScript.instance;
            if (pre != null && pre.runSettingsScreen != null) pre.runSettingsScreen.SetActive(false);
        }
        catch { }

        if (_startBtn != null) { try { _startBtn.interactable = true; } catch { } }

        try
        {
            if (_coverRay)
            {
                var pre = PreRunScript.instance;
                if (pre != null && pre.cover != null) pre.cover.raycastTarget = true;
                _coverRay = false;
            }
        }
        catch { }

        // 同理：关闭时把主画布/设置页的交互状态复位，避免语言切换后留下灰屏
        try
        {
            var pre = PreRunScript.instance;
            if (pre != null) { ForceInteractive(pre.runSettingsScreen); ForceInteractive(pre.mainCanvas != null ? pre.mainCanvas.gameObject : null); }
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null) es.SetSelectedGameObject(null);
        }
        catch { }


        if (apply) ApplyMenuPortrait(Current);
        try { MenuArt.Invalidate(); } catch { }
        Plugin.Log.LogInfo($"[STEMFishDLC] 选角结束（{(apply ? "确认" : "取消")}）：{Current}");
    }

    // 选角页由我们打开时（Picking），Start 只当确认；其他时候（原版"进入游戏→预设→开始"）
    // 原样放行。Picking 在 ClosePicker / Tick 兜底 / Start_Postfix 三处都会清。
    private static bool StartRun_Prefix()
    {
        if (!Picking) return true;
        ClosePicker(true);
        return false;
    }

    private static readonly List<Vector3> _origPos = new List<Vector3>();
    private static readonly List<Vector3> _origScale = new List<Vector3>();
    private static bool _layoutSaved;

    // 选角页打开时把 4 格等分排开（原版 3 格的宽度放不下第 4 个），关闭时还原。
    // 第 0 格就是主菜单那只立绘，所以必须存原始位置/缩放再恢复，否则主界面会被挪歪。
    // 诊断：列出主画布下「接近全屏 + 不透明度够高」的图形。
    // 只在我们打开选角页时跑一次，正常情况不会刷日志。
    private static void DumpOverlays(PreRunScript pre)
    {
        try
        {
            var host = pre.mainCanvas != null ? pre.mainCanvas.transform : pre.transform;
            if (host == null) return;

            float sw = Mathf.Max(1f, Screen.width);
            float sh = Mathf.Max(1f, Screen.height);
            int n = 0;
            var v = new Vector3[4];

            foreach (var g in host.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                if (g == null || !g.isActiveAndEnabled) continue;
                if (g.color.a < 0.30f) continue;

                var rt = g.rectTransform;
                rt.GetWorldCorners(v);
                float w = Mathf.Abs(v[2].x - v[0].x);
                float h = Mathf.Abs(v[2].y - v[0].y);
                if (w < sw * 0.85f || h < sh * 0.85f) continue;      // 只关心全屏级

                if (n++ >= 8) break;
                Plugin.Log.LogInfo($"[STEMFishDLC] 全屏图层：{PathOf(rt, host)} " +
                                   $"{g.GetType().Name} color={g.color} raycast={g.raycastTarget}");
            }
            if (n == 0) Plugin.Log.LogInfo("[STEMFishDLC] 全屏图层：无（干净）");
        }
        catch { }
    }

    private static string PathOf(Transform t, Transform stop)
    {
        var parts = new List<string>();
        while (t != null && t != stop && parts.Count < 8)
        {
            parts.Insert(0, t.name);
            t = t.parent;
        }
        return string.Join("/", parts);
    }

    // ==========================================================================
    //  选角页守护：每帧把可疑的遮罩/暂停状态压回去，并留一条不需要点击的退路
    //  ---------------------------------------------------------------------------
    //  「灰 + 锁」有两种完全不同的成因，心跳日志用来区分：
    //    · 心跳一直在打，但画面灰  → 有东西在压暗/挡射线（cover / CanvasGroup）
    //    · 心跳突然断了            → 主线程卡死，画面停在最后一帧
    // ==========================================================================
    private static bool _coverRay;
    private static float _beatAt;
    private static int _beatFrames;

    private static void PickingGuard(PreRunScript pre)
    {
        if (!Picking || pre == null) return;

        _beatFrames++;

        // ---- 救援 1：时间被停住（表现就是画面定格、什么都不动）----
        if (Time.timeScale <= 0.001f)
        {
            Time.timeScale = 1f;
            Plugin.Log.LogWarning("[STEMFishDLC] 选角页：timeScale=0，已强制恢复为 1");
        }

        // ---- 救援 2：PreRunScript.cover 是整屏淡入淡出层 ----
        //   · color 半透明黑 → 画面"灰"
        //   · raycastTarget=true → 即使 alpha=0 也会吃掉所有点击（经典坑）
        try
        {
            if (pre.cover != null)
            {
                if (pre.cover.color.a > 0.001f)
                {
                    pre.cover.color = new Color(0f, 0f, 0f, 0f);
                    Plugin.Log.LogInfo("[STEMFishDLC] 选角页：cover 遮罩已清空");
                }
                if (pre.cover.raycastTarget)
                {
                    _coverRay = true;
                    pre.cover.raycastTarget = false;
                    Plugin.Log.LogInfo("[STEMFishDLC] 选角页：cover.raycastTarget 已关闭（它默认会吃掉整屏点击）");
                }
            }
        }
        catch { }

        // ---- 救援 3：光标。从世界退回主菜单时可能还是 Locked + 隐藏，
        //      看着就是"画面在、鼠标没有、点什么都不动" ----
        try
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }
        catch { }

        // ---- 救援 4：整条链路上的 CanvasGroup（含子层级）拉回可交互 ----
        try { ForceInteractiveChain(pre.runSettingsScreen != null ? pre.runSettingsScreen.transform : null); } catch { }

        // ---- 心跳：一秒一条 ----
        if (Time.unscaledTime - _beatAt >= 1f)
        {
            _beatAt = Time.unscaledTime;
            string es = "无";
            try
            {
                var sys = UnityEngine.EventSystems.EventSystem.current;
                if (sys != null)
                {
                    var m = sys.currentInputModule;
                    es = (m != null ? m.GetType().Name + (m.enabled ? "" : "(off)") : "无模块");
                }
            }
            catch { }
            string hit = "";
            if (_beatFrames < 400)          // 只在前几秒打，避免刷屏
            {
                try
                {
                    var sys = UnityEngine.EventSystems.EventSystem.current;
                    if (sys != null)
                    {
                        var ped = new UnityEngine.EventSystems.PointerEventData(sys)
                        { position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) };
                        var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                        sys.RaycastAll(ped, hits);
                        for (int i = 0; i < hits.Count && i < 3; i++)
                            hit += $" [{i}] {PathOf(hits[i].gameObject.transform, null)}";
                        if (hits.Count == 0) hit = " 无线索命中";
                    }
                }
                catch { }
            }

            string page = "";
            try
            {
                var rootT = FindCharSelectRoot(pre);
                page = $" · CharSelect={(rootT != null && rootT.gameObject.activeInHierarchy ? "on" : "OFF")}" +
                       $" · Presets={(pre.presetsMenu != null && pre.presetsMenu.gameObject.activeInHierarchy ? "on" : "off")}" +
                       $" · chr={PreRunScript.characterSelected}";
            }
            catch { }

            Plugin.Log.LogInfo($"[STEMFishDLC] 选角心跳：帧 {_beatFrames} · timeScale {Time.timeScale:0.##} " +
                               $"· cover {(pre.cover != null ? pre.cover.color.a : -1f):0.###} · EventSystem {es}" +
                               $"{page}{hit}");
        }

        // ---- 救援 5：原版把 CharSelect 收起来了（点「返回」就会这样）----
        //   RunSettings 自己是 alpha 0.91 + raycastTarget 的近全屏底板：
        //   页面一收，屏幕上只剩这层灰底，点什么都没反应。
        //   这时直接把整个设置页关掉，回到主菜单。
        try
        {
            var page = FindCharSelectRoot(pre);
            if (page != null && !page.gameObject.activeInHierarchy)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 原版把选角页收起了（返回）→ 关闭整个设置页，避免只剩灰色底板");
                ClosePicker(false);
                return;
            }
        }
        catch { }

        // ---- 逃生：ESC / 右键 ------------------------------------------------
        // 鼠标点不动时也能出去，不用重启游戏。
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 选角页手动逃逸（ESC / 右键）→ 关闭选角页");
            ClosePicker(false);
        }
    }

    // 把 from 自身、它的祖先（到 mainCanvas 为止）以及它下面所有 CanvasGroup 拉回正常
    private static void ForceInteractiveChain(Transform from)
    {
        try
        {
            if (from == null) return;

            var pre = PreRunScript.instance;
            Transform stop = pre != null && pre.mainCanvas != null ? pre.mainCanvas.transform : null;

            var t = from;
            int guard = 0;
            while (t != null && guard++ < 12)
            {
                FixGroup(t);
                if (stop != null && t == stop) break;
                t = t.parent;
            }

            foreach (var g in from.GetComponentsInChildren<CanvasGroup>(true)) FixGroup(g.transform);
        }
        catch { }
    }

    private static void FixGroup(Transform t)
    {
        try
        {
            var cg = t.GetComponent<CanvasGroup>();
            if (cg == null) return;
            cg.alpha = 1f;
            cg.interactable = true;
            cg.blocksRaycasts = true;
        }
        catch { }
    }

    // 打开时 dump 一次选角页结构：谁被关、谁的 CanvasGroup 不对、谁在吃射线
    private static void DumpPage(PreRunScript pre)
    {
        try
        {
            var rs = pre.runSettingsScreen != null ? pre.runSettingsScreen.transform : null;
            if (rs == null) return;

            var sb = new System.Text.StringBuilder();
            DumpNode(rs, rs, 0, sb, 0);

            float coverA = -1f;
            try { coverA = pre.cover != null ? pre.cover.color.a : -1f; } catch { }

            string coverInfo = "无";
            try
            {
                if (pre.cover != null)
                    coverInfo = $"{PathOf(pre.cover.transform, null)} a={pre.cover.color.a:0.###} ray={pre.cover.raycastTarget}";
            }
            catch { }
            string listeners = "";
            try
            {
                var bottom = FindDeep(rs, "Bottom");
                if (bottom != null)
                {
                    foreach (var name in new[] { "Start", "Back" })
                    {
                        var t = FindDeep(bottom, name);
                        var b = t != null ? t.GetComponent<Button>() : null;
                        if (b == null) { listeners += $"\n  {name}: 无 Button"; continue; }

                        listeners += $"\n  {name}: interactable={b.interactable} 持久监听 {b.onClick.GetPersistentEventCount()} 个";
                        for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                            listeners += $" [{i}] {b.onClick.GetPersistentTarget(i)}.{b.onClick.GetPersistentMethodName(i)}";
                    }
                }
            }
            catch { }

            Plugin.Log.LogInfo($"[STEMFishDLC] 选角页结构（cover: {coverInfo} · timeScale={Time.timeScale:0.##}）" +
                               $"{listeners}\n{sb}");
        }
        catch { }
    }

    private static void DumpNode(Transform t, Transform root, int depth, System.Text.StringBuilder sb, int count)
    {
        if (t == null || depth > 4 || sb.Length > 3500) return;

        string extra = "";
        try
        {
            var cg = t.GetComponent<CanvasGroup>();
            if (cg != null) extra += $" cg(a={cg.alpha:0.##},i={(cg.interactable ? 1 : 0)},b={(cg.blocksRaycasts ? 1 : 0)})";

            var g = t.GetComponent<UnityEngine.UI.Graphic>();
            if (g != null) extra += $" gfx(a={g.color.a:0.##},r={(g.raycastTarget ? 1 : 0)})";
        }
        catch { }

        sb.Append('\n').Append(new string(' ', depth * 2))
          .Append(t.name)
          .Append(t.gameObject.activeSelf ? "" : "[off]")
          .Append(extra);

        for (int i = 0; i < t.childCount && sb.Length < 3500; i++)
            DumpNode(t.GetChild(i), root, depth + 1, sb, count + 1);
    }

    // 把目标对象上的 CanvasGroup 拉回「可见 + 可交互」，并解除可能的 alpha=0
    private static void ForceInteractive(GameObject go)
    {
        if (go == null) return;
        try
        {
            var cg = go.GetComponent<CanvasGroup>();
            if (cg != null)
            {
                cg.alpha = 1f;
                cg.interactable = true;
                cg.blocksRaycasts = true;
            }
        }
        catch { }
    }

    private static void SaveAndLayout(PreRunScript pre)
    {
        if (pre == null || pre.charImages == null || pre.charImages.Count < 4) return;
        try
        {
            _origPos.Clear();
            _origScale.Clear();
            for (int i = 0; i < 4; i++)
            {
                var im = pre.charImages[i];
                var r = im != null ? im.rectTransform : null;
                _origPos.Add(r != null ? r.position : Vector3.zero);
                _origScale.Add(r != null ? r.localScale : Vector3.one);
            }
            _layoutSaved = true;

            var parent = pre.charImages[0].rectTransform.parent as RectTransform;
            if (parent == null) return;

            // ★ 统一单位：位置/间距全用**世界坐标**，比较缩放时再换回**本地宽度**。
            //   旧代码把「世界单位的格宽」直接除以「本地单位的立绘宽」，
            //   父画布缩放 ≠ 1 时（2K / 高 DPI）系数会大 1.3 倍以上，
            //   日志里表现为「父宽 1728 · 剩余 1959」这种剩余比父宽还大的怪数。
            float sc = Mathf.Max(0.0001f, parent.lossyScale.x);
            float pwLocal = parent.rect.width;
            if (pwLocal < 100f) pwLocal = 1920f;
            float pwWorld = pwLocal * sc;
            float left = parent.position.x - pwWorld * parent.pivot.x;
            float right = left + pwWorld;

            // ★ 第 0 格就是主菜单那只立绘，位置/缩放一律不动。
            //   它一动，MenuArt 抄给轮廓的 RectTransform 就是错的，而且关闭选角页时
            //   只恢复了立绘、没恢复轮廓，于是偏移会一直留着（重开游戏才重算）。
            var r0 = pre.charImages[0].rectTransform;
            float w0World = r0.rect.width * Mathf.Abs(_origScale[0].x) * sc;
            if (w0World < 10f) w0World = 280f * sc;
            float x0 = r0.position.x + w0World * 0.5f;     // 第 0 格右边缘（世界）

            float gap = pwWorld * 0.02f;
            float availWorld = Mathf.Max(60f * sc, right - (x0 + gap) - gap);
            float slotWorld = availWorld / 3f;             // 1/2/3 三格（世界）
            float slotLocal = slotWorld / sc;              // 换算成本地宽度再比

            for (int i = 1; i < 4; i++)
            {
                var r = pre.charImages[i].rectTransform;
                float w = r.rect.width * Mathf.Abs(_origScale[i].x);   // 本地宽度
                if (w < 10f) w = 280f;
                float k = Mathf.Clamp(slotLocal * 0.88f / w, 0.40f, 1f);
                r.localScale = new Vector3(_origScale[i].x * k, _origScale[i].y * k, _origScale[i].z);
                r.position = new Vector3(x0 + gap + slotWorld * (i - 0.5f), r.position.y, r.position.z);
            }
            Plugin.Log.LogInfo($"[STEMFishDLC] 选角 1~3 格重排：父宽 {pwLocal:0}（世界 {pwWorld:0}）· " +
                               $"剩余 {availWorld:0} · 格宽 {slotWorld:0} · 画布缩放 {sc:0.###}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 四格重排失败: {e.Message}"); }
    }

    private static void RestoreLayout()
    {
        if (!_layoutSaved) return;
        _layoutSaved = false;
        try
        {
            var pre = PreRunScript.instance;
            if (pre == null || pre.charImages == null) return;
            for (int i = 1; i < 4 && i < pre.charImages.Count && i < _origPos.Count; i++)
            {
                var im = pre.charImages[i];
                var r = im != null ? im.rectTransform : null;
                if (r == null) continue;
                r.position = _origPos[i];
                r.localScale = _origScale[i];
            }
        }
        catch { }
    }

    // ==========================================================================
    //  原版「预设页 → 选角页 → 开始」流程：在主界面已经选过角色了，
    //  所以把预设页的确认键改成**直接开局**，跳过选角页。
    //  只清按钮自己的持久监听，不 patch StartRun 方法。
    // ==========================================================================
    private static Button _presetsStart;

    private static void HookPresetsStart(PreRunScript pre)
    {
        try
        {
            if (pre == null || pre.presetsMenu == null) return;
            if (_presetsStart != null) return;

            var t = FindDeep(pre.presetsMenu, "Start");
            var btn = t != null ? t.GetComponent<Button>() : null;

            if (btn == null)
            {
                foreach (var b in pre.presetsMenu.GetComponentsInChildren<Button>(true))
                {
                    if (b == null) continue;
                    string txt = "";
                    foreach (var x in b.GetComponentsInChildren<TMP_Text>(true))
                    { if (x == null) continue; if (string.IsNullOrEmpty(txt)) txt = x.text ?? ""; }
                    if (txt.Contains("开始") || txt.Contains("Start") || txt.Contains("下一步") || txt.Contains("Next"))
                    { btn = b; break; }
                }
            }
            if (btn == null) return;

            _presetsStart = btn;
            for (int i = 0; i < btn.onClick.GetPersistentEventCount(); i++)
                btn.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                try { PreRunScript.instance.StartRun(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[STEMFishDLC] 跳过选角直接开局失败: {e.Message}"); }
            });
            Plugin.Log.LogInfo($"[STEMFishDLC] 预设页确认键 → 直接开局（{btn.name}）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 接管预设页按钮失败: {e.Message}");
        }
    }

    private static Button FindStartButton(Transform root, Transform bottom)
    {
        try
        {
            foreach (var b in root.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                string txt = "";
                foreach (var t in b.GetComponentsInChildren<TMP_Text>(true))
                { if (t == null) continue; if (string.IsNullOrEmpty(txt)) txt = t.text ?? ""; }
                if (txt.Contains("开始") || txt.Contains("冒险") || txt.Contains("Start") || txt.Contains("BEGIN"))
                    return b;
            }
            // 兜底：同一排里最靠右的
            Button best = null; float bx = float.MinValue;
            if (bottom != null)
                foreach (var b in bottom.GetComponentsInChildren<Button>(true))
                    if (b != null && b.transform.position.x > bx) { bx = b.transform.position.x; best = b; }
            return best;
        }
        catch { return null; }
    }



    // ---------------- UI 小工具 ----------------
    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    private static Image NewImage(Transform parent, string name, Color c)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var img = rt.gameObject.AddComponent<Image>();
        img.color = c;
        img.raycastTarget = false;
        return img;
    }

    private static TextMeshProUGUI NewText(Transform parent, string name, string text, float size, Color c, TextAlignmentOptions align)
    {
        var rt = NewRect(name, parent);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (DlcPanel.Font != null) t.font = DlcPanel.Font;
        t.text = text;
        t.fontSize = size;
        t.color = c;
        t.alignment = align;
        t.raycastTarget = false;
        t.enableWordWrapping = false;
        return t;
    }

    private static void Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    private static void MakePickerButton(RectTransform root, string label, Vector2 center, Vector2 size, Color bgCol, Action onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(root, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = center;
        rt.sizeDelta = size;

        var img = go.GetComponent<Image>();
        img.color = bgCol;
        img.raycastTarget = true;

        var btn = go.GetComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(() => { try { onClick?.Invoke(); } catch (Exception e) { Plugin.Log.LogWarning(e.Message); } });

        var t = NewText(rt, "Label", label, 18f, Color.white, TextAlignmentOptions.Center);
        t.rectTransform.anchorMin = Vector2.zero;
        t.rectTransform.anchorMax = Vector2.one;
        t.rectTransform.offsetMin = Vector2.zero;
        t.rectTransform.offsetMax = Vector2.zero;
    }

}
