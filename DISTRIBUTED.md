# Distributed Sort — Map-Reduce on AWS ECS

Horizontal scaling for very large files using multiple parallel ECS Fargate tasks.  
The same `LargeFileSort` binary handles all modes via the `--mode` flag.

---

## How it works

```
S3: input/input.txt  (e.g. 100 GB)
        │
        │  byte-range GET (each worker gets 1/N of the file)
        ▼
┌──────────────────────────────────────────┐
│  Map Worker 0   │  Map Worker 1  │  ...  │   (N parallel ECS tasks)
│                 │                │       │
│  1. Download    │  1. Download   │       │
│     slice       │     slice      │       │
│  2. SplitPhase  │  2. SplitPhase │       │
│  3. MergePhase  │  3. MergePhase │       │
│  4. Upload      │  4. Upload     │       │
│     part_0.bin  │     part_1.bin │       │
└──────────────────────────────────────────┘
        │                │
        ▼                ▼
   S3: distributed/<run_id>/parts/part_000000.bin
   S3: distributed/<run_id>/parts/part_000001.bin
   ...
        │
        │  download all parts
        ▼
┌──────────────────────────┐
│  Reduce Worker           │   (1 ECS task)
│                          │
│  1. Download all parts   │
│  2. MergeEngine (k-way)  │
│  3. FinalizePhase        │
│  4. Upload sorted.txt    │
└──────────────────────────┘
        │
        ▼
S3: distributed/<run_id>/output/sorted.txt
```

---

## Line boundary alignment

Each map worker is assigned a nominal byte range `[start, end]`.  
S3 byte-range GETs can land in the middle of a line, so the worker trims:

- **Worker 0**: starts at byte 0 — no trimming needed at the start.
- **Workers 1..N-1**: download from `start - 1KB`, skip bytes until the first `\n` → actual start is the next complete line.
- **All workers**: include the line that crosses `end`, then stop. This ensures every line belongs to exactly one worker.

Max line size is ~122 bytes (20-digit number + `. ` + 100-char text). The 1 KB alignment buffer is always sufficient.

---

## Prerequisites

The same AWS resources used by the integration test are reused:

| Resource | Name |
|----------|------|
| ECR repo | `large-file-sorter` |
| ECS cluster | `large-file-sorter-cluster` |
| S3 bucket | `large-file-sorter-test-quantumhawk` |
| ECS execution role | `ecsTaskExecutionRole` |
| ECS task role | `ecsTaskRole` (needs S3 read/write) |

**The input file must already exist in S3** at `input/input.txt`.  
Run the **AWS Integration Test** workflow first to generate and upload a test file.

---

## Running distributed sort

### Via GitHub Actions (recommended)

1. Go to **Actions → Distributed Sort (Map-Reduce) → Run workflow**
2. Fill in parameters:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `file_size_mb` | `1024` | Set to `0` to use existing `input/input.txt` |
| `worker_count` | `4` | Number of parallel map workers |
| `chunk_size_mb` | `512` | In-memory chunk size per worker |
| `merge_fan_in` | `32` | k-way merge fan-in per worker |
| `run_id` | _(auto)_ | Leave blank to auto-generate a unique run tag |

3. The workflow:
   - Builds and pushes the Docker image to ECR
   - Registers an ECS task definition
   - Launches `worker_count` map tasks in parallel
   - Waits for all map tasks to complete
   - Launches 1 reduce task
   - Shows S3 results + first/last 5 lines of output

### Manually via CLI

```bash
# Map worker (run once per worker with a different --worker-id)
./LargeFileSort \
  --mode map \
  --s3-bucket large-file-sorter-test-quantumhawk \
  --s3-input-key input/input.txt \
  --s3-parts-prefix distributed/myrun/parts/ \
  --worker-id 0 \
  --worker-count 4 \
  --chunk-size-mb 512 \
  --merge-fan-in 32 \
  --aws-region us-east-1 \
  --temp-dir /tmp/sort-work

# Reduce worker (run after all map workers finish)
./LargeFileSort \
  --mode reduce \
  --s3-bucket large-file-sorter-test-quantumhawk \
  --s3-parts-prefix distributed/myrun/parts/ \
  --s3-output-key distributed/myrun/output/sorted.txt \
  --worker-count 4 \
  --merge-fan-in 32 \
  --aws-region us-east-1 \
  --temp-dir /tmp/sort-work
```

---

## S3 layout

```
large-file-sorter-test-quantumhawk/
├── input/
│   └── input.txt                          ← source file
└── distributed/
    └── <run_id>/
        ├── parts/
        │   ├── part_000000.bin            ← sorted binary from worker 0
        │   ├── part_000001.bin            ← sorted binary from worker 1
        │   └── ...
        └── output/
            └── sorted.txt                 ← final merged sorted output
```

Parts are sorted **binary** files (the internal `LFSCHNK1` format).  
The reduce worker merges them directly with the existing `MergeEngine` — no re-parsing needed.

---

## Sizing guide

### Workers vs file size

| File size | Workers | RAM per worker | Fargate size |
|-----------|---------|---------------|--------------|
| 10 GB     | 2       | 2 GB slice → 512 MB chunks → ~4 chunk files | 4 vCPU / 8 GB |
| 100 GB    | 4       | 25 GB slice → 512 MB chunks → ~50 chunk files | 4 vCPU / 16 GB |
| 100 GB    | 10      | 10 GB slice → 512 MB chunks → ~20 chunk files | 4 vCPU / 8 GB |
| 1 TB      | 10      | 100 GB slice → 512 MB chunks → ~200 chunk files | 4 vCPU / 16 GB |

### Reduce worker RAM

The reduce worker downloads all parts (binary). Parts are ~equal to the input size total.  
It only keeps `MergeFanIn` records in the min-heap at once — RAM usage is bounded to a few GB regardless of total data size.

### Total S3 I/O per run

```
Map phase:   download 1× input + upload N× parts  ≈ 2× input size
Reduce phase: download N× parts + upload 1× output ≈ 2× input size
Total:       ≈ 4× input size in S3 transfer
```

For 100 GB input: ~400 GB transfer. At $0.09/GB egress that's ~$36 in transfer costs.

---

## Compared to single-machine mode

| | Single machine | Distributed (N=4) |
|-|---------------|-------------------|
| RAM required | `chunk_size_mb` | `fileSize/N + chunk_size_mb` per worker |
| Wall-clock time (100 GB) | ~30 min | ~10 min (map in parallel, ~15 min reduce) |
| Complexity | Low | Medium |
| Cost | 1 × Fargate task | N+1 × Fargate tasks |
| When to use | ≤ 1 TB, SSD available | Need faster wall-clock, or file > 500 GB |

For most cases, **single-machine mode on a large Fargate instance is simpler and cheaper**.  
Use distributed mode when you need to sort **multiple terabytes** or need results faster.

---

## Troubleshooting

### Map worker fails immediately
- Check `ecsTaskRole` has `AmazonS3FullAccess`
- Verify `s3://large-file-sorter-test-quantumhawk/input/input.txt` exists
- Check CloudWatch logs: `/ecs/large-file-sorter` → `dist/large-file-sorter/<task-id>`

### Reduce worker finds no parts
- Map workers may have failed — check their exit codes in the "Wait for map workers" step
- Parts are uploaded to `s3://<bucket>/distributed/<run_id>/parts/` — verify with:
  ```bash
  aws s3 ls s3://large-file-sorter-test-quantumhawk/distributed/ --recursive
  ```

### Output looks wrong (lines out of order)
- Verify all `worker_count` parts were uploaded (listing should show exactly N `.bin` files)
- A missing part means one map worker silently failed

