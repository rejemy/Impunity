#!/bin/bash
set -e

cd "$(dirname "$0")"

# Runs the dotnet test suite (ImpunityTests). Builds the code generator, the non-Unity runtime, and
# the standalone server automatically via project references.
# Useful arguments (all passed through to `dotnet test`):
#   --filter "Category!=Slow"        skip the wall-clock reaper/migration-recovery tests
#   --filter "Category=Transport"    just the transport matrix (local/TCP/standalone/WebSocket)
dotnet test ImpunityTests/ImpunityTests.csproj "$@"
