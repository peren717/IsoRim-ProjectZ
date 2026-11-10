#!/bin/bash
# The XML test mods under Source/Dev, put where RimWorld loads them for a test run, and taken out again.
# They are templates: inside this repo they ship with Source but are never loaded (RimWorld loads only mods directly in
# its Mods folder). A test mode that needs one says so (TestGameRange: gamerange; TestCustomLadders: customladders).
#
# Usage (Git Bash):  Source/Dev/testmods.sh install|remove|status <name> [ModsDir]
#   name: gamerange (GameRangeTestMod -> Mods/_PZGameRangeTest) or ladders (LadderTestMod -> Mods/_PZLadderTest)
#   ModsDir: default the Mods folder this repo sits in (this script's ../../..)
#   install: copies the template (replacing an earlier copy of the same test mod); remove: deletes exactly that copy,
#   only when its About.xml has the template's packageId.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
CMD="$1"; NAME="$2"; MODS="${3:-$(cd "$HERE/../../.." && pwd)}"
case "$NAME" in
  gamerange) SRC=GameRangeTestMod; DEST=_PZGameRangeTest ;;
  ladders) SRC=LadderTestMod; DEST=_PZLadderTest ;;
  *) echo "name: gamerange or ladders"; exit 2 ;;
esac
ID=$(grep -o '<packageId>[^<]*' "$HERE/$SRC/About/About.xml" | cut -d'>' -f2)
TARGET="$MODS/$DEST"
ours() { [ -f "$TARGET/About/About.xml" ] && grep -q "<packageId>$ID</packageId>" "$TARGET/About/About.xml"; }
case "$CMD" in
  install)
    if [ -e "$TARGET" ] && ! ours; then echo "$TARGET exists and is not $ID: left alone"; exit 1; fi
    rm -rf "$TARGET"
    cp -r "$HERE/$SRC" "$TARGET"
    echo "installed $ID at $TARGET (activate packageId $ID after ProjectZ)" ;;
  remove)
    if [ ! -e "$TARGET" ]; then echo "$TARGET: not installed"; exit 0; fi
    if ! ours; then echo "$TARGET is not $ID: left alone"; exit 1; fi
    rm -rf "$TARGET"
    echo "removed $TARGET" ;;
  status)
    if ours; then diff -rq "$HERE/$SRC" "$TARGET" > /dev/null && echo "$TARGET: installed, same as the template" || echo "$TARGET: installed, differs from the template"; else echo "$TARGET: not installed"; fi ;;
  *) echo "install, remove or status"; exit 2 ;;
esac
