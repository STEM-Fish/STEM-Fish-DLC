using System;
using System.Collections.Generic;
using System.IO;

// ============================================================================
//  STEMFishDLC.Patcher - BepInEx 5 预加载器插件（放在 BepInEx/patchers/）
//  ---------------------------------------------------------------------------
//  为什么必须是 patcher：
//    运行时撤销别的 MOD 的 Harmony 补丁，会把它们变成"半死"状态
//    （界面还在、逻辑没了 → 点一下卡死）。唯一干净的做法是**让它们根本不被加载**。
//    而只有在预加载阶段（插件枚举之前）改文件才来得及。
//
//  做法：把 BepInEx/plugins/ 下**其他** .dll 改名成 .dll.isolated。
//    · 完全可逆：开关关掉后下次启动会自动改名回来
//    · 只碰 plugins 目录里的 mod 文件，不动游戏本体
//    · 自己不依赖 BepInEx.dll（预加载阶段它可能还没就绪），日志自己写文件
//
//  开关来源：
//    BepInEx/config/com.stemfish.dlc.cfg
//      [General] IsolateOtherMods = true|false        —— 总开关
//      [General] IsolateKeep      = No Self-Destruct, KrokMP   —— 白名单（相对路径子串）
//
//  白名单默认值为什么包含 KrokMP：
//    · 它是**其他 MOD 的依赖项**（很多插件引用 KrokoshaCasualtiesMP 的类型）
//    · 它自带「关闭联机 / 回到单机」的功能，玩家可以在它自己的界面里关掉联机，
//      不需要靠改名屏蔽 —— 屏蔽它反而会让依赖它的 MOD 一起半死
//    · 它的依赖分散在 plugins/KrokMP/ 子目录，所以白名单按**相对路径**匹配
// ============================================================================

namespace STEMFishDLC.Patcher
{
    public static class StemFishIsolation
    {
        private const string OurPluginDll = "STEMFishDLC.dll";
        private const string IsolatedSuffix = ".isolated";

        // ---- BepInEx 5 patcher 契约（反汇编确认必需）----
        //  · public static IEnumerable<string> TargetDLLs { get; }   —— 用来识别这是 patcher
        //  · public static void Patch(AssemblyDefinition)            —— 契约要求，本插件不修改程序集
        //  · public static void Initialize()                         —— 预加载阶段回调
        //  · public static void Finish()                             —— patch 结束回调（可选）
        //  ★ 名字必须是 Initialize / Finish / Patch（反汇编 AddPatchersFromDirectory 确认），
        //    写成 Initializer 不会报错，但永远不会被调用（v0.11.0 的坑）
        public static IEnumerable<string> TargetDLLs => new string[0];

        private static bool _done;

        public static void Patch(Mono.Cecil.AssemblyDefinition assembly) { }   // 不用改 IL

        public static void Initialize()
        {
            if (_done) return;
            _done = true;
            Run();
        }

        private static void Run()
        {
            try
            {
                string root = FindBepInExRoot();
                if (root == null) return;

                string plugins = Path.Combine(root, "plugins");
                string cfg = Path.Combine(root, "config", "com.stemfish.dlc.cfg");
                bool isolate = ReadFlag(cfg, "General", "IsolateOtherMods");
                var keep = ReadList(cfg, "General", "IsolateKeep");

                Log(root, $"[Isolation] 启动检查: 隔离模式={(isolate ? "开" : "关")} | plugins={plugins}");
                if (keep.Length > 0) Log(root, "[Isolation] 白名单: " + string.Join(", ", keep));

                if (!Directory.Exists(plugins)) return;

                // ★ 自保：万一本插件自己的 dll 被改过名（旧版本/误操作），先改回来，
                //   否则隔离一开会把自己也屏蔽掉 —— 表现就是"重启后我们自己的 MOD 没了"
                try
                {
                    var selfIso = Path.Combine(plugins, OurPluginDll + IsolatedSuffix);
                    if (File.Exists(selfIso))
                    {
                        var self = Path.Combine(plugins, OurPluginDll);
                        if (File.Exists(self)) File.Delete(self);
                        File.Move(selfIso, self);
                        Log(root, "[Isolation] 发现本插件被改名，已自动恢复 " + OurPluginDll);
                    }
                }
                catch (Exception e) { Log(root, "[Isolation] 恢复本插件失败: " + e.Message); }

                int renamed = 0, restored = 0, skippedSelf = 0, skippedKeep = 0;

                // ★ 两个目录都要处理：
                //   plugins/  —— 插件本体
                //   patchers/ —— 预加载器插件（KrokMP 的 autoupdater_patcher 会改 Assembly-CSharp）
                //   只改名 plugins 而不改名 patchers 会造成**不一致状态**：
                //   KrokMP 插件没加载，但它的预加载器仍在往游戏程序集里注入 —— 这正是
                //   "隔离一开，整个游戏连原版功能都失常" 的来源之一。
                var dirs = new[]
                {
                    plugins,
                    Path.Combine(root, "patchers"),
                };

                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    Log(root, $"[Isolation] 扫描 {Rel(root, dir)}");

                    foreach (var dll in Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(dll);
                        var rel = Rel(root, dll);

                        if (string.Equals(name, OurPluginDll, StringComparison.OrdinalIgnoreCase)
                            || name.IndexOf("STEMFishDLC", StringComparison.OrdinalIgnoreCase) >= 0)
                        { skippedSelf++; continue; }
                        if (LooksLikePatcherOrCore(name)) continue;
                        if (InKeepList(rel, keep)) { skippedKeep++; Log(root, "[Isolation] 白名单跳过 " + rel); continue; }

                        // ★ 只屏蔽 **.NET 程序集**：像 opus.dll / steam_api64.dll 这类原生库
                        //   一旦被改名，依赖它们的插件会以"缺 DLL"的方式崩掉（而不是简单不加载），
                        //   是很难排查的半死状态。原生库本来也不会被 BepInEx 当插件加载，改它没有意义。
                        if (isolate && !IsManagedAssembly(dll))
                        {
                            Log(root, "[Isolation] 跳过非 .NET 库 " + rel);
                            continue;
                        }

                        if (isolate)
                        {
                            try
                            {
                                var dst = dll + IsolatedSuffix;
                                if (File.Exists(dst)) File.Delete(dst);
                                File.Move(dll, dst);
                                renamed++;
                                Log(root, $"[Isolation] 已隔离 {Rel(root, dll)}");
                            }
                            catch (Exception e) { Log(root, $"[Isolation] 隔离失败 {name}: {e.Message}"); }
                        }
                    }
                }

                // 关掉隔离时把上次改名的恢复回来（两个目录都扫）
                if (!isolate)
                {
                    foreach (var dir in dirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var iso in Directory.GetFiles(dir, "*" + IsolatedSuffix, SearchOption.AllDirectories))
                        {
                            try
                            {
                                var dst = iso.Substring(0, iso.Length - IsolatedSuffix.Length);
                                if (File.Exists(dst)) File.Delete(dst);
                                File.Move(iso, dst);
                                restored++;
                                Log(root, $"[Isolation] 已恢复 {Rel(root, iso)}");
                            }
                            catch (Exception e) { Log(root, $"[Isolation] 恢复失败 {Path.GetFileName(iso)}: {e.Message}"); }
                        }
                    }
                }

                Log(root, $"[Isolation] 完成：隔离 {renamed} 个，恢复 {restored} 个，跳过自身 {skippedSelf} 个，白名单 {skippedKeep} 个");
            }
            catch (Exception e)
            {
                try { Log(null, "[Isolation] 异常: " + e); } catch { }
            }
        }

        // 预加载阶段 Paths 可能还没初始化，自己往上找 BepInEx 目录
        private static string FindBepInExRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "BepInEx", "core", "BepInEx.dll")))
                        return Path.Combine(dir.FullName, "BepInEx");
                    if (string.Equals(dir.Name, "BepInEx", StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(Path.Combine(dir.FullName, "core")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        private static bool LooksLikePatcherOrCore(string name)
        {
            var n = name.ToLowerInvariant();
            return n.StartsWith("bepinex") || n.StartsWith("0harmony") || n.StartsWith("monomod")
                || n.StartsWith("mono.cecil") || n.StartsWith("harmonyxinterop")
                || n.Contains("stemfishdlc.patcher");
        }

        // 极简 ini 读：[section] key = value
        private static string ReadRaw(string cfgPath, string section, string key)
        {
            try
            {
                if (!File.Exists(cfgPath)) return null;
                string cur = "";
                foreach (var raw in File.ReadAllLines(cfgPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        cur = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim();
                    var v = line.Substring(eq + 1).Trim();
                    if (string.Equals(cur, section, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        return v;
                }
            }
            catch { }
            return null;
        }

        private static bool ReadFlag(string cfgPath, string section, string key)
        {
            var v = ReadRaw(cfgPath, section, key);
            return v != null && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // 逗号分隔的子串列表；读不到就用内置默认（防止 cfg 被删/被覆盖后误伤关键 MOD）
        private static string[] ReadList(string cfgPath, string section, string key)
        {
            var v = ReadRaw(cfgPath, section, key);
            if (string.IsNullOrEmpty(v)) return DefaultKeep;
            var parts = v.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.Count > 0 ? list.ToArray() : DefaultKeep;
        }

        // 内置默认白名单：
        //   No Self-Destruct —— 屏蔽后退出游戏会触发芯片自毁（丢档）
        //   KrokMP / Together / CT_AutoUpdate —— 联机 MOD（KrokMP 是前身，Together 是正式版的续作）
        //      · 它是其它 MOD 的依赖项
        //      · 它自己能在设置里「Deactivate MP Mod」关掉联机，不需要靠改名屏蔽
        private static readonly string[] DefaultKeep = { "No Self-Destruct", "KrokMP", "Together", "CT_AutoUpdate" };

        // 白名单匹配：**相对路径**（不是裸文件名）
        //  ---------------------------------------------------------------------
        //  KrokMP 的依赖分散在子目录里，光看文件名根本认不出来：
        //    plugins/KrokMP/KrokoshaCasualtiesMP.dll   ← 联机核心，被别的 MOD 依赖
        //    plugins/KrokMP/LiteNetLib.dll / opus.dll / OpusSharp.Core.dll / Steamworks.NET.dll
        //    plugins/KrokMPChineseSupplement.dll       ← 中文补全
        //    patchers/KrokMP/autoupdater_patcher.dll   ← 它的预加载器
        //  用相对路径匹配 "KrokMP" 就能一次全命中，不用逐条列文件名。
        private static bool InKeepList(string relativePath, string[] keep)
        {
            if (keep == null || keep.Length == 0) return false;
            foreach (var k in keep)
                if (relativePath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 是不是 .NET 程序集：读 PE 头里的 **CLI Header** 数据目录（索引 14）
        //  · 非零 = 含 .NET 元数据 = 是托管程序集
        //  · 零/读不到 = 原生 dll（opus / steam_api64 之类），**不要动**
        // 只看文件头，不加载程序集，预加载阶段也安全。
        private static bool IsManagedAssembly(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x100) return false;
                    if (br.ReadUInt16() != 0x5A4D) return false;          // "MZ"

                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peOff = br.ReadInt32();
                    if (peOff <= 0 || peOff + 24 >= fs.Length) return false;

                    fs.Seek(peOff, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) return false;       // "PE\0\0"

                    fs.Seek(peOff + 6, SeekOrigin.Begin);                  // NumberOfSections
                    int sections = br.ReadUInt16();
                    fs.Seek(peOff + 20, SeekOrigin.Begin);                 // SizeOfOptionalHeader
                    int optSize = br.ReadUInt16();

                    int optStart = peOff + 24;
                    if (optSize < 216) return false;                       // PE32 至少 216 字节

                    // Magic: 0x10B = PE32, 0x20B = PE32+
                    fs.Seek(optStart, SeekOrigin.Begin);
                    ushort magic = br.ReadUInt16();
                    bool pe32Plus = magic == 0x20B;

                    // DataDirectory[14] = CLI Header；PE32 从 optStart+96 开始，PE32+ 从 optStart+112
                    int ddStart = optStart + (pe32Plus ? 112 : 96);
                    int cliEntry = ddStart + 14 * 8;
                    if (cliEntry + 8 > peOff + 24 + optSize) return false;

                    fs.Seek(cliEntry, SeekOrigin.Begin);
                    uint rva = br.ReadUInt32();
                    uint size = br.ReadUInt32();
                    return rva != 0 && size != 0;
                }
            }
            catch { return false; }
        }

        private static string Rel(string root, string full)
        {
            try
            {
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            }
            catch { }
            return full;
        }

        private static void Log(string root, string msg)
        {
            try
            {
                var path = root != null
                    ? Path.Combine(root, "STEMFishDLC_isolation.log")
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "STEMFishDLC_isolation.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
