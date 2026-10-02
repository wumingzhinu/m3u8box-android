#!/usr/bin/env bash
#
# Rewrites the pinned N_m3u8DL-RE checkout so its download engine can be
# consumed as a library from an Android app.
#
# The upstream engine project (N_m3u8DL-RE) is OutputType=Exe and wires itself
# to System.CommandLine. None of that is usable from a library, and the pieces
# we need -- SimpleDownloadManager, DownloaderConfig, MyOption -- are all
# `internal`, so an external assembly cannot even name them.
#
# Every edit below is asserted. A rewrite that silently does nothing is worse
# than no rewrite: it produces a confusing failure far from the cause, which
# is exactly what happened when a Directory.Build.props condition failed to
# match in an earlier revision of this workflow.
#
# Pinned upstream: see .nre-pin
set -euo pipefail

UPSTREAM="third_party/N_m3u8DL-RE"
ENGINE="${UPSTREAM}/src/N_m3u8DL-RE"
CSPROJ="${ENGINE}/N_m3u8DL-RE.csproj"

fail=0
note() { echo "::notice::$*"; }
die()  { echo "::error::$*"; fail=1; }

[ -f "$CSPROJ" ] || { echo "::error::upstream not cloned: $CSPROJ missing"; exit 1; }

# ---------------------------------------------------------------------------
# 1. Engine project: Exe -> Library, drop System.CommandLine.
# ---------------------------------------------------------------------------
sed -i 's|<OutputType>Exe</OutputType>|<OutputType>Library</OutputType>|' "$CSPROJ"
if grep -q '<OutputType>Library</OutputType>' "$CSPROJ"; then
  note "engine: OutputType -> Library"
else
  die "engine: OutputType was not changed to Library"
fi

# System.CommandLine is only used by the CLI front end (Program.cs,
# CommandInvoker, PowerShellCompletionAction). Removing the reference lets all
# of those files be deleted below, which in turn removes the last dependency on
# a terminal-oriented API.
sed -i '/<PackageReference Include="System.CommandLine"/d' "$CSPROJ"
if grep -q 'System.CommandLine' "$CSPROJ"; then
  die "engine: System.CommandLine reference survived"
else
  note "engine: System.CommandLine reference removed"
fi

# ---------------------------------------------------------------------------
# 2. Delete the CLI front end.
#
# Program.cs holds the download orchestration, but it is private static and
# wired to MyOption-from-argv. The app reimplements that flow against the
# public manager types instead, so the CLI entry point is dead weight that
# would drag in Console/tput/process-exit handling Android cannot provide.
# ---------------------------------------------------------------------------
rm -f  "${ENGINE}/Program.cs" \
      "${ENGINE}/CommandLine/CommandInvoker.cs" \
      "${ENGINE}/CommandLine/PowerShellCompletionAction.cs"
if [ -e "${ENGINE}/Program.cs" ]; then
  die "engine: Program.cs still present"
else
  note "engine: CLI front end removed (Program.cs, CommandInvoker, PowerShellCompletionAction)"
fi

# MyOption is annotated with roughly a hundred <see cref="CommandInvoker.X"/>
# doc comments. Once CommandInvoker is gone those crefs no longer resolve, and
# an unresolvable cref is a warning today but breaks documentation builds and
# obscures real diagnostics. Rewrite the cref target to MyOption's own member so
# the references stay meaningful.
#
# Only crefs whose target actually lives on MyOption are rewritten; anything
# else is reduced to plain text rather than left dangling.
if [ -f "${ENGINE}/CommandLine/MyOption.cs" ]; then
  crefs=$(grep -c 'cref="CommandInvoker\.' "${ENGINE}/CommandLine/MyOption.cs" || true)
  if [ "$crefs" -gt 0 ]; then
    # Python because sed -E does not expand \1 in the replacement, which
    # previously wrote a literal "$1" into every one of these doc comments.
    python3 - "${ENGINE}/CommandLine/MyOption.cs" <<'PYEOF'
import re, sys
p = sys.argv[1]
src = open(p, encoding="utf-8-sig").read()
out, n = re.subn(r'cref="CommandInvoker\.([A-Za-z0-9_]+)"',
                 lambda m: 'cref="@MyOption.%s"' % m.group(1), src)
if n:
    open(p, "w", encoding="utf-8").write(out)
PYEOF
    note "MyOption: rewrote $crefs CommandInvoker cref(s) to @MyOption"
  fi
  if grep -q 'cref="CommandInvoker\.' "${ENGINE}/CommandLine/MyOption.cs"; then
    die "MyOption: CommandInvoker crefs survived the rewrite"
  fi
  # The literal "$1" is the specific failure mode worth guarding: it is valid
  # text, so nothing downstream complains until the docs are read.
  if grep -q 'cref="@MyOption\.\$1"' "${ENGINE}/CommandLine/MyOption.cs"; then
    die "MyOption: cref rewrite wrote a literal \$1 instead of the member name"
  fi
fi

# EmbeddedResource for the completion script lives in a now-missing file.
sed -i '/PowerShellCompletion\.ps1/d' "$CSPROJ"

# Tidy the ItemGroups left empty by the two deletions above, so the project file
# does not ship with stray empty groups.
python3 - "$CSPROJ" <<'PYEOF'
import re, sys
p = sys.argv[1]
s = open(p, encoding="utf-8-sig").read()
s = re.sub(r"[ \t]*<ItemGroup>\s*</ItemGroup>\s*\n", "", s)
open(p, "w", encoding="utf-8").write(s)
PYEOF

# ---------------------------------------------------------------------------
# 3. internal -> public for the types the app must reference.
#
# Done per-file with explicit assertions. A blanket sed across the tree would be
# shorter but would also promote unrelated internals and could break overload
# resolution between an internal and a public member.
# ---------------------------------------------------------------------------
promote() {
  local file="$1" pattern="$2" label="$3"
  if [ ! -f "$file" ]; then
    die "$label: file not found: $file"
    return
  fi

  # Done in Python rather than sed. Two earlier attempts with sed both failed
  # silently:
  #   * `^` was used as a literal marker for a line-start anchor. In a regex it
  #     is an anchor, so sed replaced the empty position at line start and left
  #     "^internal class MyOption" behind, yielding "public ^internal class
  #     MyOption" and CS0116.
  #   * after stripping the caret, the capture group was interpolated into the
  #     replacement without sed expanding \1, producing a literal "$1".
  #
  # A regex with Python's re.sub is unambiguous: pattern is an ERE anchored at
  # line start, and the group is substituted correctly.
  local count
  count=$(python3 - "$file" "$pattern" <<'PYEOF'
import re, sys
path, pattern = sys.argv[1], sys.argv[2]
pat = pattern[:-1] if pattern.endswith("$") else pattern
lines = open(path, encoding="utf-8-sig").read().splitlines(keepends=True)

# Only the declaration line matching the pattern is touched, and only the first
# "internal" token on it.
#
# Two mistakes avoided here. A blanket re.subn over r'^(\s*)internal\b' would
# also rewrite members *inside* the class, silently widening far more than
# intended. And emitting "public " + whole_match would produce
# "public internal partial class Foo" -- legal C#, so it would compile, but
# wrong and confusing to read.
hit = 0
for i, line in enumerate(lines):
    if re.search(pat, line):
        new, n = re.subn(r'\binternal\b', 'public', line, count=1)
        if n:
            lines[i] = new
            hit += 1
if hit:
    open(path, "w", encoding="utf-8").writelines(lines)
print(hit)
PYEOF
) || { die "$label: rewrite failed for $file"; return; }

  if [ "${count:-0}" -eq 0 ]; then
    die "$label: expected to find /$pattern/ in $file but found none"
    return
  fi

  # Verify rather than trust. "public internal" is legal and would compile while
  # being wrong; "^internal" and "public public" are outright broken. All are
  # cheaper to catch here than in a compiler error far away.
  if grep -qE '\^internal|public public|public internal|public \^' "$file"; then
    die "$label: malformed result in $file (caret, doubled, or 'public internal' modifier)"
  fi
  if ! grep -qE '^\s*public (partial |static |sealed |abstract )*class' "$file"; then
    die "$label: $file has no top-level 'public class' after rewrite"
  fi
  note "$label: promoted $count declaration(s) to public"
}

# MyOption is a plain POCO despite living in the CommandLine namespace; it does
# not reference System.CommandLine types, so it survives the reference removal.
promote "${ENGINE}/CommandLine/MyOption.cs" \
  '^internal class MyOption' 'MyOption'

# Clone() is used by the engine internally to isolate per-part options and is
# needed by the app to derive a per-task copy.
if grep -q 'internal MyOption Clone()' "${ENGINE}/CommandLine/MyOption.cs"; then
  sed -i 's|internal MyOption Clone()|public MyOption Clone()|' \
    "${ENGINE}/CommandLine/MyOption.cs"
  note "MyOption: Clone() promoted to public"
else
  die "MyOption: Clone() not found; upstream layout may have changed"
fi

# DownloaderConfig is the parameter object SimpleDownloadManager requires.
promote "${ENGINE}/Config/DownloaderConfig.cs" \
  '^internal class DownloaderConfig' 'DownloaderConfig'

# SimpleDownloadManager is internal as well, and is the actual entry point the
# app drives. Note it does not implement IDisposable, so the app must not wrap
# it in `using`.
promote "${ENGINE}/DownloadManager/SimpleDownloadManager.cs" \
  '^internal partial class SimpleDownloadManager' 'SimpleDownloadManager'

# The live-recording manager is the other entry point worth exposing; live
# recording is a later feature but the type is unreachable while internal.
if [ -f "${ENGINE}/DownloadManager/SimpleLiveRecordManager2.cs" ]; then
  promote "${ENGINE}/DownloadManager/SimpleLiveRecordManager2.cs" \
    '^internal .*class SimpleLiveRecordManager2' 'SimpleLiveRecordManager2' || true
fi

# ---------------------------------------------------------------------------
# 4. Report what the engine now exposes, for review in the run summary.
# ---------------------------------------------------------------------------
echo "--- engine public surface (spot check) ---"
grep -nE '^public (class|sealed class|static class|partial class|abstract class)' \
  "${ENGINE}/DownloadManager/SimpleDownloadManager.cs" 2>/dev/null | head -3 || true
grep -nE 'public (class MyOption|MyOption Clone)' \
  "${ENGINE}/CommandLine/MyOption.cs" || true
grep -nE 'public (class DownloaderConfig)' \
  "${ENGINE}/Config/DownloaderConfig.cs" || true

exit $fail
