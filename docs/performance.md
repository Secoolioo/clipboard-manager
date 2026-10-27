# Performance

A clipboard manager runs all day, so it is held to strict budgets. Numbers below are real
measurements of the published single-file EXE (`dotnet publish -c Release -r win-x64`), taken on
2026-10-06 on a Windows 11 laptop with integrated AMD graphics and real-time antivirus scanning on.

## Results

| Metric | Target | Measured | How |
|---|---|---|---|
| Private bytes, idle, after the UI rendered | ≤ 45 MB | **33.1–35.4 MB** | `--selftest` report (`Process.PrivateMemorySize64`) |
| CPU while idle | ≈ 0 | **15.6 ms CPU in 120 s** (≈ 0.01 %, one scheduler tick) | `--selftest --idle 120` |
| Popup open, warm (Show → render pass done) | ≤ 30 ms | **7.7–9.0 ms median, 14 ms max** | 20 cycles in `--selftest` |
| Search, 5,000 typical entries | ≤ 2 ms | **0.17 ms median, 0.35 ms p95** | `tools/AssetGen bench` |
| Search, 5,000 × 4,096-char entries (worst case) | ≤ 20 ms | **3.2 ms median, 4.5 ms p95** | `tools/AssetGen bench` |
| Process start → checks done (warm) | ≤ 1.5 s | **~1.0–1.6 s** | `--selftest` process age |
| Saving a 4K picture (decode, PNG, thumbnail – on the reader thread, never the UI) | ≤ 500 ms | **116 ms** x64, 198 ms ARM64 ¹ | `--selftest` |
| Private bytes after saving a 4K picture | ≤ 45 MB | **27.8 MB** (28.2 MB before it) ¹ | `--selftest` |
| First ever start (native DLL extraction) | ≤ 5 s | **~4.9–5.1 s** | first `--selftest` after publishing |
| EXE size (x64) | – | **142.7 MB** (zip 59.1 MB) | file size |

¹ Measured on 2026-10-27 on the GitHub Actions runners (`windows-2025`, `windows-11-arm`) with a
synthetic 3840×2160 picture. Real screenshots compress less well than that test picture, so encoding
them takes somewhat longer; the memory is given back the same way.

## Design choices that make these numbers possible

- **Event-driven only.** `AddClipboardFormatListener` notifications; no polling. Timers exist only
  as one-shots (end of a timed pause, tray retry right after login, end of the quiet start).
- **Quiet start at sign-in.** Started by Windows (`--autostart`), the app runs at below-normal
  priority and defers popup pre-warming and cleanup for 45 seconds or until it is first opened;
  capture and the hotkey are ready immediately (see [architecture.md](architecture.md#startup)).
- **Software rendering.** For this small, mostly static UI, WPF's hardware path (D3D device) cost
  ~90 MB of private memory on the test machine; `RenderMode.SoftwareOnly` brought the whole app to
  ~35 MB while warm opening stayed below 10 ms.

  | Rendering | Private bytes after first show |
  |---|---|
  | Hardware (default) | ~120–127 MB |
  | Software | ~36–43 MB |

- **No single-file compression.** Compressed bundles inflate assemblies into private memory:
  +70 MB measured at startup.

  | Bundle | EXE | Private bytes at startup |
  |---|---|---|
  | Uncompressed | 141 MB | ~16 MB |
  | Compressed | 64 MB | ~88 MB |

- **Pre-built popup.** The window is created and rendered once (invisibly, DWM-cloaked) and reused.
- **In-memory search over the first 4,096 characters** of each entry; the full text is loaded from
  SQLite only when an entry is copied.
- **Load cost scales with the number of entries, not their size**: the text and image columns are
  the last columns and are not read when building the index. A picture adds only its thumbnail
  (a few KB) to the index; the picture itself is read only for the preview or to copy it.
- **Pictures cost nothing while idle.** They are decoded after the clipboard is released, on the
  reader thread; after a large picture the app collects its imaging buffers at once, so private
  bytes return to the idle level. Thumbnails are decoded only for rows that are shown, the preview
  decodes at most 1,280 px on the longer side and one picture at a time.
- **Bounded data**: 256K characters per entry, 20 million characters for all unpinned texts;
  pictures up to 25 megapixels and 32 MB as PNG, 128 MB for all unpinned pictures; 1 MB log with
  one backup.

## Reproducing

```powershell
dotnet publish src/ClipboardManager -c Release -r win-x64 -o publish
$env:CLIPBOARDMANAGER_SELFTEST_OUT = "$PWD\selftest.txt"
Start-Process publish\ClipboardManager.exe '--selftest','--idle','120' -Wait
Get-Content selftest.txt
dotnet run --project tools/AssetGen -c Release -- bench
```

The self-test uses a temporary data folder, keeps every window cloaked and does not touch the
clipboard (unless `--selftest-clipboard` is passed).

## Still to measure

Multi-hour soak tests (100,000 captures, thousands of popup cycles, Explorer restarts), hybrid-CPU
laptops on battery and multi-monitor setups with mixed DPI are tracked in
[manual-test-checklist.md](manual-test-checklist.md).
