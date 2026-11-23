using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace STEMFishDLC;

// ============================================================================
//  MapSave - 把「这一层的地图」和「角色坐标」一起存进槽位
//  ---------------------------------------------------------------------------
//  背景：
//    原版存档（save.sv）只存人物 / 物品 / 层数 / 设置，**不存地图**。
//    `WorldGeneration.Start` 用的是无种子的 Random，读档后整层会重新随机生成 ——
//    挖开的矿道、炸开的地板、走过的地形全都变样。
//
//  做法：
//    存档时把 `WorldGeneration.worldBlocks`（`ushort[width, height]`，整层方块表）
//    连同宽高、层号、角色坐标一起 gzip 写成 `<槽位id>.map`；
//    读档时先只把它读进内存，等新世界生成完毕（`timeSinceFinishedGeneration > 0`
//    + `body` 已就位）再把方块表整份写回，并按 CHUNKSIZE 分块 `SetTiles` 批量刷新。
//
//  ★ 层号要"掰回来"（v0.18.12 修）：
//    原版 `SaveSystem.SaveGame` 写的是 `biome = biomeDepth + 1`，
//    而 `SaveSystem.TryLoadGame` 读回来是 `biomeDepth = biome` —— **整整差一层**。
//    原版是"进层式"存档（存档点=本层到底，读档=直接进下一层），所以官方没觉得错；
//    我们要的是"原地续玩"，就必须在 TryLoadGame 之后把层号改回我们记的那一层，
//    否则读第 11 层的档会掉进第 12 层（= 实验室结局层，`Body.PlaceBody` 里
//    `biomeDepth == 11` → ENDLABSPAWN）。
//
//  为什么用批量 SetTiles 而不是逐格 SetBlock：
//    逐格 `UpdateTile` 每格都 `Tilemap.SetTile` 一次，几十万格会卡好几秒；
//    分块批量提交只有 chunkWidth×chunkHeight 次调用。
//
//  ⚠ 不在存档里的东西（原版格式限制）：地图上的敌人、掉落物之外的建筑实体状态、
//    容器是否被翻过、陷阱是否触发过。这些仍然按原版行为重置。
// ============================================================================
public static class MapSave
{
    public static bool Enabled = true;

    private const string Magic = "SFMAP1";
    private const int Version = 1;

    // ---- 读档待应用的数据 ----
    private static bool _pending;
    private static ushort[] _blocks;
    private static int _w, _h, _depth;
    private static Vector2 _pos;
    private static float _preparedAt = -1f;
    private static WorldGeneration _preparedWorld;   // 记录"读档前"的世界实例，换了实例才应用
    private static bool _appliedLogged;
    private static bool _patched;

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Save", "SaveMap", true,
                "存档时一并保存本层地图与角色坐标（关掉 = 回到原版行为，读档后地图重新随机）").Value;
        }
        catch { }

        PatchTryLoadGame();
    }

    // ======================================================================
    //  读档层号修正：TryLoadGame 之后把 biomeDepth 掰回存档时那一层
    // ----------------------------------------------------------------------
    //  原版把层号写成 biome = depth + 1，读回来直接 depth = biome → 深一层。
    //  我们这个 hook 只对"有地图存档（.map）的槽位"生效：
    //    ① 没有 .map 的旧档 → 完全走原版逻辑，不赌
    //    ② 有 .map → 层号 = .map 里记的那一层，后面 Tick 才能把方块写回去
    // ======================================================================
    private static void PatchTryLoadGame()
    {
        if (_patched) return;
        try
        {
            var m = AccessTools.Method(typeof(SaveSystem), "TryLoadGame", Type.EmptyTypes);
            if (m == null)
            {
                Plugin.Log.LogWarning("[STEMFishDLC] 找不到 SaveSystem.TryLoadGame，读档层号不会修正");
                return;
            }
            var post = typeof(MapSave).GetMethod("TryLoadGame_Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            new Harmony("com.stemfish.dlc.mapsave").Patch(m, postfix: new HarmonyMethod(post));
            var info = Harmony.GetPatchInfo(m);
            _patched = true;
            Plugin.Log.LogInfo($"[STEMFishDLC] 读档层号修正 hook 完成 | postfix={info?.Postfixes?.Count ?? -1}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读档层号修正 hook 失败: {e.Message}");
        }
    }

    private static void TryLoadGame_Postfix()
    {
        if (!Enabled || !_pending || _blocks == null) return;   // 没有地图存档：不动原版行为
        try
        {
            var w = WorldGeneration.world;
            if (w == null) return;

            int cur = w.biomeDepth;
            if (cur == _depth) return;

            // 只有"读档读到我们这一层附近"才掰（±1 是原版的进位；夹到 1 的极端情况差得更远，
            // 但只要 .map 存在就说明这是我们的档，照样掰回去）
            w.biomeDepth = _depth;
            Plugin.Log.LogInfo($"[STEMFishDLC] 读档层号修正：{cur} → {_depth}（原版 biome=depth+1 的进位）");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读档层号修正失败: {e.Message}");
        }
    }

    public static string PathFor(string id) => Path.Combine(SaveSlots.Root, id + ".map");

    // ======================================================================
    //  只读层号（给存档列表显示用）
    //  ----------------------------------------------------------------------
    //  存档列表以前是拿 save.sv 里的 `biome - 1` 当层号，但原版在最后一层存档时
    //  会把 biome 夹成 1（`if (biome >= amountOfLayers) biome = 1`），列表就会显示第 1 层。
    //  我们自己记的层号在 .map 头里，读几十个字节就够。
    // ======================================================================
    public static int ReadDepth(string id) => ReadDepth(id, out _);

    public static int ReadDepth(string id, out bool ok)
    {
        ok = false;
        if (string.IsNullOrEmpty(id)) return -1;

        string path = PathFor(id);
        if (!File.Exists(path)) return -1;

        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var br = new BinaryReader(gz))
            {
                if (br.ReadString() != Magic) return -1;
                if (br.ReadInt32() != Version) return -1;
                br.ReadInt32();                      // width
                br.ReadInt32();                      // height
                int depth = br.ReadInt32();
                ok = true;
                return depth;
            }
        }
        catch { return -1; }
    }

    // ======================================================================
    //  存
    // ======================================================================
    public static void Save(string id)
    {
        if (!Enabled || string.IsNullOrEmpty(id)) return;

        try
        {
            var w = WorldGeneration.world;
            if (w == null || !w.worldExists) return;

            var fBlocks = AccessTools.Field(typeof(WorldGeneration), "worldBlocks");
            var arr = fBlocks != null ? fBlocks.GetValue(w) as ushort[,] : null;
            if (arr == null) { WarnOnce("取不到 worldBlocks"); return; }

            int width = arr.GetLength(0), height = arr.GetLength(1);
            if (width <= 0 || height <= 0) return;

            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            Vector2 pos = body != null ? (Vector2)body.transform.position : Vector2.zero;

            string path = PathFor(id);
            Directory.CreateDirectory(SaveSlots.Root);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
            using (var bw = new BinaryWriter(gz))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(width);
                bw.Write(height);
                bw.Write(w.biomeDepth);
                bw.Write(pos.x);
                bw.Write(pos.y);
                for (int x = 0; x < width; x++)
                    for (int y = 0; y < height; y++)
                        bw.Write(arr[x, y]);
            }

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            Plugin.Log.LogInfo($"[STEMFishDLC] 地图已保存：{width}×{height} 方块 · {size / 1024}KB · " +
                               $"坐标 ({pos.x:0},{pos.y:0}) · 第 {w.biomeDepth} 层");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 保存地图失败: {e.Message}");
        }
    }

    // ======================================================================
    //  读（只读进内存，等世界就绪再应用）
    // ======================================================================
    public static void Prepare(string id)
    {
        _pending = false;
        _blocks = null;
        _preparedAt = -1f;
        _appliedLogged = false;
        _preparedWorld = null;
        _verifyAt = -1f;

        if (!Enabled || string.IsNullOrEmpty(id)) return;

        string path = PathFor(id);
        if (!File.Exists(path)) return;

        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var br = new BinaryReader(gz))
            {
                string magic = br.ReadString();
                int ver = br.ReadInt32();
                if (magic != Magic || ver != Version)
                {
                    Plugin.Log.LogWarning($"[STEMFishDLC] 地图文件版本不符（{magic} v{ver}），已忽略");
                    return;
                }

                int width = br.ReadInt32(), height = br.ReadInt32();
                if (width <= 0 || height <= 0 || width > 4096 || height > 8192)
                {
                    Plugin.Log.LogWarning($"[STEMFishDLC] 地图尺寸异常 {width}×{height}，已忽略");
                    return;
                }

                _depth = br.ReadInt32();
                _pos = new Vector2(br.ReadSingle(), br.ReadSingle());

                var data = new ushort[width * height];
                for (int i = 0; i < data.Length; i++) data[i] = br.ReadUInt16();

                _blocks = data;
                _w = width;
                _h = height;
                _pending = true;
                _preparedAt = Time.realtimeSinceStartup;
                try { _preparedWorld = WorldGeneration.world; } catch { _preparedWorld = null; }

                Plugin.Log.LogInfo($"[STEMFishDLC] 已读入地图存档 {width}×{height} · 第 {_depth} 层 · " +
                                   $"坐标 ({_pos.x:0},{_pos.y:0})，等世界生成完再写回");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 读取地图存档失败: {e.Message}");
            _pending = false;
            _blocks = null;
        }
    }

    public static void Cancel()
    {
        _pending = false;
        _blocks = null;
        _verifyAt = -1f;
    }

    // ======================================================================
    //  传送复核
    //  ----------------------------------------------------------------------
    //  原版 `WorldPlacePlayer` 也是"世界生成完之后"才把人物摆到出生点，
    //  万一它比我们晚一帧，传送就会被它盖掉（人站在出生点，地图却是存档的）。
    //  所以写回方块 1 秒后再看一次：人离目标点超过 8 格就补一次传送。
    // ======================================================================
    private static void VerifyTeleport()
    {
        if (_verifyAt <= 0f) return;
        if (Time.realtimeSinceStartup < _verifyAt) return;
        _verifyAt = -1f;

        try
        {
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body == null) return;

            var pos = (Vector2)body.transform.position;
            if (Vector2.Distance(pos, _verifyPos) <= 8f) return;

            var rb = body.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.position = _verifyPos;
            }
            body.transform.position = new Vector3(_verifyPos.x, _verifyPos.y, body.transform.position.z);
            try
            {
                var cam = PlayerCamera.main.transform;
                cam.position = new Vector3(_verifyPos.x, _verifyPos.y, cam.position.z);
            }
            catch { }

            Plugin.Log.LogInfo($"[STEMFishDLC] 传送复核：出生点把人挪走了（{pos.x:0},{pos.y:0}），已拉回 " +
                               $"({_verifyPos.x:0},{_verifyPos.y:0})");
        }
        catch { }
    }

    // ======================================================================
    //  每帧：等世界生成完 → 写回方块 + 传送角色
    // ======================================================================
    public static void Tick()
    {
        VerifyTeleport();

        if (!_pending || _blocks == null) return;

        // 兜底：超过 3 分钟还没等到世界就绪就放弃，避免哪天误应用到别的局上
        if (_preparedAt > 0f && Time.realtimeSinceStartup - _preparedAt > 180f)
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 地图存档等待超时，已放弃写回（其余功能不受影响）");
            Cancel();
            return;
        }

        WorldGeneration w = null;
        try { w = WorldGeneration.world; } catch { }
        if (w == null) return;

        // 必须是"新世界"：读档失败 / 还停在旧世界时绝不能把旧地图覆盖上去
        if (w == _preparedWorld) return;

        // 生成中 / 还在实例化 → 等
        try
        {
            if (!w.worldExists) return;
            if (w.generatingWorld) return;
            if (_fInstantiating == null)
                _fInstantiating = AccessTools.Field(typeof(WorldGeneration), "instantiatingWorld");
            if (_fInstantiating != null && (bool)_fInstantiating.GetValue(w)) return;
        }
        catch { return; }

        // 世界刚生成完的那几帧还在铺东西，等 0.5 秒更稳
        try
        {
            if (w.timeSinceFinishedGeneration < 0.5f) return;
        }
        catch { return; }

        // 必须等到玩家本体出现（TryLoadGame 已经把人物摆好）
        Body body = null;
        try { body = PlayerCamera.main != null ? PlayerCamera.main.body : null; } catch { }
        if (body == null) return;

        // 层号对不上（读档时层还没切过来）→ 再等等；超时 60 秒就放弃
        try
        {
            if (w.biomeDepth != _depth)
            {
                if (Time.realtimeSinceStartup - _preparedAt > 60f)
                {
                    Plugin.Log.LogWarning($"[STEMFishDLC] 地图存档层号不符（存 {_depth} / 现 {w.biomeDepth}），放弃写回");
                    Cancel();
                }
                return;
            }
        }
        catch { }

        Apply(w, body);
    }

    private static void Apply(WorldGeneration w, Body body)
    {
        _pending = false;
        try
        {
            var fBlocks = AccessTools.Field(typeof(WorldGeneration), "worldBlocks");
            var arr = fBlocks != null ? fBlocks.GetValue(w) as ushort[,] : null;
            if (arr == null) { WarnOnce("写回时取不到 worldBlocks"); return; }

            int width = arr.GetLength(0), height = arr.GetLength(1);
            if (width != _w || height != _h)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 地图尺寸不符（存 {_w}×{_h} / 现 {width}×{height}），放弃写回");
                return;
            }

            // ① 方块表整份写回
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    arr[x, y] = _blocks[x * height + y];

            // ② 分块批量刷新（逐格 SetTile 会卡好几秒）
            int chunksDrawn = RefreshChunks(w, arr, width, height);

            // ★ 爆炸留下的方块伤害贴花是独立对象，不会随 worldBlocks 写回而消失；
            //   不清掉就会在读档后留下"鬼影"。ClearBlockDamages 会 DestroySprite + 清表。
            try { w.ClearBlockDamages(); } catch { }

            // ★ 读档后把建筑实体（商人舱 / 箱子 / 机器）的旋转和速度归零，
            //   避免它们踩在新地形上歪倒。方块碰撞体已经在上一步刷新过了。
            try { ResetBuildings(); } catch { }

            // ③ 角色坐标
            Vector2 p = _pos;
            try
            {
                var rb = body.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.velocity = Vector2.zero;
                    rb.angularVelocity = 0f;
                    rb.position = p;
                }
                body.transform.position = new Vector3(p.x, p.y, body.transform.position.z);

                // 镜头直接瞬移过去，否则会从出生点一路"飞"过来
                try
                {
                    var cam = PlayerCamera.main.transform;
                    cam.position = new Vector3(p.x, p.y, cam.position.z);
                }
                catch { }

                // 世界自己的镜头缓存也跟上
                try
                {
                    var fLast = AccessTools.Field(typeof(WorldGeneration), "lastCameraPos");
                    if (fLast != null) fLast.SetValue(w, new Vector3(p.x, p.y, 0f));
                }
                catch { }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[STEMFishDLC] 传送角色失败: {e.Message}");
            }

            if (!_appliedLogged)
            {
                _appliedLogged = true;
                Plugin.Log.LogInfo($"[STEMFishDLC] 地图已还原：{width}×{height} · {chunksDrawn} 个区块 · " +
                                   $"角色 → ({p.x:0},{p.y:0})");
            }

            // ④ 1 秒后复核位置（原版的出生点摆放可能比我们晚一点）
            _verifyPos = p;
            _verifyAt = Time.realtimeSinceStartup + 1f;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 写回地图失败: {e.Message}");
        }
        finally
        {
            _blocks = null;
        }
    }

    // 读档后把建筑实体的物理状态摆正：零速度、零角速度、旋转归零。
    // 不保存它们的位置（原版格式没有），但至少不会歪着嵌进地形。
    private static void ResetBuildings()
    {
        int n = 0;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<BuildingEntity>();
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                try
                {
                    var rb = b.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {
                        rb.velocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                        rb.rotation = 0f;
                    }
                    b.transform.rotation = Quaternion.identity;
                    n++;
                }
                catch { }
            }
        }
        catch { }
        if (n > 0) Plugin.Log.LogInfo($"[STEMFishDLC] 建筑实体已摆正：{n} 个（读档后防歪倒）");
    }

    // 按 CHUNKSIZE 分块，一次性提交整块（比逐格快两个数量级）
    private static int RefreshChunks(WorldGeneration w, ushort[,] arr, int width, int height)
    {
        int drawn = 0;
        try
        {
            var fChunks = AccessTools.Field(typeof(WorldGeneration), "chunks");
            var chunks = fChunks != null ? fChunks.GetValue(w) as Tilemap[,] : null;
            if (chunks == null || w.tiles == null) return 0;

            int cs = WorldGeneration.CHUNKSIZE;
            if (cs <= 0) return 0;

            int cw = chunks.GetLength(0), ch = chunks.GetLength(1);
            var positions = new Vector3Int[cs * cs];
            var tiles = new TileBase[cs * cs];

            for (int cx = 0; cx < cw; cx++)
            {
                for (int cy = 0; cy < ch; cy++)
                {
                    var map = chunks[cx, cy];
                    if (map == null) continue;

                    int n = 0;
                    for (int lx = 0; lx < cs; lx++)
                    {
                        int x = cx * cs + lx;
                        if (x >= width) break;
                        for (int ly = 0; ly < cs; ly++)
                        {
                            int y = cy * cs + ly;
                            if (y >= height) break;

                            ushort id = arr[x, y];
                            positions[n] = new Vector3Int(lx, ly, 0);
                            tiles[n] = (id < w.tiles.Length) ? w.tiles[id] : null;
                            n++;
                        }
                    }
                    if (n == 0) continue;

                    if (n == positions.Length)
                    {
                        map.SetTiles(positions, tiles);
                    }
                    else
                    {
                        var p2 = new Vector3Int[n];
                        var t2 = new TileBase[n];
                        Array.Copy(positions, p2, n);
                        Array.Copy(tiles, t2, n);
                        map.SetTiles(p2, t2);
                    }

                    // ★ 只 SetTiles 不够：ChunkUpdated 负责刷新碰撞体 / ChunkScript。
                    //   不触发它，读档后旧碰撞体还在，商人舱会陷进/卡在新地形里歪掉。
                    try { w.ChunkUpdated[cx, cy]?.Invoke(); } catch { }
                    drawn++;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[STEMFishDLC] 刷新区块失败: {e.Message}");
        }
        return drawn;
    }

    private static FieldInfo _fInstantiating;

    // 传送复核（见 VerifyTeleport）
    private static float _verifyAt = -1f;
    private static Vector2 _verifyPos;

    private static bool _warned;
    private static void WarnOnce(string msg)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Log.LogWarning($"[STEMFishDLC] {msg}（地图存档功能失效，其余不受影响）");
    }
}
