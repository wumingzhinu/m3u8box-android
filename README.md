# M3U8Box for Android

An Android app that downloads and decrypts HLS/M3U8 streams, powered by
[N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) as the download/decrypt
engine.

## Status

**Feasibility verification stage.** The repository contains a compile probe that
answers one question:

> Can the N_m3u8DL-RE engine be consumed as a library and packaged for
> `net9.0-android`?

Answered: **yes.** The engine compiles, is reachable from the app assembly, and
produces a signed arm64 APK. See "Findings" for how, and for the two things that
do not work.

## App status

`src/M3U8Box.App` is a working single-screen app: enter an m3u8 URL plus request
headers, tap 解析画质 to list tracks, pick one, tap 开始下载. Output lands in the
app's external files dir under `downloads/`.

The `verify/Probe` project still has no UI by design. It exists to guard the
upstream rewrite, not to be used.

Not yet implemented: task queue, history persistence, foreground service, and
export to the shared MediaStore. Downloads run in the Activity, so rotating the
screen or backgrounding the app can interrupt them.

## Platform notes for the app

- **No ffmpeg.** `BinaryMerge` is forced, which concatenates already-decrypted
  segments. `MuxAfterDone` would shell out to ffmpeg and is never enabled.
- **No native AOT.** ILCompiler has no Android target on the .NET 9 band, so the
  Mono runtime ships in the APK. This is most of the package size.
- **Logging.** The engine logs through Spectre.Console, which needs a terminal.
  `Core/AndroidLog.cs` bridges it to logcat; without that the first download
  would fail at runtime while building perfectly.

## Layout

```
.nre-pin                      pinned upstream commit of N_m3u8DL-RE
Directory.Build.props         forces net9.0 over upstream's net10.0
verify/                       the minimal compile probe
.github/workflows/            CI that runs the probe
```

## How the upstream is consumed

Upstream is **not** vendored. The workflow clones it at the commit recorded in
`.nre-pin` into `third_party/N_m3u8DL-RE`, then `Directory.Build.props`
overrides its `TargetFramework` down to `net9.0`. Keeping it as a clone instead
of a submodule avoids the shallow-clone `fetch-pack` failures seen on Termux and
keeps the pin explicit and reviewable.

The override is the load-bearing part. Upstream targets `net10.0`; .NET for
Android is only reliable through `net9.0-android`. `N_m3u8DL-RE.Common` and
`N_m3u8DL-RE.Parser` are pure logic with no ffmpeg dependency, so they are
expected to retarget cleanly.

## Findings

Established by the probe, in the order they were hit:

1. **Upstream must be retargeted.** It ships `net10.0`; .NET for Android is
   dependable through `net9.0-android`. CI rewrites the two known TFM lines
   with `sed`, which is authoritative. A `Directory.Build.props` condition was
   tried first and silently did nothing, because a mistyped MSBuild condition
   is indistinguishable from a passing one.
2. **Native AOT is unavailable for Android on the .NET 9 band.** With
   `PublishAot=true`, ILCompiler aborts with
   `CommandLineException: Target OS 'android' is not supported`. There is no
   configuration that avoids this, so the app ships the shared Mono runtime
   (`AndroidLinkMode=None`), costing roughly 20-40 MB. Revisit on a later band.
3. **SDK, android workload, and ILCompiler must share a band.** A 10.x SDK with
   a 9.x ILCompiler produced a confusing failure. `global.json` pins it.
4. **`AndroidApplication=true` is mandatory**, not optional. Only the Android
   application targets populate `PrivateSdkAssemblies`, and without them
   ILCompiler fails with a message that does not mention the real cause.
5. **The engine is an `Exe` full of `internal` types**, so it cannot be
   referenced as shipped. `scripts/prepare-upstream.sh` rewrites it: Library
   output type, System.CommandLine severed, CLI front end deleted, and
   accessibility widened. Two consequences worth knowing:
   - Promoting `MyOption` exposed its property types (CS0053), and promoting
     those exposed *their* types, one build at a time. All top-level engine
     types are now promoted in a single pass, because enumerating them by hand
     is a losing game that depends on the pinned commit.
   - `SimpleDownloadManager` is split across two files; both partials must be
     promoted or CS0262.
   - `MyOption` carries ~70 `<see cref="CommandInvoker.X"/>` doc comments that
     dangle once the CLI is deleted, and are rewritten to self-references.

Remaining risks, not yet tested:

1. **`Spectre.Console` 0.57.1** — used by `N_m3u8DL-RE.Common.Log` for ANSI
   console rendering. Android has no terminal. Rendering should degrade rather
   than crash, but upstream routes *all* logging through it, so a logging
   redesign is likely needed.
2. **`NetworkInterfaceBinding.cs:53`** throws `PlatformNotSupportedException`
   on platforms that are not Windows/Linux/macOS. Android hits this only when a
   network interface is explicitly requested.
3. **Muxing needs ffmpeg** — `--enableBinaryMerge` avoids it, but proper
   audio/video track merging does not. Either bundle ffmpeg (tens of MB) or
   reimplement with `MediaMuxer`.
4. **`System.CommandLine`** is only used by the upstream `Exe` project, which
   this app does not reference. `Common` + `Parser` are the only projects used.

## License

N_m3u8DL-RE is MIT. This repository's own code has the same license; see
`LICENSE`.
