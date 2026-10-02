# M3U8Box for Android

An Android app that downloads and decrypts HLS/M3U8 streams, powered by
[N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) as the download/decrypt
engine.

## Status

**Feasibility verification stage.** The repository contains only the minimal
probe that answers one question:

> Can `N_m3u8DL-RE.Common` + `N_m3u8DL-RE.Parser` be compiled and packaged for
> `net9.0-android`?

Answered: yes, once upstream is retargeted to `net9.0`. See "Findings" below for
what the probe established, including the one thing that does not work.

Nothing else is implemented yet. Do not expect a working app from this tree.

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
