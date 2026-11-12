#!/usr/bin/env bash
# ============================================================================
#  STEM Fish DLC —— 一键构建 + 部署
#  ---------------------------------------------------------------------------
#  只做两件事：编译、把产物拷到插件目录。**不启动游戏**，不碰任何游戏文件。
#  用法：在 Git Bash 里执行  ./deploy.sh
# ============================================================================
set -e

SRC="$(cd "$(dirname "$0")" && pwd)"
GAME="D:/Steam/steamapps/common/Casualties Unknown Demo"
DST="$GAME/BepInEx/plugins"
PLUGIN="$DST/STEMFishDLC"

if tasklist 2>/dev/null | grep -qi "CasualtiesUnknown.exe"; then
  echo "!! 游戏正在运行，请先退出（dll 会被占用）"
  exit 1
fi

echo "== 构建 =="
"/c/Program Files/dotnet/dotnet.exe" build -c Release -p:GameDir="$GAME" | grep -E "error|已成功生成" || true

DLL="$SRC/bin/Release/netstandard2.1/STEMFishDLC.dll"
if [ ! -f "$DLL" ]; then
  echo "!! 构建产物不存在：$DLL"
  exit 1
fi

echo "== 部署 =="
cp "$DLL" "$DST/STEMFishDLC.dll"
mkdir -p "$PLUGIN/textures"
cp "$SRC"/textures/*.png "$PLUGIN/textures/"
cp "$SRC"/items.zh-CN.json "$SRC"/voice.zh-CN.json "$SRC"/lore.zh-CN.json "$SRC"/ui.zh-CN.json "$PLUGIN/"

# 档案配图（lore.json 里 sprite 字段引用的那些）
if [ -d "$SRC/lore_img" ]; then
  mkdir -p "$PLUGIN/lore_img"
  cp "$SRC"/lore_img/*.png "$PLUGIN/lore_img/" 2>/dev/null || true
fi

# 皮肤包：源码 skin/<包名>/ → plugins/CustomSprites/<包名>/
# 这是游戏侧资源（不在插件目录里），但同样要能从源码恢复
if [ -d "$SRC/skin" ]; then
  echo "== 皮肤 =="
  for pack in "$SRC"/skin/*/; do
    [ -d "$pack" ] || continue
    name="$(basename "$pack")"
    mkdir -p "$DST/CustomSprites/$name"
    cp -r "$pack". "$DST/CustomSprites/$name/"
    echo "  同步皮肤包: $name"
  done
fi

echo "== 完成 =="
ls -la "$DST/STEMFishDLC.dll"
echo "文本:"
ls -la "$PLUGIN"/*.json
