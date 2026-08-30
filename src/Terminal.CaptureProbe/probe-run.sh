#!/bin/bash
# Drive the probe: screenshots between dumps, region from the probe's own metadata.
DIR=${1:-/tmp/probe}; SECS=${2:-30}
rm -rf "$DIR"; mkdir -p "$DIR"
cd ~/Git/term-perf
PROBE_DIR="$DIR" nohup dotnet run --project src/Terminal.CaptureProbe -c Release --no-build > "$DIR/app.log" 2>&1 &
APP=$!
# wait for the first dump so the metadata exists
for i in $(seq 1 60); do ls "$DIR"/dump-*.txt >/dev/null 2>&1 && break; sleep 0.5; done
META=$(head -1 $(ls "$DIR"/dump-*.txt | head -1))
IFS='|' read CW CH COLS ROWS SCALE OX OY <<< "$META"
W=$(python3 -c "print(int($COLS*$CW))"); H=$(python3 -c "print(int($ROWS*$CH))")
echo "region: ${OX},${OY} ${W}x${H} (scale $SCALE)"
END=$((SECONDS+SECS))
while [ $SECONDS -lt $END ]; do
  # capture right after each new dump appears, tagged with its sequence number
  LAST=$(ls "$DIR"/dump-*.txt 2>/dev/null | tail -1)
  N=$(basename "$LAST" | grep -o '[0-9]*')
  [ -n "$N" ] && screencapture -x -R"${OX},${OY},${W},${H}" "$DIR/shot-$N.png" 2>/dev/null
  sleep 0.5
done
kill $APP 2>/dev/null
echo captured; ls "$DIR" | grep -c shot
