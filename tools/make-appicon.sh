#!/usr/bin/env bash
# 用游戏自带图标(icon.icns)生成 iOS App 图标资源(Assets.car + 散图标),
# 供 deploy-slim.sh 注入 .app(替换 Godot 导出的占位灰图标)。
# 产物落在 ios-export/appicon/(gitignore, 因含游戏美术, 不分发)。
# 幂等: 重复跑覆盖产物。游戏 icns 是 macOS 风格带透明边, 这里展平到黑底
# (iOS 会自动加圆角遮罩, 黑底与游戏暗色调协调)。
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
EXPORT_DIR="$ROOT/ios-export"
[ -f "$EXPORT_DIR/config.sh" ] || { echo "❌ 缺 ios-export/config.sh"; exit 1; }
# shellcheck disable=SC1091
source "$EXPORT_DIR/config.sh"
ICNS="${STS2_GAME_APP}/icon.icns"
OUT="$EXPORT_DIR/appicon"
# 图标源优先级: 命令行参数 $1 > ios-export/appicon-src.png(自备方形图) > 游戏 icon.icns。
# 自备图放一张 1024×1024 方形 png/jpg 到 appicon-src.png 即可(如官方封面主视觉)。
CUSTOM_SRC="${1:-$EXPORT_DIR/appicon-src.png}"
fail(){ echo "❌ $1"; exit 1; }

xcrun --find actool >/dev/null 2>&1 || fail "缺 actool(装 Xcode)"

WORK="$(mktemp -d)/AppIcon.appiconset"; mkdir -p "$WORK"
# 1) 选图标源
if [ -f "$CUSTOM_SRC" ]; then
  SRC="$CUSTOM_SRC"
  echo "图标源: 自定义图 $SRC"
else
  command -v iconutil >/dev/null || fail "缺 iconutil"
  [ -f "$ICNS" ] || fail "无自定义图且游戏图标缺失: $ICNS"
  iconutil -c iconset "$ICNS" -o "$WORK/../src.iconset" || fail "iconutil 解 icns 失败"
  SRC=$(ls -S "$WORK/../src.iconset"/*.png 2>/dev/null | head -1)
  [ -f "$SRC" ] || fail "icns 里无 png"
  echo "图标源: 游戏 icns($SRC)"
fi
# 2) 展平 alpha 到黑底(经 jpeg 去 alpha 再回 png)
sips -s format jpeg "$SRC" --out "$WORK/../flat.jpg" >/dev/null 2>&1 || fail "展平失败"
sips -s format png "$WORK/../flat.jpg" --out "$WORK/../flat.png" >/dev/null 2>&1 || fail "回 png 失败"
# 3) 写 Contents.json(对齐 Godot 4.5.1 iOS 导出的 AppIcon 尺寸表)
cat > "$WORK/Contents.json" <<'JSON'
{"images":[{"idiom":"universal","platform":"ios","size":"29x29","scale":"2x","filename":"Icon-58.png"},{"idiom":"universal","platform":"ios","size":"29x29","scale":"3x","filename":"Icon-87.png"},{"idiom":"universal","platform":"ios","size":"20x20","scale":"2x","filename":"Icon-40.png"},{"idiom":"universal","platform":"ios","size":"20x20","scale":"3x","filename":"Icon-60.png"},{"idiom":"universal","platform":"ios","size":"38x38","scale":"2x","filename":"Icon-76.png"},{"idiom":"universal","platform":"ios","size":"38x38","scale":"3x","filename":"Icon-114.png"},{"idiom":"universal","platform":"ios","size":"40x40","scale":"2x","filename":"Icon-80.png"},{"idiom":"universal","platform":"ios","size":"40x40","scale":"3x","filename":"Icon-120.png"},{"idiom":"universal","platform":"ios","size":"60x60","scale":"2x","filename":"Icon-120-1.png"},{"idiom":"universal","platform":"ios","size":"60x60","scale":"3x","filename":"Icon-180.png"},{"idiom":"universal","platform":"ios","size":"83.5x83.5","scale":"2x","filename":"Icon-167.png"},{"idiom":"universal","platform":"ios","size":"76x76","scale":"2x","filename":"Icon-152.png"},{"idiom":"universal","platform":"ios","size":"64x64","scale":"2x","filename":"Icon-128.png"},{"idiom":"universal","platform":"ios","size":"64x64","scale":"3x","filename":"Icon-192.png"},{"idiom":"universal","platform":"ios","size":"68x68","scale":"2x","filename":"Icon-136.png"},{"idiom":"universal","platform":"ios","size":"1024x1024","filename":"Icon-1024.png"}],"info":{"author":"xcode","version":1}}
JSON
# 4) 按尺寸生成各图标(从展平源缩放; 大部分下采样清晰, 1024 仅商店用)
for spec in Icon-1024.png:1024 Icon-192.png:192 Icon-180.png:180 Icon-167.png:167 \
  Icon-152.png:152 Icon-136.png:136 Icon-128.png:128 Icon-120.png:120 Icon-120-1.png:120 \
  Icon-114.png:114 Icon-87.png:87 Icon-80.png:80 Icon-76.png:76 Icon-60.png:60 \
  Icon-58.png:58 Icon-40.png:40; do
  fn="${spec%%:*}"; sz="${spec##*:}"
  sips -z "$sz" "$sz" "$WORK/../flat.png" --out "$WORK/$fn" >/dev/null 2>&1 || fail "缩放 $fn 失败"
done
# 5) actool 编译成 Assets.car + 散图标
rm -rf "$OUT"; mkdir -p "$OUT"
xcrun actool "$(dirname "$WORK")" --compile "$OUT" \
  --app-icon AppIcon --platform iphoneos --minimum-deployment-target 14.0 \
  --output-partial-info-plist "$OUT/partial.plist" >/dev/null 2>&1 || fail "actool 编译失败"
[ -f "$OUT/Assets.car" ] || fail "未产出 Assets.car"
echo "✅ App 图标已生成: $OUT/（Assets.car + $(ls "$OUT"/*.png | wc -l | tr -d ' ') 散图标）"
