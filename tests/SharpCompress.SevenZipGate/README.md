# SevenZip gate

Windows-only, BCL-only benchmark harness for WISH-318. Publish from the repository root with the SDK pinned in `global.json`:

```powershell
dotnet publish tests/SharpCompress.SevenZipGate -c Release -r win-x64 -p:PublishAot=true
```

The executable is `tests/SharpCompress.SevenZipGate/bin/Release/net10.0/win-x64/publish/SharpCompress.SevenZipGate.exe`.

```powershell
SharpCompress.SevenZipGate.exe smoke --root <empty-temporary-directory>
SharpCompress.SevenZipGate.exe prepare --root C:\7zSandbox
SharpCompress.SevenZipGate.exe run --root C:\7zSandbox
```

`prepare` reads the fixed source locations in the spec. It skips datasets with completed manifests. `smoke` requires an explicitly supplied empty directory, generates its own input, and runs all six engines at level 1 plus the default-properties baseline at level 0. It never reads the preparation sources. The caller removes the smoke directory after inspecting its results.

Options:

- `--7z <exe>`: defaults to `C:\Program Files\7-Zip\7z.exe`.
- `--pin-mask <mask>`: decimal or hexadecimal; defaults to `0x4`. Select a mask valid for the current machine.
- `--timeout-min <minutes>`: defaults to 30; smoke uses 2.
- `--dataset source,mixed,large-bin,large-iso`, `--engine <comma-list>`, `--level 0,1,5,9`: restrict the matrix. The parallel estimate runs only for source and mixed; the default-properties baseline uses level 0.
- `--rerun`: include tests already recorded with status `ok` or `timeout`.
- `--par-threads <n>`: concurrent files for the parallel estimate; defaults to the logical CPU count. Level 9
  at full width can exhaust 32 GiB of RAM.
- `--7z-mt-extra "<switches>"`: extra 7-Zip switches for the multi-threaded engines, e.g. `-mmemuse=p60` to cap
  7-Zip's memory at level 9. Recorded in the test's notes.

The vswhere-based native linker lookup needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on `PATH`
for the AOT publish.

Create `<root>/STOP` or press Ctrl+C to stop. Remove STOP before resuming. Output paths reject junctions and symlinks. Keep the root exclusively owned by the harness while it is running. All commands preserve a 30 GiB free-space reserve, including smoke. An existing nonempty `out` directory requires inspection before a run; the harness cleans its own output after each test.

`results/results.csv` is the resumable invariant-culture record. `results.md` includes per-dataset and gate comparison tables. `environment.txt` records probed LZMA dictionaries, runtime, hardware, and native settings. `run.log` records child command lines and output. Dictionary columns describe configured maxima; SharpCompress reduces them for each known input size. Native LZMA2/default settings are controlled by 7-Zip. The parallel estimate omits archive/header overhead and records verification as `n/a`. Native timeout progress is unavailable and remains zero; managed children report bytes read once per second.
`sc-lzma2-1t` runs immediately after `sc-lzma-1t`, using LZMA2 with the same probed dictionary and fast-byte settings. Both are single-threaded, pinned to `--pin-mask`, and their archives are verified with `7z t`. The Gate table reports `SC LZMA2 / 7z LZMA wall` alongside the existing comparisons.

For sc-lzma-1t, sc-lzma2-1t, and sc-lzma-par, level 1 uses LZMA fast parsing
and HC4, matching 7-Zip's -mx1 choices. Levels 5 and 9 use normal parsing and BT4.
The level 0 sc-enci-today baseline retains the default normal + BT4 settings.
Each level's mode and match finder are recorded in environment.txt, the CSV
match-finder column, and result notes. Use --rerun when comparing with older
results, whose managed level 1 used normal + BT4.