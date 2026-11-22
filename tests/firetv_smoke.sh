#!/bin/bash
# Drives the Fire TV app on an Android TV emulator against a real hub with fake upstreams.
# Run from the repo root inside the emulator job. Screenshots land in ./shots.
set -u
HUB_DLL=$1; APK=$2
ROOT=$PWD; mkdir -p shots _media/Movies "_media/TV/Breaking Bad/Season 1"
fail() { echo "SMOKE FAIL: $1"; FAILED=1; }
FAILED=0

# ---- media, upstream, hub ---------------------------------------------------------------
ffmpeg -loglevel error -y -f lavfi -i testsrc=duration=90:size=1280x720:rate=24 -f lavfi -i sine=frequency=440:duration=90 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest _media/base.mp4
cp _media/base.mp4 "_media/Movies/Inception (2010).mp4"; cp _media/base.mp4 "_media/Movies/Interstellar.2014.1080p.mp4"; cp _media/base.mp4 "_media/Movies/Toy Story (1995).mp4"; cp _media/base.mp4 "_media/Movies/The.Dark.Knight.2008.mp4"
for e in 1 2 3; do cp _media/base.mp4 "_media/TV/Breaking Bad/Season 1/Breaking.Bad.S01E0$e.mp4"; done
rm _media/base.mp4
python3 tests/fake_upstream.py 19001 & UP=$!
DJFLIX_DATA=$ROOT/_data DJFLIX_PORT=8674 DJFLIX_TMDB_BASE=http://127.0.0.1:19001/3 DJFLIX_RD_BASE=http://127.0.0.1:19001/rd DJFLIX_TMDB_IMAGE_BASE=http://127.0.0.1:19001/img dotnet "$HUB_DLL" > hub.log 2>&1 & HUB=$!
for i in $(seq 1 60); do curl -sf localhost:8674/api/v1/health > /dev/null && break; sleep 0.5; done
ADMIN=$(cat _data/admin.token)
api() { local m=$1 p=$2; shift 2; curl -sf -X "$m" -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" "http://localhost:8674/api/v1$p" "$@"; }
api PUT /admin/providers/metadata -d '{"apiKey":"tmdbgood"}' > /dev/null
api POST /admin/libraries -d "{\"path\":\"$ROOT/_media\"}" > /dev/null
api POST '/admin/library/scan?wait=true' > /dev/null
api POST /profiles -d '{"name":"DG","prefs":{"favouriteGenres":["Science Fiction"]}}' > /dev/null
echo "library: $(api GET '/movies?pageSize=1' | python3 -c 'import sys,json;print(json.load(sys.stdin)["total"])') movies"
CODE=$(api POST /admin/pairing/code | python3 -c 'import sys,json;print(json.load(sys.stdin)["code"])')

# ---- app ----------------------------------------------------------------------------------
shot() { sleep "${2:-2}"; adb exec-out screencap -p > "shots/$1.png"; echo "shot $1"; }
key() { for k in "$@"; do adb shell input keyevent "$k"; sleep 0.7; done; }
adb install -r "$APK" > /dev/null || fail "install"
adb logcat -c
adb shell am start -W -n com.djflix.tv/.MainActivity > /dev/null; shot 01-setup-network 5
adb shell am start -W -a android.intent.action.VIEW -d "djflix://connect?host=10.0.2.2\&port=8674\&code=$CODE" com.djflix.tv > /dev/null
shot 02-profiles 9
[ "$(api GET /admin/devices | python3 -c 'import sys,json;print(len(json.load(sys.stdin)))')" = "1" ] || fail "device did not pair"
key DPAD_CENTER; shot 03-home 7
key DPAD_DOWN; shot 04-home-rows 2
key DPAD_UP DPAD_CENTER; shot 05-player 18
key DPAD_RIGHT; shot 06-player-seek 1
key DPAD_DOWN; shot 07-player-options 2
key BACK BACK; shot 08-home-continue 6
POS=$(api GET /profiles/1/continue-watching | python3 -c 'import sys,json;d=json.load(sys.stdin);print(d[0]["progress"]["positionMs"] if d else 0)')
echo "progress reported by the TV: $POS ms"; [ "${POS:-0}" -gt 8000 ] || fail "playback progress was not reported (got $POS)"
key DPAD_RIGHT DPAD_CENTER; shot 09-details 5
key BACK; sleep 3
key DPAD_UP DPAD_LEFT DPAD_LEFT DPAD_LEFT DPAD_LEFT DPAD_LEFT DPAD_CENTER; shot 10-search 3
key DPAD_DOWN DPAD_CENTER; shot 11-search-results 4
key DPAD_UP DPAD_UP DPAD_RIGHT DPAD_RIGHT DPAD_CENTER; shot 12-movies 5
key DPAD_RIGHT DPAD_CENTER; shot 13-shows 5
key DPAD_DOWN DPAD_DOWN DPAD_DOWN DPAD_CENTER; shot 14-show-details 6
key DPAD_DOWN DPAD_DOWN; shot 15-episodes 3
key DPAD_CENTER; shot 16-episode-playing 12
key BACK; sleep 3; key BACK; sleep 3
key DPAD_UP DPAD_UP DPAD_UP DPAD_RIGHT DPAD_RIGHT DPAD_CENTER; shot 17-settings 4
key DPAD_DOWN DPAD_CENTER; shot 18-settings-changed 3
api GET /profiles | python3 -c 'import sys,json;print("prefs now:",json.load(sys.stdin)[0]["prefs"])'

adb logcat -d > logcat.txt
if grep -A25 "FATAL EXCEPTION" logcat.txt | grep -q "com.djflix.tv"; then fail "app crashed"; grep -A30 "FATAL EXCEPTION" logcat.txt | head -60; fi
echo "---- hub log (api lines) ----"; api GET '/admin/logs?limit=60' | python3 -c 'import sys,json;[print(x["level"],x["message"]) for x in reversed(json.load(sys.stdin))]' | tail -45
kill $HUB $UP 2>/dev/null
[ $FAILED = 0 ] && echo "SMOKE OK" || { echo "SMOKE FAILED"; exit 1; }
