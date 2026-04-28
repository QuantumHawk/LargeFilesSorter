# Architecture & Design Reference

Complete explanation of every class in the project — what it does, why it was designed that way, and what alternatives were considered.

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [High-Level Algorithm](#2-high-level-algorithm)
3. [Project Structure](#3-project-structure)
4. [Common Library](#4-common-library)
   - [Record](#record)
   - [RecordComparer](#recordcomparer)
   - [LineParser](#lineparser)
   - [ChunkBinaryFormat](#chunkbinaryformat)
   - [SortOptions](#sortoptions)
   - [GeneratorOptions](#generatoroptions)
   - [InvalidLineMode](#invalidlinemode)
   - [TempFilePolicy](#tempfilepolicy)
   - [SortManifest + SortManifestStore](#sortmanifest--sortmanifeststore)
   - [MetricsSnapshot](#metricssnapshot)
   - [ProgressReporter](#progressreporter)
5. [LargeFileGenerator](#5-largefilegenerator)
   - [Program (Generator)](#program-generator)
   - [TestDataGenerator](#testdatagenerator)
6. [LargeFileSort — Infrastructure](#6-largefilesort--infrastructure)
   - [SortMetrics](#sortmetrics)
   - [TempFileRegistry](#tempfileregistry)
7. [LargeFileSort — IO](#7-largefilesort--io)
   - [BlockLineReader](#blocklinereader)
   - [ChunkFileWriter](#chunkfilewriter)
   - [ChunkFileReader](#chunkfilereader)
8. [LargeFileSort — Pipeline](#8-largefilesort--pipeline)
   - [SplitPhase](#splitphase)
   - [MergePhase](#mergephase)
   - [FinalizePhase](#finalizephase)
9. [LargeFileSort — Merge](#9-largefilesort--merge)
   - [MergeEngine](#mergeengine)
10. [LargeFileSort — Telemetry](#10-largefilesort--telemetry)
    - [SorterTelemetry](#sortertelemetry)
    - [TelemetrySetup](#telemetrysetup)
11. [LargeFileSort — Orchestration](#11-largefilesort--orchestration)
    - [ExternalSorter](#externalsorter)
    - [Program (Sorter)](#program-sorter)
12. [Key Design Decisions](#12-key-design-decisions)

---

## 1. Problem Statement

Sort a text file that can be **100 GB or larger** on a machine with limited RAM.

Each line has the format:
```
Number. String
```
For example:
```
415. Apple
30432. Something something something
1. Apple
32. Cherry is the best
2. Banana is yellow
```

**Sorting rules:**
1. Compare the `String` part first (lexicographic, case-sensitive)
2. If the strings are equal, compare the `Number`

**Constraints:**
- String length ≤ 100 characters
- Number ≤ 10^18 (fits in `ulong`)
- The file does **not** fit in RAM — a naive `File.ReadAllLines().Sort()` would crash

---

## 2. High-Level Algorithm

The solution uses **external merge sort** — a classic algorithm for data that exceeds RAM:

```
Phase 1 — Split
  Read input in streaming fashion
  Accumulate records in RAM until the chunk size limit is reached
  Sort the in-memory batch
  Write it as a sorted binary chunk file to disk
  Repeat until the entire input is consumed
  → N sorted binary chunk files

Phase 2 — Merge
  Group chunk files into batches of MergeFanIn (e.g. 32)
  Merge each batch using a min-heap (k-way merge)
  The output of each merge is a new sorted chunk file
  Repeat until only 1 chunk file remains
  → 1 large sorted binary chunk file

Phase 3 — Finalize
  Stream the final binary chunk file
  Decode each record back to text
  Write to the output text file
  → sorted.txt
```

This keeps RAM usage bounded to `chunk_size_mb` regardless of the input size.

---

## 3. Project Structure

```
LargeFilesSorter.sln
│
├── Common/                         Shared types used by all projects
│   ├── Record.cs                   The core data unit
│   ├── RecordComparer.cs           Sort order logic
│   ├── LineParser.cs               "415. Apple" → Record
│   ├── ChunkBinaryFormat.cs        Binary encoding for chunk files
│   ├── SortOptions.cs              All tuning knobs for the sorter
│   ├── GeneratorOptions.cs         Tuning knobs for the generator
│   ├── InvalidLineMode.cs          What to do with bad lines
│   ├── MetricsSnapshot.cs          Plain stats DTO
│   ├── ProgressReporter.cs         Rate-limited console output
│   ├── SortManifest.cs             Resume checkpoint DTO
│   ├── Manifest/
│   │   └── SortManifestStore.cs    JSON load/save for SortManifest
│   └── Options/
│       └── TempFilePolicy.cs       When to clean up temp files
│
├── LargeFileGenerator/             Utility to create test files
│   ├── Program.cs                  CLI entry point
│   └── TestDataGenerator.cs        File generation logic
│
└── LargeFileSort/                  The actual sorter
    ├── Program.cs                  CLI entry point + argument parsing
    ├── ExternalSorter.cs           Orchestrator (phases 1→2→3)
    ├── Infrastructure/
    │   ├── SortMetrics.cs          Thread-safe runtime counters
    │   └── TempFileRegistry.cs     Tracks temp files for cleanup
    ├── IO/
    │   ├── BlockLineReader.cs      Fast block-level UTF-8 line reader
    │   ├── ChunkFileWriter.cs      Writes records to binary chunk files
    │   └── ChunkFileReader.cs      Reads records from binary chunk files
    ├── Merge/
    │   └── MergeEngine.cs          k-way merge using a min-heap
    ├── Pipeline/
    │   ├── SplitPhase.cs           Phase 1: read → sort → write chunks
    │   ├── MergePhase.cs           Phase 2: multi-pass k-way merge
    │   └── FinalizePhase.cs        Phase 3: binary → text output
    └── Telemetry/
        ├── SorterTelemetry.cs      OTel instrument definitions
        └── TelemetrySetup.cs       OTel provider wiring
```

---

## 4. Common Library

### Record

```csharp
public readonly record struct Record(ulong Number, byte[] Utf8Text)
```

The fundamental unit that flows through the entire pipeline.

**Why `ulong`?**
The spec says Number ≤ 10^18. `ulong.MaxValue` is ~1.84×10^19, so `ulong` fits with headroom. Using `long` would technically work too, but `ulong` makes the constraint explicit and avoids negative number edge cases.

**Why `byte[] Utf8Text` instead of `string`?**
This is the most important design decision in the whole project. The pipeline processes billions of records and comparing strings requires reading every character. UTF-16 strings (C# default) use 2 bytes per character. Storing text as **UTF-8 bytes** halves the memory footprint for ASCII text (which most English text is) and more importantly, SIMD-accelerated `SequenceCompareTo` on byte spans is significantly faster than string comparison — it operates on the native byte representation and processes multiple bytes per CPU instruction.

The UTF-8 → string decoding happens **only once**: in `FinalizePhase` when writing the final output. Everywhere else in the pipeline (sorting, merging, binary I/O), the text stays as raw bytes.

**Why `readonly record struct`?**
- `struct`: avoids heap allocation per record — with billions of records this matters
- `readonly`: immutability prevents accidental mutation in multi-threaded workers
- `record`: compiler-generated equality/hash is a bonus for testing

---

### RecordComparer

```csharp
public sealed class RecordComparer : IComparer<Record>
```

Implements the sort order: text first, then number.

**Why `SequenceCompareTo` on byte spans?**
`SequenceCompareTo` is a JIT-intrinsic that uses SIMD instructions (SSE2, AVX2) to compare multiple bytes per clock cycle. For ASCII/UTF-8 text, ordinal byte comparison gives exactly the same result as lexicographic string comparison because UTF-8 preserves character ordering for code points < 128.

**Why a singleton via `Instance`?**
`List.Sort` accepts an `IComparer<T>`. Creating a new comparer for each sort call would allocate memory; the singleton reuses the same instance everywhere.

**Why not `Comparer<Record>.Create(...)`?**
That creates a delegate-based comparer with an extra virtual dispatch per comparison. A dedicated class with a direct method call is faster in the hot path where compare is called billions of times.

---

### LineParser

```csharp
public static bool TryParse(string line, out Record record)
```

Parses `"415. Apple"` into `Record(415, utf8("Apple"))`.

**Why `ReadOnlySpan<char>` and `IndexOf('.')`?**
Avoids allocating a substring just to find the dot. `span[..dotIndex]` is a view over the existing string, not a copy.

**Why `ulong.TryParse` with `NumberStyles.None`?**
`NumberStyles.None` rejects leading spaces, signs, and formatting characters. The spec says Number is a plain non-negative integer, so this is the correct strict parsing mode.

**Why store text as `byte[]` here?**
`Encoding.UTF8.GetBytes(textSpan.ToString())` is called once per line at parse time. Every subsequent operation (sort, merge, binary write/read) uses the bytes directly. This is cheaper than converting back and forth.

---

### ChunkBinaryFormat

Defines the binary format of chunk files on disk.

**File layout:**
```
[0..7]   Magic bytes: "LFSCHNK1" (UTF-8)
[8..11]  Version: int32 little-endian (currently 2)
[12+]    Records, sequentially:
           [0..7]  Number: uint64 little-endian
           [8..9]  TextByteCount: uint16 little-endian
           [10..]  UTF-8 text bytes (TextByteCount bytes)
```

**Why a custom binary format instead of CSV/JSON?**
Binary is dramatically faster to write and read:
- No character escaping
- No parsing overhead — fixed-size fields read with `BinaryPrimitives`
- Compact — a record with a 20-character text takes 30 bytes binary vs ~23+ bytes as text (and text requires re-parsing on read)

**Why `uint16` for text length (max 65535)?**
The spec says string ≤ 100 characters → ≤ 400 bytes UTF-8 (worst case 4 bytes/char). `uint16` is more than sufficient and saves 2 bytes per record compared to `int32`.

**Why little-endian?**
All modern x86/x64/ARM CPUs are little-endian. `BinaryPrimitives.ReadUInt64LittleEndian` maps directly to a memory read with no byte-swap on these architectures.

**Why a magic number and version?**
Detects corrupted files (e.g., a partial write from a crash) and incompatible format versions from previous runs, producing a clear error instead of a silent wrong sort.

---

### SortOptions

All configuration for a sort run. Uses C# 11 `required` properties and `init`-only setters to make the object immutable after construction.

**Key settings:**

| Property | Default | Why |
|----------|---------|-----|
| `ChunkSizeMb` | 512 | Controls RAM usage. Larger = fewer chunks = fewer merge passes, but uses more RAM. |
| `MergeFanIn` | 64 | How many chunks to merge at once. Higher = fewer passes, but each merge keeps `fanIn` files open simultaneously. |
| `MaxParallelChunkSorters` | `CPU/2` | Parallel sort workers. Half the cores to leave headroom for I/O. |
| `ChunkQueueCapacity` | 2 | Bounded producer-consumer queue depth. Low value keeps memory bounded. |
| `MaxConcurrentMerges` | 2 | Parallel merge jobs. Merges are I/O-bound; more than 2-4 rarely helps on a single disk. |
| `UseBlockReader` | `true` | Use `BlockLineReader` (fast) vs `StreamReader` (simpler). |
| `ResumeIfManifestExists` | `true` | Auto-resume interrupted runs. |

**Why `ChunkSizeBytesOverrideForTests`?**
Unit tests need to trigger multi-chunk behaviour with tiny files (e.g., 100 bytes per chunk). The CLI doesn't expose this flag — it's only for tests.

---

### GeneratorOptions

Configuration for the test file generator. Separated from `SortOptions` to keep the two concerns independent.

**Why `TargetSizeMb` (not exact)?**
Generating exactly N bytes is impossible at line granularity. The generator writes lines until `writtenBytes >= targetBytes`, so the output can be slightly larger. The spec explicitly allows this.

---

### InvalidLineMode

```csharp
public enum InvalidLineMode { Strict, SkipInvalid, LogInvalid }
```

Defines what happens when a line does not match `"Number. String"`:

| Mode | Behaviour |
|------|-----------|
| `Strict` | Throw exception immediately — safest for production data |
| `SkipInvalid` | Count and skip — useful for dirty data |
| `LogInvalid` | Write bad lines to a separate file — for auditing |

**Why three modes?**
Different environments have different tolerance for data quality issues. A production ETL pipeline wants `Strict`. A one-off data migration might prefer `LogInvalid`.

---

### TempFilePolicy

```csharp
public enum TempFilePolicy { KeepAll, DeleteOnSuccess, DeleteAlways }
```

Controls when the temp directory is cleaned up.

- **`KeepAll`**: for debugging — inspect chunk files to diagnose sort issues
- **`DeleteOnSuccess`**: default — clean up only if the sort completed (preserves evidence on failure)
- **`DeleteAlways`**: for constrained disks — clean up even on failure

---

### SortManifest + SortManifestStore

`SortManifest` is a plain JSON checkpoint written to `<temp-dir>/sort-manifest.json` during the sort. It records which phase is active, which chunk files exist, and which merge pass was last completed.

`SortManifestStore` handles the JSON serialization. It caches `JsonSerializerOptions` as a static field — constructing options is expensive and doing it on every save in a hot loop would cause unnecessary allocations.

**Why a manifest?**
Sorting 100 GB can take 30+ minutes. If the process is killed (out of memory, machine restart, ECS task timeout), the manifest allows resuming from the last completed merge pass rather than starting over from scratch.

**Why not a database?**
The manifest is written infrequently (once per merge pass) and is a simple JSON object. A flat file is zero-dependency and robust.

---

### MetricsSnapshot

Plain DTO (`public sealed class` with public setters) that carries a point-in-time snapshot of all runtime counters. Returned by `SortMetrics.Snapshot`.

**Why a separate DTO instead of reading fields directly from `SortMetrics`?**
`SortMetrics` fields are updated by multiple threads. Reading them individually would give a non-atomic snapshot (e.g., `ValidLines` could update between reading it and reading `InputBytesRead`). The snapshot is created with a single pass of `Volatile.Read` calls, which is less error-prone than callers doing multiple reads.

---

### ProgressReporter

Rate-limited console output. Reports at most once every 2 seconds (configurable) unless `force: true` is passed.

**Why rate-limit?**
The hot path processes millions of lines per second. Writing to stdout on every line would make I/O the bottleneck. The default 2-second interval gives a good balance of responsiveness vs overhead.

**Why `DateTime.MinValue` as initial `_lastReportUtc`?**
Ensures the very first call always prints, regardless of when it happens.

---

## 5. LargeFileGenerator

### Program (Generator)

CLI entry point. Parses arguments positionally:
```
TestFileGenerator <outputPath> <targetSizeMb> [distinctStrings=10000] [seed=12345]
```

The `--size-mb` flag format is also supported in the `aws-integration-test.sh` script. The positional format is used from the CLI directly.

### TestDataGenerator

Generates a test file by:

1. **Building a string pool** of `distinctStrings` (default 10,000) random phrases from word lists of adjectives, nouns, and suffixes. This creates realistic duplicates — sorting requires handling many equal-string records.
2. **Writing lines** in a loop until `writtenBytes >= targetBytes`, picking a random number and a random phrase from the pool each iteration.

**Why a pre-built string pool?**
Generating a completely random string for each of billions of lines would produce almost no duplicates. The sort test is more interesting with many duplicate strings (to exercise the number-comparison fallback). The pool size of 10,000 means ~every string appears thousands of times in a 1 GB file.

**Why `ulong NextUInt64` via `stackalloc byte[8]`?**
`Random.NextInt64()` returns negative values. Using 8 random bytes and casting to `ulong` gives a uniform distribution in `[0, 10^18)` without sign concerns.

**Why `UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`?**
Suppresses the BOM (byte order mark). BOM in UTF-8 files causes the first line to start with `\uFEFF`, which breaks sorting (it sorts before any printable character) and causes test failures.

---

## 6. LargeFileSort — Infrastructure

### SortMetrics

Thread-safe runtime metrics using `Interlocked` operations.

**Why `Interlocked.Add` instead of `lock`?**
Multiple worker threads (chunk sorters, merge workers) update the counters concurrently. `Interlocked.Add` is a single atomic CPU instruction — it is orders of magnitude faster than acquiring a lock, and for simple counter increments it provides the same correctness guarantee.

**Why a CAS loop for `PeakManagedMemoryBytes`?**
Peak tracking requires a conditional update: only update if the new value is higher. This needs atomicity to avoid a race where two threads both read the same old peak, both compute a new value, and one overwrites the other's result. The compare-and-swap (CAS) loop retries if another thread updated the value between the read and the write.

**Why `Volatile.Read` in `Snapshot`?**
Ensures the reading thread sees the latest value written by any other thread. Without `Volatile.Read`, the JIT or CPU might serve a cached/stale value from a register.

**Why save metrics to JSON?**
The metrics file (`--metrics-path`) gives a human-readable post-run summary: how many lines were valid, how much data was read/written, peak memory usage. Useful for benchmarking and debugging without needing a full observability stack.

---

### TempFileRegistry

Thread-safe registry of all temp files created during a run.

**Why `ConcurrentDictionary<string, byte>`?**
Multiple chunk-writer workers register files concurrently. `ConcurrentDictionary` provides lock-free registration without external synchronization. The `byte` value is unused — it's effectively a `ConcurrentHashSet`.

**Why not `List<string>` with a `lock`?**
A lock would serialize all registrations. With many parallel workers this creates contention. `ConcurrentDictionary` is designed exactly for this pattern.

**Why `DeleteAllSafe` swallows exceptions?**
Cleanup is best-effort. If a temp file is locked by another process or already deleted, we don't want cleanup to throw and obscure the original error.

---

## 7. LargeFileSort — IO

### BlockLineReader

High-throughput line reader that reads the file in large byte blocks (default 4 MB) and scans for newlines manually.

**Why not `StreamReader.ReadLine()`?**
`StreamReader.ReadLine()` allocates a new `string` for every line and has overhead from its internal UTF-16 conversion and buffer management. For a 100 GB file with billions of lines, these allocations overwhelm the GC and cause significant pauses.

`BlockLineReader` reads large blocks of raw bytes, finds `\n` characters by scanning the buffer, and only allocates a string once per line using `Encoding.UTF8.GetString`. The large block size means one syscall reads megabytes at a time instead of kilobytes.

**Why `ArrayPool<byte>.Shared`?**
ArrayPool reuses byte arrays instead of allocating fresh ones. The internal line-accumulation buffer (`_lineBuffer`) grows dynamically but always rents from the pool, keeping GC pressure low.

**Why `FileOptions.SequentialScan`?**
Tells the OS that the file will be read from start to finish without seeking. This enables OS-level read-ahead — the OS prefetches the next block while the CPU is processing the current one, hiding I/O latency.

**Why handle both `LF` and `CRLF`?**
Input files may originate from Linux (LF) or Windows (CRLF). The reader strips the trailing `\r` when it finds `\r\n`.

---

### ChunkFileWriter

Writes `Record` values to a binary chunk file, preceded by a file header.

**Why `ArrayPool<byte>.Shared` for the encode buffer?**
`ChunkBinaryFormat.EncodeRecord` writes into a caller-supplied buffer. Renting from the pool avoids allocating a new buffer for each record.

**Why buffer growth by doubling (`Math.Max(_buffer.Length * 2, needed)`)?**
Grows the buffer geometrically to amortize the cost of reallocations. Without doubling, writing many records of increasing size causes O(n) re-allocations instead of O(log n).

---

### ChunkFileReader

Reads `Record` values from a binary chunk file, validates the header.

**Why separate header and text buffers?**
The record header is always exactly `RecordHeaderSize` (10) bytes. Reading it into a dedicated buffer avoids conditionally sizing a unified buffer. The text buffer is separate because its required size varies per record and may need to grow.

**Why `EnsureTextBuffer` with pool rent/return?**
The text length varies from 1 to 400 bytes. Renting a buffer of at least the needed size and growing it when necessary (while returning the old one) keeps the GC clean.

---

## 8. LargeFileSort — Pipeline

### SplitPhase

Phase 1: reads the input file, partitions it into fixed-size batches, sorts each batch in memory, and writes sorted binary chunk files.

**Architecture: producer + worker pool via `Channel<T>`**

```
Producer task (single)
  ├─ reads lines sequentially
  ├─ accumulates records until chunk size is reached
  └─ posts SortChunk to a bounded Channel

Worker tasks (N parallel)
  ├─ drain the channel
  ├─ sort the chunk in memory (List.Sort with RecordComparer)
  └─ write sorted binary chunk file
```

**Why a bounded `Channel<T>` with `ChunkQueueCapacity = 2`?**
If the producer is faster than the workers, an unbounded channel would buffer all chunks in RAM — defeating the purpose. The bounded channel creates back-pressure: the producer blocks until a worker is ready. A capacity of 2 means the producer can stay one chunk ahead of the workers (pipeline parallelism) without accumulating more.

**Why `CancellationTokenSource` across all tasks?**
If any worker fails, it calls `cts.Cancel()` before throwing. This causes the producer (blocked on `channel.Writer.WriteAsync(chunk, cts.Token)`) to receive `OperationCanceledException` and also stop. Without this, the producer would block indefinitely waiting for a dead worker.

**Why `ExceptionDispatchInfo.Capture(real).Throw()`?**
`Task.WhenAll` wraps exceptions in an `AggregateException`. The code unwraps the first non-cancellation exception and re-throws it with its **original stack trace** preserved. Without this, the stack trace would point to the `throw` site in `ExternalSorter`, not the actual failure location.

**Why `InvalidLineMode.LogInvalid` uses a separate unbounded `Channel<string>`?**
If invalid lines were written to the log file inside the producer task (which is in the hot path), every invalid line would serialize all other processing. The dedicated log-writer task drains the channel asynchronously, so the producer just does a non-blocking `TryWrite` and continues.

**Why estimate chunk size with `EstimateRecordBytes`?**
`sizeof(ulong) + utf8.Length + overhead`. The estimate intentionally uses 1 byte per character rather than `sizeof(char) == 2`. Using the UTF-16 size would cause chunks to be written at half the target size (512 MB chunks would write at ~256 MB), wasting I/O cycles.

---

### MergePhase

Phase 2: repeats k-way merge passes until only one chunk file remains.

**Multi-pass k-way merge:**
```
Pass 1: [c1,c2,...,c64] → [m1], [c65,...,c128] → [m2], ...
Pass 2: [m1,m2,...,m64] → [n1], ...
...
Until: one file remains
```

**Why `SemaphoreSlim(maxConcurrentMerges)`?**
Batches within a pass are independent and can run in parallel. The semaphore bounds the number of concurrent merges to avoid opening too many files simultaneously (each merge opens `MergeFanIn` readers) and saturating disk I/O.

**Why delete previous-pass files with a `HashSet<string>` check?**
After each pass, the input chunks are no longer needed. Deleting them reclaims disk space so the total used space stays bounded to approximately `2× the current chunk set size`. The `HashSet` replaces an `O(n)` `List.Contains` that was in the original code.

**Why `manifest.CurrentChunkFiles = nextFiles` after each pass?**
If the process crashes mid-merge, the manifest records which files are the current valid state. On resume, the sorter can skip completed passes and continue from where it stopped.

---

### FinalizePhase

Phase 3: streams the final sorted binary chunk, decodes each record to text, and writes the output file.

**Why decode UTF-8 only here?**
This is the one and only place where `byte[] → string` conversion happens. The entire split and merge pipeline operated on raw bytes. This design eliminates billions of unnecessary string allocations.

**Why write with `UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`?**
Suppresses the BOM. A BOM in the output file would appear as `\uFEFF` at the start of the first line in many text editors and tools, corrupting the output.

**Why write `record.Number` and `". "` and the text separately instead of `record.ToString()`?**
`record.ToString()` allocates a new formatted string `$"{Number}. {Text}"`. Writing the parts individually (`writer.Write(record.Number)`, `writer.Write(". ")`, `writer.WriteLine(...)`) avoids this allocation in the hot loop where billions of lines are written.

**Why `writer.Flush()` before reading `outputStream.Position`?**
`StreamWriter` buffers internally. Without an explicit flush, `outputStream.Position` would reflect only the bytes the OS has received, not the total bytes written. Flushing ensures the position equals the true file size.

---

## 9. LargeFileSort — Merge

### MergeEngine

Merges N sorted binary chunk files into one sorted binary chunk file using a **min-heap** (k-way merge).

**Algorithm:**
1. Open all input chunk readers
2. Read the first record from each reader and push to a `PriorityQueue`
3. Loop: pop the minimum record, write it to the output, read the next record from the same source and push it back
4. Stop when the queue is empty

**Why `PriorityQueue<QueueItem, QueueItem>`?**
.NET's `PriorityQueue<TElement, TPriority>` is a binary min-heap. The element and priority are the same `QueueItem` struct, so there is no separate key. This gives O(log k) dequeue where k is the number of open chunk files — far better than O(k) for a linear scan.

**Why `QueueItemComparer` breaks ties by `ReaderIndex`?**
If two records are identical (same text and same number), a deterministic tie-break by reader index ensures the merge output order is stable and reproducible regardless of scheduling.

**Why is `MergeEngine` separate from `MergePhase`?**
Single responsibility: `MergeEngine` knows only how to merge a fixed list of files into one. `MergePhase` knows how to orchestrate multiple passes of merges, track the manifest, and clean up intermediate files. This makes `MergeEngine` independently testable.

---

## 10. LargeFileSort — Telemetry

### SorterTelemetry

Static registry of all OpenTelemetry instruments. Created once at startup (static initializers).

**Instruments:**

| Name | Type | Purpose |
|------|------|---------|
| `sort.lines.valid` | Counter | Lines successfully parsed |
| `sort.lines.invalid` | Counter | Lines that failed parsing |
| `sort.chunks.created` | Counter | Chunk files written in split phase |
| `sort.merge.passes` | Counter | Merge passes completed |
| `sort.records.merged` | Counter | Records processed across all merges |
| `sort.lines.written` | Counter | Lines written to final output |
| `sort.bytes.input` | UpDownCounter | Total bytes read from input |
| `sort.bytes.output` | UpDownCounter | Total bytes written to output |
| `sort.phase.split.duration` | Histogram | Wall-clock time for split phase |
| `sort.phase.merge.duration` | Histogram | Wall-clock time for merge phase |
| `sort.phase.finalize.duration` | Histogram | Wall-clock time for finalize phase |
| `sort.chunk.sort.duration` | Histogram | Per-chunk in-memory sort time (tagged by `chunk_index`) |

**Why static fields for instruments?**
Instruments are created once and reused everywhere. Creating a new Counter on each measurement would be incorrect (instruments are registered with the SDK at creation time) and wasteful.

**Why `ActivitySource` for tracing?**
`System.Diagnostics.ActivitySource` is the .NET standard for OpenTelemetry tracing — it is part of the BCL and requires no external package. The OTel SDK hooks into it transparently.

---

### TelemetrySetup

Builds and owns the `TracerProvider` and `MeterProvider` for the process lifetime.

**Why OTLP only (no console exporter)?**
Console output is for human readability. The proper destination for structured telemetry (flame graphs, metric charts) is a backend (AWS X-Ray, Grafana). In ECS, the **ADOT Collector sidecar** receives OTLP on `localhost:4317` and forwards traces to X-Ray and metrics to CloudWatch. 

**Why `if (otlpEnabled)` conditional?**
Locally (no ADOT collector), setting `OTEL_EXPORTER_OTLP_ENDPOINT` is not required. Without the env var, providers are built with no exporters — a silent no-op. This avoids confusing connection errors in development.

**Why `OtelState : IDisposable`?**
`TracerProvider` and `MeterProvider` buffer telemetry internally. `Dispose` triggers `ForceFlush` which pushes all pending data to the exporter before the process exits — ensuring no spans are lost at the end of a short-lived CLI run.

---

## 11. LargeFileSort — Orchestration

### ExternalSorter

The top-level orchestrator. `Sort()` runs the three phases in sequence, wires up resume logic, and handles success/failure cleanup.

**Resume logic:**
```
If manifest exists AND --resume is set:
    Load manifest
    Skip phases that are already done
    Continue from current merge pass
Else:
    Create fresh manifest
    Run all three phases
```

**Atomic output:**
The output file is first written to `<outputPath>.tmp`. Only after `FinalizePhase` completes successfully is it moved to the final path using `File.Move(..., overwrite: true)`. This prevents a partial file from appearing at the output path if the process crashes during phase 3.

**Path access validation (`ProbeReadAccess`, `ProbeWriteAccess`):**
Before doing any work, the sorter verifies it can read the input and write to the output/temp directories by opening/creating a probe file. This "fail fast" pattern produces a clear error message (e.g., `"Cannot write to output directory 'C:\' — choose a path inside your user folder"`) rather than a confusing `UnauthorizedAccessException` 30 minutes into a sort.

**Why `ExternalSorter` is `public` but phases are `internal`?**
`ExternalSorter` is the public API used by tests and `Program.cs`. The pipeline phases are implementation details — making them `internal` prevents tests from depending on them directly.

---

### Program (Sorter)

CLI entry point. Parses arguments manually (no third-party library) and constructs `SortOptions`.

**Why no argument-parsing library (e.g., `System.CommandLine`)?**
The CLI has ~20 flags — manageable with a `switch` statement. Adding a dependency for argument parsing introduces a transitive dependency tree, increases the published binary size, and can interfere with `PublishSingleFile` trimming.

**Why is `TelemetrySetup.Configure()` called before the `try` block?**
`using var otel = TelemetrySetup.Configure()` is a C# "using declaration" — it disposes at the end of the method regardless of whether an exception occurs. This ensures the OTel providers are always flushed, even when the sort fails.

---

## 12. Key Design Decisions

### UTF-8 bytes instead of strings throughout the pipeline

**Problem**: One billion records × 2 bytes/char (UTF-16) = double memory vs UTF-8. String comparison requires UTF-16 on .NET.

**Solution**: Parse text to `byte[]` at input, keep bytes through sort and merge, decode to string only at final output.

**Benefit**: ~50% less memory for ASCII text, SIMD-accelerated comparison via `SequenceCompareTo`.

---

### Binary chunk format instead of text

**Problem**: Text chunk files require re-parsing on every read (find dot, parse number, extract text), which is CPU-intensive at scale.

**Solution**: Binary format with fixed-width `uint64` number + `uint16` text length + raw UTF-8 bytes.

**Benefit**: Reads are near-zero-overhead `BinaryPrimitives` operations. No parsing, no allocation.

---

### Producer-consumer pipeline with back-pressure

**Problem**: If reading is faster than sorting, chunks accumulate in memory. If sorting is faster than I/O, workers idle.

**Solution**: Bounded `Channel<SortChunk>` with capacity 2. Producer blocks when both slots are full, ensuring RAM usage is bounded to `ChunkSizeBytes × (queueCapacity + workerCount)`.

---

### External merge sort with multi-pass merging

**Problem**: Can't sort 100 GB in RAM.

**Solution**: Split into N RAM-sized chunks, multi-pass k-way merge to consolidate.

**Why k-way instead of binary merge?**
Binary merge (k=2) of N chunks requires O(log₂ N) passes. With k=64 and 200 chunks, that's 1 pass (200 < 64²). With k=2, that's 8 passes. Fewer passes = less total I/O = significantly faster.

---

### Resume via manifest

**Problem**: Sorting 100 GB takes 30+ minutes. Process can be killed at any time.

**Solution**: Write the manifest (current phase + current chunk files) to disk after every merge pass. On restart, load the manifest and skip completed work.

---

### Atomic output with `.tmp` write + rename

**Problem**: If the process crashes during final output, the output file would be partial/corrupt.

**Solution**: Write to `<output>.tmp`, atomically rename to `<output>` on success. The `.tmp` file is always deleted at startup if it exists.

---

### Interlocked metrics instead of lock

**Problem**: Multiple threads update counters concurrently; a coarse lock serializes all updates.

**Solution**: One `Interlocked.Add` per counter — atomic, no lock, O(1).

---

### OpenTelemetry with ADOT → X-Ray + CloudWatch

**Problem**: A long-running ECS task produces no observable telemetry unless explicitly instrumented.

**Solution**: Emit `ActivitySource` spans (one per phase) and `Meter` instruments (counters + histograms). The ADOT Collector sidecar forwards via OTLP to AWS X-Ray (trace flame graphs) and CloudWatch Metrics (charts).

**Why no console exporter?**
Console output is for humans reading logs. Structured telemetry belongs in a backend with query/visualization capabilities. CloudWatch Logs already captures stdout; adding an OTel exporter on top would duplicate data and clutter the logs.

