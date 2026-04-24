# Large File Sorter

High-performance external sort for very large files (100GB+).

## Format

```
Number. String
```

Example:

```
415. Apple
1. Apple
2. Banana
```

## Sorting rules

1. Compare `String`
2. If equal → compare `Number`

---

## Algorithm

External merge sort:

1. Split → sort chunks
2. Multi-pass merge
3. Final write

---

## Features

* External sorting (RAM-safe)
* Parallel chunk processing
* Limited parallel merge
* Resume via manifest
* Metrics
* Invalid line handling
* Atomic output

---

## Run

```bash
dotnet run --project src/LargeFileSorter -- data.txt sorted.txt
```

---

## Advanced

```bash
dotnet run --project src/LargeFileSorter -- data.txt sorted.txt \
  --chunk-size-mb 512 \
  --merge-fan-in 32 \
  --max-parallel-chunk-sorters 4 \
  --chunk-queue-capacity 2 \
  --max-concurrent-merges 2
```

---

## Notes

* Use SSD/NVMe for best performance
* Tune chunk size based on RAM
* Merge parallelism depends on disk speed
