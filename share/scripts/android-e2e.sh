#!/usr/bin/env bash
# Runs on a CI machine with a booted emulator. Tests the APK against the .NET engine on the host,
# then installs the minified release APK, screenshots it and accepts a real incoming transfer by tapping the UI.
set -uo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HERE/android-e2e"; mkdir -p "$OUT"
APK_DIR="$HERE/android/app/build/outputs/apk"
CLI="dotnet $HERE/windows/tools/ClipFx.Share.Cli/bin/Release/net8.0/ClipFx.Share.Cli.dll"
PKG=com.clipfx.share
fail=0
pass() { echo "PASS $*"; }
bad() { echo "FAIL $*"; fail=1; }

adb wait-for-device
adb shell settings put global window_animation_scale 0
adb shell settings put global transition_animation_scale 0
adb shell settings put global animator_duration_scale 0
adb install -r -g "$APK_DIR/debug/app-debug.apk" >/dev/null || bad "install debug"
adb install -r "$APK_DIR/androidTest/debug/app-debug-androidTest.apk" >/dev/null || bad "install test apk"
adb shell appops set $PKG MANAGE_EXTERNAL_STORAGE allow

# 1. Phone -> PC
WORK=$(mktemp -d); trap 'kill $(jobs -p) 2>/dev/null; rm -rf "$WORK"' EXIT
$CLI receive --data "$WORK/host" --save "$WORK/host-save" --port 47710 > "$WORK/recv.log" 2>&1 &
sleep 3
out=$(adb shell am instrument -w -e class $PKG.DeviceTest#sendsToHost -e hostPort 47710 $PKG.test/androidx.test.runner.AndroidJUnitRunner)
echo "$out" | grep -E "CFS-RESULT|OK \(|FAILURES" || echo "$out" | tail -20
echo "$out" | grep -q "OK (1 test)" && pass "phone -> pc" || bad "phone -> pc"
[ -f "$WORK/host-save/from-phone.bin" ] && [ "$(stat -c %s "$WORK/host-save/from-phone.bin")" = "52428800" ] && pass "file arrived on pc" || bad "file on pc"

# 2. PC -> phone (into Internal storage/ClipFx Share, sorted by type)
adb forward tcp:47630 tcp:47630
adb shell am instrument -w -e class $PKG.DeviceTest#receivesFromHost -e receive 1 $PKG.test/androidx.test.runner.AndroidJUnitRunner > "$WORK/recv-inst.log" &
inst=$!
sleep 12
head -c 30000000 /dev/urandom > "$WORK/movie.mp4"; head -c 2000 /dev/urandom > "$WORK/song.mp3"
$CLI send --data "$WORK/host-send" --to 127.0.0.1:47630 "$WORK/movie.mp4" "$WORK/song.mp3" | tee "$WORK/send.log"
wait $inst
grep -E "CFS-RESULT" "$WORK/recv-inst.log"
grep -q "OK (1 test)" "$WORK/recv-inst.log" && pass "pc -> phone" || { bad "pc -> phone"; tail -30 "$WORK/recv-inst.log"; }
remote=$(adb shell md5sum "/sdcard/ClipFx Share/Videos/movie.mp4" | awk '{print $1}')
[ "$remote" = "$(md5sum "$WORK/movie.mp4" | awk '{print $1}')" ] && pass "video saved in ClipFx Share/Videos, identical" || bad "video on phone ($remote)"
adb shell ls "/sdcard/ClipFx Share/Music/song.mp3" >/dev/null 2>&1 && pass "music sorted into ClipFx Share/Music" || bad "music folder"
adb shell ls "/sdcard/ClipFx Share/" | tr -d '\r' | tr '\n' ' '; echo

# 3. Minified release build: UI, theme, and accepting a real request by tapping
adb uninstall $PKG.test >/dev/null 2>&1
adb install -r -g "$APK_DIR/release/app-release.apk" >/dev/null && pass "release installs over debug (same key)" || bad "install release"
adb shell appops set $PKG MANAGE_EXTERNAL_STORAGE allow
adb shell am force-stop $PKG
adb shell pm clear $PKG >/dev/null   # fresh start: default settings ask before receiving
adb shell appops set $PKG MANAGE_EXTERNAL_STORAGE allow
adb shell pm grant $PKG android.permission.POST_NOTIFICATIONS 2>/dev/null
adb shell cmd uimode night no
adb shell am start -n $PKG/.MainActivity >/dev/null
sleep 6
adb exec-out screencap -p > "$OUT/1-send-light.png"
adb shell cmd uimode night yes; sleep 3
adb exec-out screencap -p > "$OUT/2-send-dark.png"
adb shell cmd uimode night no; sleep 2
pid=$(adb shell pidof $PKG | tr -d '\r'); [ -n "$pid" ] && pass "release app running" || bad "release app crashed"

$CLI send --data "$WORK/host-send2" --to 127.0.0.1:47630 --text "hello phone" "$WORK/song.mp3" > "$WORK/send2.log" &
sender=$!
sleep 6
adb exec-out screencap -p > "$OUT/3-incoming.png"
tap() { # tap the first UI element whose text is $1
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1
  local b; b=$(adb shell cat /sdcard/ui.xml | tr '>' '\n' | grep -m1 "text=\"$1\"" | grep -o 'bounds="[^"]*"' | grep -o '[0-9]\+' | tr '\n' ' ')
  set -- $b
  [ $# -eq 4 ] && adb shell input tap $(( ($1 + $3) / 2 )) $(( ($2 + $4) / 2 )) && return 0
  return 1
}
tap "Accept" && pass "tapped Accept" || bad "Accept button not found"
wait $sender; cat "$WORK/send2.log"
grep -q "RESULT Completed" "$WORK/send2.log" && pass "accepted transfer completed on release build" || bad "release receive"
sleep 2
tap "Transfers"; sleep 2
adb exec-out screencap -p > "$OUT/4-transfers.png"
tap "Settings"; sleep 2
adb exec-out screencap -p > "$OUT/5-settings.png"
adb shell cmd uimode night yes; sleep 2
adb exec-out screencap -p > "$OUT/6-settings-dark.png"
adb logcat -d -s AndroidRuntime:E > "$OUT/crashes.txt"
[ -s "$OUT/crashes.txt" ] && grep -q "FATAL" "$OUT/crashes.txt" && { bad "crash in logcat"; head -40 "$OUT/crashes.txt"; }

[ $fail -eq 0 ] && echo "ANDROID E2E OK" || echo "ANDROID E2E FAILED"
exit $fail
