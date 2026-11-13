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

    public static void Init(Plugin plugin)
    {
        try
        {
            Enabled = plugin.Config.Bind("Save", "SaveMap", true,
                "存档时一并保存本层地图与角色坐标（关掉 = 回到原版行为，读档后地图重新随机）").Value;
        }
        catch { }
    }

    public static string PathFor(string id) => Path.Combine(SaveSlots.Root, id + ".map");

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
    }

    // ======================================================================
    //  每帧：等世界生成完 → 写回方块 + 传送角色
    // ======================================================================
    public static void Tick()
    {
        if (!_pending || _blocks == null) return;

        // 兜底：超过 3 分钟还没等到世界就绪就放弃，避免哪天误应用到别的局上
        if (_preparedAt > 0f && Time.realtimeSinceStartup - _preparedAt > 180f)
        {
            Plugin.Log.LogWarning("[STEMFishDLC] 地图存档等待超时，已放弃写回（其余功能不受影响）");
            Cancel();
            return;
        }

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

    private static bool _warned;
    private static void WarnOnce(string msg)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Log.LogWarning($"[STEMFishDLC] {msg}（地图存档功能失效，其余不受影响）");
    }
}
