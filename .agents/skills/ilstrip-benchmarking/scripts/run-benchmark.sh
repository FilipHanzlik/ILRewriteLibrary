#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../../../.." && pwd)"
BENCHMARK_PROJ="${REPO_ROOT}/Tests/Benchmarks/ILStrip/ILStrip/ILStrip.csproj"

CANDIDATE_PATH="${1:-}"

if [ -n "${CANDIDATE_PATH}" ] && [ -f "${CANDIDATE_PATH}" ]; then
  shift
  echo "Running ILStrip benchmarks comparing against candidate: ${CANDIDATE_PATH}"
  exec dotnet run --project "${BENCHMARK_PROJ}" -c Release --no-build -- --candidate "${CANDIDATE_PATH}" "$@"
else
  echo "No candidate DLL file specified as first argument; running with provided arguments."
  exec dotnet run --project "${BENCHMARK_PROJ}" -c Release --no-build -- "$@"
fi
