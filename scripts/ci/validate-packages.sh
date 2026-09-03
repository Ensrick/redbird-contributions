#!/usr/bin/env bash
set -euo pipefail

version="${1:?usage: validate-packages.sh VERSION [DIRECTORY]}"
directory="${2:-artifacts/packages}"
expected=(
  RedBird
  RedBird.Abstractions
  RedBird.Core
  RedBird.X64
  RedBird.X86
  RedBird.Backends.NativeX64
  RedBird.Backends.NativeX86
  RedBird.Backends.PolyHook2
  RedBird.Backends.MinHook
  RedBird.Extensions.CallingConventions
)

if ! command -v unzip >/dev/null 2>&1; then
  echo 'Package validation requires unzip.' >&2
  exit 1
fi

list_entries() { unzip -Z1 "$1"; }

for id in "${expected[@]}"; do
  package="$directory/$id.$version.nupkg"
  symbols="$directory/$id.$version.snupkg"
  test -f "$package" || { echo "Missing package: $package" >&2; exit 1; }
  test -f "$symbols" || { echo "Missing symbols: $symbols" >&2; exit 1; }
  entries="$(list_entries "$package")"
  grep -qx 'README.md' <<<"$entries" || { echo "$package has no README.md" >&2; exit 1; }
  grep -qx 'LICENSE.txt' <<<"$entries" || { echo "$package has no LICENSE.txt" >&2; exit 1; }
  grep -qx 'logo.png' <<<"$entries" || { echo "$package has no logo.png" >&2; exit 1; }

  nuspec="$(unzip -p "$package" '*.nuspec')"
  grep -Fq "<id>$id</id>" <<<"$nuspec" || { echo "$package has the wrong package ID." >&2; exit 1; }
  grep -Fq "<version>$version</version>" <<<"$nuspec" || { echo "$package has the wrong version." >&2; exit 1; }
  grep -Fq '<repository type="git"' <<<"$nuspec" || { echo "$package has no repository metadata." >&2; exit 1; }
done

core_package="$directory/RedBird.Core.$version.nupkg"
list_entries "$core_package" | grep -qx 'runtimes/linux-x64/native/libredbird_thread_patch.so' || {
  echo "$core_package has no Linux x64 thread-patch runtime asset." >&2
  exit 1
}

shopt -s nullglob
packages=("$directory"/*.nupkg)
if [[ ${#packages[@]} -ne ${#expected[@]} ]]; then
  echo "Expected ${#expected[@]} NuGet packages, found ${#packages[@]}." >&2
  exit 1
fi

symbols=("$directory"/*.snupkg)
if [[ ${#symbols[@]} -ne ${#expected[@]} ]]; then
  echo "Expected ${#expected[@]} symbol packages, found ${#symbols[@]}." >&2
  exit 1
fi

if find "$directory" -name '*Tests*.nupkg' -print -quit | grep -q .; then
  echo 'Test projects must not be packed.' >&2
  exit 1
fi

echo "Validated ${#expected[@]} NuGet packages for version $version."
