#!/usr/bin/env bash
# Requires a breaking pull request title when the pull request adds or changes an API
# compatibility suppression file.
#
# Usage: check-breaking-title.sh <pr-title> < files
#
# stdin carries one "<status><TAB><filename>" line per changed file, using the status values of
# the GitHub pulls/files API: added, modified, removed, renamed. A suppression file is any file
# named CompatibilitySuppressions.*.xml, in any directory. Adding, modifying or renaming one
# records a deliberate break, and release-please derives the version bump from the title, so the
# title must carry the "!" marker. Removing one drops a recorded break and needs nothing.
#
# The title is untrusted input. It is only ever expanded inside double quotes, never evaluated.
# Needs only bash, so it runs in Git Bash on Windows as well.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <pr-title> < files" >&2
  exit 2
fi

title=$1
breaking='^[a-z]+(\([^)]*\))?!: '

# stdin is always read to the end, even for a breaking title: leaving it unread would hand the
# producer a broken pipe, which fails the workflow's pipeline under pipefail.
offending=()
while IFS=$'\t' read -r status filename || [ -n "$status" ]; do
  case "$status" in
    added | modified | renamed) ;;
    *) continue ;;
  esac
  name=${filename##*/}
  if [[ "$name" == CompatibilitySuppressions.*.xml ]]; then
    offending+=("$filename")
  fi
done

if [ "${#offending[@]}" -eq 0 ] || [[ "$title" =~ $breaking ]]; then
  exit 0
fi

printf '::error::This pull request adds or changes an API compatibility suppression, which records a breaking change, so its title must be a breaking one such as "feat!: ..." or "fix(scope)!: ...". Files: %s\n' \
  "$(IFS=,; echo "${offending[*]}")"
exit 1
