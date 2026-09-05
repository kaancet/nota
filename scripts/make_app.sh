#!/bin/bash
# Build a thin-launcher Nota.app (macOS) that double-clicks to run the repo build.
# The app is a stable shell: code changes need only a normal `dotnet build/run`, never a re-bundle.
# Re-run this only to refresh the icon, name, or launcher.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="$REPO/Nota.app"
DOTNET="$(command -v dotnet)"
LOGO="$REPO/frontend/Assets/logo.png"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# --- icon: logo.png (1024) -> Nota.icns via an iconset ---
ICONSET="$(mktemp -d)/Nota.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s     "$LOGO" --out "$ICONSET/icon_${s}x${s}.png"     >/dev/null
  sips -z $((s*2)) $((s*2)) "$LOGO" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Nota.icns"

# --- Info.plist: name + icon + id (what macOS reads for the dock + app menu) ---
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>            <string>Nota</string>
  <key>CFBundleDisplayName</key>     <string>Nota</string>
  <key>CFBundleExecutable</key>      <string>Nota</string>
  <key>CFBundleIconFile</key>        <string>Nota.icns</string>
  <key>CFBundleIdentifier</key>      <string>com.kaan.nota</string>
  <key>CFBundlePackageType</key>     <string>APPL</string>
  <key>CFBundleShortVersionString</key> <string>0.1.0</string>
  <key>NSHighResolutionCapable</key> <true/>
</dict>
</plist>
PLIST

# --- thin launcher: runs the latest repo build (dotnet run rebuilds changed code) ---
cat > "$APP/Contents/MacOS/Nota" <<LAUNCH
#!/bin/bash
# GUI launch has a minimal PATH -> use absolute dotnet. Backend.cs already uses an absolute
# venv python + repo cwd, so nothing else needs the PATH.
mkdir -p "\$HOME/.nota/logs"
cd "$REPO/frontend"
exec "$DOTNET" run -c Release >>"\$HOME/.nota/logs/app-launch.log" 2>&1
LAUNCH
chmod +x "$APP/Contents/MacOS/Nota"

echo "built $APP  (double-click to launch; re-run this script only to refresh icon/name)"
