#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../../../.." && pwd)"

STRIPPER_PATH="${1:-${NEW_ILSTRIP_PATH:-}}"

if [ -n "${STRIPPER_PATH}" ]; then
  export NEW_ILSTRIP_PATH="${STRIPPER_PATH}"
  echo "Running ILStrip validation using stripper: ${NEW_ILSTRIP_PATH}"
else
  echo "No stripper path provided; defaulting to referent legacy oracle baseline."
fi

dotnet test "${REPO_ROOT}/Tests/ValidationTests/ValidationTests.sln" "${@:2}"
