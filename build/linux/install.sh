#!/bin/sh
# Installs AutoKey for the current user: the program in ~/.local/share/autokey, an "autokey" command in
# ~/.local/bin, and an entry with an icon in the applications menu. Run it again to update.
set -e

here=$(cd "$(dirname "$0")" && pwd)
target="$HOME/.local/share/autokey"
applications="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor/256x256/apps"

mkdir -p "$target" "$applications" "$icons" "$HOME/.local/bin"
cp "$here/AutoKey" "$target/AutoKey"
chmod 755 "$target/AutoKey"
ln -sf "$target/AutoKey" "$HOME/.local/bin/autokey"
cp "$here/autokey.png" "$icons/autokey.png"
sed "s|^Exec=.*|Exec=$target/AutoKey|" "$here/autokey.desktop" > "$applications/autokey.desktop"
update-desktop-database "$applications" 2>/dev/null || true

echo "AutoKey is installed. Start it from your applications menu, or run: autokey"
