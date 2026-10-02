# M3U8Box for Android

An Android app that downloads and decrypts HLS/M3U8 streams, powered by
[N_m3u8DL-RE](https://github.com/nilaoda/N_m3u8DL-RE) as the download/decrypt
engine.

## Status

**Feasibility verification stage.** The repository currently contains only the
minimal probe that answers one question:

> Can `N_m3u8DL-RE.Common` + `N_m3u8DL-RE.Parser` be compiled for
> `net9.0-android` (AOT-compatible)?

Nothing else is implemented yet. Do not expect a working APK from this tree.

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

## Known obstacles ahead

Recorded up front so the probe result is interpreted correctly:

1. **`net9.0-android` viability** — the probe answers this.
2. **`Spectre.Console` 0.57.1** — used by `N_m3u8DL-RE.Common.Log` for ANSI
   console rendering. Android has no terminal. Rendering fails rather than
   crashing, but AOT trimming behaviour is unverified.
3. **`NetworkInterfaceBinding.cs:53`** throws `PlatformNotSupportedException`
   on platforms that are not Windows/Linux/macOS. Android hits this only when a
   network interface is explicitly requested.
4. **Muxing needs ffmpeg** — `--enableBinaryMerge` avoids it, but proper
   audio/video track merging does not. Either bundle ffmpeg (tens of MB) or
   reimplement with `MediaMuxer`.
5. **`System.CommandLine`** is only used by the upstream `Exe` project, which
   this app does not reference. `Common` + `Parser` are the only projects used.

## License

N_m3u8DL-RE is MIT. This repository's own code has the same license; see
`LICENSE`.
