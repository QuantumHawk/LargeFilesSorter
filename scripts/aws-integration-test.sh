#!/bin/bash
set -euo pipefail

# ── Configuration (passed via ECS task environment variables) ──────────────
S3_BUCKET="${S3_BUCKET:-large-file-sorter-test}"
FILE_SIZE_MB="${FILE_SIZE_MB:-1024}"
WORK_DIR="/tmp/sort-work"
INPUT_FILE="$WORK_DIR/input.txt"
SORTED_FILE="$WORK_DIR/sorted.txt"
S3_INPUT="s3://$S3_BUCKET/input/input.txt"
S3_OUTPUT="s3://$S3_BUCKET/output/sorted.txt"

mkdir -p "$WORK_DIR"

echo "=== AWS Integration Test ==="
echo "S3 bucket  : $S3_BUCKET"
echo "File size  : ${FILE_SIZE_MB} MB"
echo "Work dir   : $WORK_DIR"
echo ""

# ── Disk space check ───────────────────────────────────────────────────────
AVAILABLE_KB=$(df "$WORK_DIR" | awk 'NR==2 {print $4}')
AVAILABLE_MB=$((AVAILABLE_KB / 1024))
# Need ~2× file size: chunks during sort + sorted output
REQUIRED_MB=$((FILE_SIZE_MB * 2 + 2048))
echo "Disk available : ${AVAILABLE_MB} MB"
echo "Disk required  : ${REQUIRED_MB} MB (2× file size + 2 GB buffer)"
if [ "$AVAILABLE_MB" -lt "$REQUIRED_MB" ]; then
    echo "ERROR: Not enough disk space. Have ${AVAILABLE_MB} MB, need ${REQUIRED_MB} MB."
    echo "       For files > 50 GB use the Distributed Sort workflow instead."
    exit 1
fi
echo ""

# ── Step 1: Generate test file ─────────────────────────────────────────────
echo "[1/4] Generating ${FILE_SIZE_MB} MB test file..."
/app/LargeFileGenerator "$INPUT_FILE" "$FILE_SIZE_MB"
ACTUAL_SIZE=$(du -sh "$INPUT_FILE" | cut -f1)
echo "      Generated: $ACTUAL_SIZE"

# ── Step 2: Upload input to S3 ─────────────────────────────────────────────
echo "[2/4] Uploading input to $S3_INPUT ..."
aws s3 cp "$INPUT_FILE" "$S3_INPUT" --no-progress
echo "      Upload complete"

# ── Step 3: Sort ───────────────────────────────────────────────────────────
echo "[3/4] Sorting..."
SORT_START=$(date +%s)

# --delete-input-after-split frees the input file from disk once Phase 1
# (split) is done — the sorter never reads it again after that point.
# This reduces peak disk usage from 3× to 2× the file size, allowing
# 100 GB sorts to fit within 200 GiB Fargate ephemeral storage.
DELETE_INPUT_FLAG=""
if [ "$FILE_SIZE_MB" -gt 20480 ]; then
    DELETE_INPUT_FLAG="--delete-input-after-split"
    echo "      Large file detected — enabling --delete-input-after-split"
fi

/app/LargeFileSort "$INPUT_FILE" "$SORTED_FILE" \
    --chunk-size-mb    "${CHUNK_SIZE_MB:-512}" \
    --merge-fan-in     "${MERGE_FAN_IN:-32}" \
    --temp-dir         "$WORK_DIR/tmp" \
    $DELETE_INPUT_FLAG

SORT_END=$(date +%s)
SORT_SECS=$((SORT_END - SORT_START))
echo "      Sort done in ${SORT_SECS}s"

# ── Step 4: Upload sorted output to S3 ────────────────────────────────────
echo "[4/4] Uploading sorted output to $S3_OUTPUT ..."
aws s3 cp "$SORTED_FILE" "$S3_OUTPUT" --no-progress
echo "      Upload complete"

# ── Verify: spot-check first and last 5 lines ──────────────────────────────
echo ""
echo "=== Verification ==="
echo "First 5 lines of sorted output:"
head -5 "$SORTED_FILE"
echo "..."
echo "Last 5 lines of sorted output:"
tail -5 "$SORTED_FILE"

# Line count check — skip if input was deleted by --delete-input-after-split
if [ -f "$INPUT_FILE" ]; then
    INPUT_LINES=$(wc -l < "$INPUT_FILE")
    SORTED_LINES=$(wc -l < "$SORTED_FILE")
    echo ""
    echo "Input lines : $INPUT_LINES"
    echo "Sorted lines: $SORTED_LINES"
    if [ "$INPUT_LINES" -ne "$SORTED_LINES" ]; then
        echo "ERROR: line count mismatch!"
        exit 1
    fi
else
    SORTED_LINES=$(wc -l < "$SORTED_FILE")
    echo ""
    echo "Sorted lines: $SORTED_LINES (input deleted after split, cannot compare)"
fi

echo ""
echo "=== Test PASSED ==="
echo "Results saved to: $S3_OUTPUT"
echo "Sort time: ${SORT_SECS}s for ${FILE_SIZE_MB}MB"
