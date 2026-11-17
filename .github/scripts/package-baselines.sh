#!/usr/bin/env bash
# Looks up, for each package id on stdin, the version API compatibility is checked against.
#
# Usage: package-baselines.sh <packing-version> <output-props> <source-root> < ids
#
# The baseline of a package is the highest stable version on nuget.org whose major.minor.patch is
# numerically below the packing version. That holds whether the packing version is a prerelease or
# a release, so a re-pack of a published version is compared with its predecessor, and 2.0.0 with
# the last 1.x.
#
# An id with no stable version at all (HTTP 404, or only prereleases) is a new package: it is
# listed as RagNetUnpublishedPackage and skipped. An id that has stable versions but none below
# the packing version fails, and so does any other HTTP failure. Treating those as "no baseline"
# would switch the check off without a trace.
#
# Needs only curl, grep and sed, so it runs in Git Bash on Windows as well. NUGET_FLAT_CONTAINER
# overrides the service root; the tests point it at a loopback server.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <packing-version> <output-props> <source-root> < ids" >&2
  exit 2
fi

packing_version=$1
output=$2
source_root=$3
flat_container=${NUGET_FLAT_CONTAINER:-https://api.nuget.org/v3-flatcontainer}

# Numeric major, minor and patch of the packing version; the prerelease label is ignored.
core=${packing_version%%[-+]*}
IFS=. read -r p_major p_minor p_patch _ <<< "$core"
p_major=$((10#${p_major:-0})); p_minor=$((10#${p_minor:-0})); p_patch=$((10#${p_patch:-0}))

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

# True when major.minor.patch of $1 is numerically below the packing version.
is_below() {
  local major minor patch
  IFS=. read -r major minor patch _ <<< "$1"
  major=$((10#${major:-0})); minor=$((10#${minor:-0})); patch=$((10#${patch:-0}))
  if [ "$major" -ne "$p_major" ]; then [ "$major" -lt "$p_major" ]; return; fi
  if [ "$minor" -ne "$p_minor" ]; then [ "$minor" -lt "$p_minor" ]; return; fi
  [ "$patch" -lt "$p_patch" ]
}

# True when $1 sorts above $2, compared per numeric field.
is_higher() {
  local a_major a_minor a_patch b_major b_minor b_patch
  IFS=. read -r a_major a_minor a_patch _ <<< "$1"
  IFS=. read -r b_major b_minor b_patch _ <<< "$2"
  a_major=$((10#${a_major:-0})); a_minor=$((10#${a_minor:-0})); a_patch=$((10#${a_patch:-0}))
  b_major=$((10#${b_major:-0})); b_minor=$((10#${b_minor:-0})); b_patch=$((10#${b_patch:-0}))
  if [ "$a_major" -ne "$b_major" ]; then [ "$a_major" -gt "$b_major" ]; return; fi
  if [ "$a_minor" -ne "$b_minor" ]; then [ "$a_minor" -gt "$b_minor" ]; return; fi
  [ "$a_patch" -gt "$b_patch" ]
}

groups=""
unpublished=""
majors=""

while IFS= read -r id || [ -n "$id" ]; do
  id=${id%$'\r'}
  [ -n "$id" ] || continue

  lower=$(printf '%s' "$id" | tr '[:upper:]' '[:lower:]')
  url="$flat_container/$lower/index.json"

  if ! code=$(curl -sS --connect-timeout 15 --max-time 60 --retry 3 --retry-delay 1 -o "$tmp" -w '%{http_code}' "$url"); then
    echo "::error::Could not reach $url while looking up the API baseline of $id."
    exit 1
  fi

  case "$code" in
    200) ;;
    404)
      echo "$id has no version on nuget.org; API compatibility is not checked for it."
      unpublished="$unpublished  <ItemGroup><RagNetUnpublishedPackage Include=\"$id\" /></ItemGroup>"$'\n'
      continue
      ;;
    *)
      echo "::error::nuget.org answered HTTP $code for $url, so the API baseline of $id is unknown."
      exit 1
      ;;
  esac

  # Only a well-formed index may reach the unpublished path below, and then only with an empty or
  # prerelease-only list. The whole body must be the nuget.org flat-container shape
  # {"versions":["1.0.0","2.0.0-beta"]}, so {} and an HTML error page fail, and so do a truncated
  # body, a non-string entry, a malformed version and trailing junk. Line breaks become spaces rather
  # than being deleted, and whitespace is allowed only between tokens, so a line break inside a
  # version string fails too. Each version is a semver version: three numbers, then optional
  # prerelease and build parts made of non-empty dot-separated identifiers. An HTTP 200 with any
  # other body says nothing about what is published, so it fails rather than switch off validation.
  # The raw bytes are checked first: the index is ASCII, and JSON allows only space, tab, CR and LF
  # as whitespace. Any other byte, such as NUL, which command substitution would silently drop, or a
  # vertical tab or form feed, fails before the shape is matched.
  if [ -n "$(LC_ALL=C tr -d ' -~\t\r\n' < "$tmp" | head -c 1 | od -An -c)" ]; then
    echo "::error::nuget.org answered HTTP 200 for $url with bytes a version index cannot contain, so the API baseline of $id is unknown."
    exit 1
  fi
  list=$(tr '\r\n\t' '   ' < "$tmp")
  ident='[0-9A-Za-z-]+'
  version="\"[0-9]+\\.[0-9]+\\.[0-9]+(-$ident(\\.$ident)*)?(\\+$ident(\\.$ident)*)?\""
  ws=' *'
  shape="^$ws\\{$ws\"versions\"$ws:$ws\\[$ws($version($ws,$ws$version)*)?$ws\\]$ws\\}$ws\$"
  if ! printf '%s\n' "$list" | grep -Eq "$shape"; then
    echo "::error::nuget.org answered HTTP 200 for $url, but the body is not a version list, so the API baseline of $id is unknown."
    exit 1
  fi

  stable=$(printf '%s' "$list" | grep -o '"[0-9][^"]*"' | tr -d '"' | grep -v -e '-' -e '+' || true)
  if [ -z "$stable" ]; then
    echo "$id has no stable version on nuget.org; API compatibility is not checked for it."
    unpublished="$unpublished  <ItemGroup><RagNetUnpublishedPackage Include=\"$id\" /></ItemGroup>"$'\n'
    continue
  fi

  baseline=""
  while IFS= read -r version; do
    if is_below "$version" && { [ -z "$baseline" ] || is_higher "$version" "$baseline"; }; then
      baseline=$version
    fi
  done <<< "$stable"

  if [ -z "$baseline" ]; then
    echo "::error::$id has stable versions on nuget.org but none below $packing_version, so there is no baseline to check it against."
    exit 1
  fi

  echo "$id: API baseline $baseline"
  groups="$groups  <PropertyGroup Condition=\"'\$(PackageId)' == '$id'\">"$'\n'
  groups="$groups    <PackageValidationBaselineVersion>$baseline</PackageValidationBaselineVersion>"$'\n'
  groups="$groups  </PropertyGroup>"$'\n'

  majors="$majors$id ${baseline%%.*}"$'\n'
done

if [ -z "$groups$unpublished" ]; then
  echo "::error::No package ids were read from stdin, so no API baseline was looked up."
  exit 1
fi

# "--" is not allowed inside an XML comment, so collapse every run of hyphens to one. The
# text ends in a period and the closing marker is preceded by a space, so a hyphen cannot touch it.
comment="Generated by .github/scripts/package-baselines.sh from $flat_container for packing version $packing_version. Do not edit."
while [ "$comment" != "${comment//--/-}" ]; do comment=${comment//--/-}; done
{
  echo '<?xml version="1.0" encoding="utf-8"?>'
  echo "<!-- $comment -->"
  echo '<Project>'
  printf '%s' "$groups"
  printf '%s' "$unpublished"
  echo '</Project>'
} > "$output"

# A suppression file for a major below its own package's baseline cannot match anything any more.
# Each project directory under the source root is named after its PackageId, so the directory name
# is the package id. A package is compared with its own baseline, not the highest of all: during a
# partial publish one package can still be on an older major whose file its hook still uses. A
# directory with no baseline gets no warning.
for file in "$source_root"/*/CompatibilitySuppressions.*.x.xml; do
  [ -e "$file" ] || continue
  name=${file##*/}
  n=${name#CompatibilitySuppressions.}
  n=${n%.x.xml}
  case "$n" in ''|*[!0-9]*) continue ;; esac
  dir=${file%/*}
  dir=${dir##*/}
  while read -r pkg pkg_major; do
    if [ "$pkg" = "$dir" ] && [ "$n" -lt "$pkg_major" ]; then
      echo "::warning::$file suppresses differences against the $n.x baseline of $dir, which is no longer used. Delete it."
    fi
  done <<< "$majors"
done
