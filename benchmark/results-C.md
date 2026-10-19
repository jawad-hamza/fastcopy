# FastCopy benchmark results

- Date: 2026-10-19
- PC: 13th Gen Intel(R) Core(TM) i5-13450HX, 16 GB RAM, Microsoft Windows 11 Home (build 26300)
- From: C: Micron MTFDKCD512QGN-1BN1AABLA (SSD, NVMe, NTFS)
- To: C: Micron MTFDKCD512QGN-1BN1AABLA (SSD, NVMe, NTFS)
- Each time is the median of 3 runs. Methods are interleaved run by run. Every copy is verified (file count + bytes).
- Before every timed copy the write cache is flushed and the script waits until the disks are idle.
- Time = until the tool reports it is done (like the moment a copy dialog closes). The source is read from a warm cache for every method.

## Many small files (like a code / documents folder) - 10,000 x 4 KB = 39 MB

| Method | Time (median) | Fastest - slowest run | Speed | Files/s | vs Explorer |
|---|---:|---:|---:|---:|---:|
| Windows Explorer engine | 33.48 s | 29.60 - 35.25 s | 1 MB/s | 299 | 1.0x |
| PowerShell Copy-Item | 20.97 s | 12.13 - 21.02 s | 2 MB/s | 477 | 1.6x |
| robocopy (single-threaded) | 6.04 s | 4.55 - 6.55 s | 6 MB/s | 1,656 | 5.5x |
| robocopy /MT:8 | 2.50 s | 2.41 - 2.99 s | 16 MB/s | 4,000 | 13.4x |
| robocopy /MT:32 | 2.27 s | 2.13 - 2.30 s | 17 MB/s | 4,405 | 14.7x |
| robocopy /MT:128 | 2.31 s | 2.22 - 2.65 s | 17 MB/s | 4,329 | 14.5x |
| **FastCopy, 16 threads (incl. scan)** | 3.39 s | 3.13 - 3.40 s | 12 MB/s | 2,950 | 9.9x |

## Photo-sized files - 300 x 4 MB = 1.17 GB

| Method | Time (median) | Fastest - slowest run | Speed | Files/s | vs Explorer |
|---|---:|---:|---:|---:|---:|
| Windows Explorer engine | 2.67 s | 2.44 - 9.13 s | 449 MB/s | 112 | 1.0x |
| PowerShell Copy-Item | 1.83 s | 1.71 - 4.92 s | 656 MB/s | 164 | 1.5x |
| robocopy (single-threaded) | 5.05 s | 1.25 - 5.78 s | 238 MB/s | 59 | 0.5x |
| robocopy /MT:8 | 1.04 s | 0.79 - 3.91 s | 1.13 GB/s | 288 | 2.6x |
| robocopy /MT:32 | 5.64 s | 1.24 - 7.02 s | 213 MB/s | 53 | 0.5x |
| robocopy /MT:128 | 1.78 s | 0.97 - 7.86 s | 674 MB/s | 169 | 1.5x |
| **FastCopy, 16 threads (incl. scan)** | 3.23 s | 1.83 - 5.33 s | 372 MB/s | 93 | 0.8x |

## Large files (like videos) - 2 x 1.00 GB = 2.00 GB

| Method | Time (median) | Fastest - slowest run | Speed | Files/s | vs Explorer |
|---|---:|---:|---:|---:|---:|
| Windows Explorer engine | 8.84 s | 6.08 - 14.54 s | 232 MB/s | 0 | 1.0x |
| PowerShell Copy-Item | 8.05 s | 4.54 - 11.68 s | 254 MB/s | 0 | 1.1x |
| robocopy (single-threaded) | 8.13 s | 6.52 - 12.19 s | 252 MB/s | 0 | 1.1x |
| robocopy /MT:8 | 7.95 s | 5.69 - 10.49 s | 258 MB/s | 0 | 1.1x |
| robocopy /MT:32 | 6.48 s | 4.01 - 8.05 s | 316 MB/s | 0 | 1.4x |
| robocopy /MT:128 | 9.36 s | 2.21 - 10.58 s | 219 MB/s | 0 | 0.9x |
| **FastCopy, 16 threads (incl. scan)** | 7.87 s | 2.92 - 8.24 s | 260 MB/s | 0 | 1.1x |

## What this shows

- **Many small files: multi-threading wins big.** Even the slowest multi-threaded robocopy run (2.99 s) was ~10x faster than the fastest Explorer run (29.60 s); the ranges don't come close to overlapping. This is where Explorer's one-file-at-a-time copying hurts.
- **FastCopy is ~1 s slower than raw robocopy here** because it scans the folder first. The scan is what gives it accurate progress, time-left, the replace/skip question and the free-space check.
- **Photo- and video-sized files: no clear winner on this PC.** Every method has both fast and slow runs. Once files are big, the drive itself is the limit, and this laptop's QLC SSD slows down whenever its fast write cache fills. More threads don't fix a slow drive.
- Run `benchmark\bench.ps1 -Target E:\` to test your own USB drive.
