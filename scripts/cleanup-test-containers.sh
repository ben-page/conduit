#!/bin/bash
# Cleans up orphaned Testcontainers containers.
# Ryuk (the Testcontainers reaper) is disabled for Podman compatibility,
# so containers from crashed/interrupted test runs must be cleaned manually.
#
# Usage: ./scripts/cleanup-test-containers.sh

set -euo pipefail

containers=$(podman ps -aq --filter "label=org.testcontainers=true" 2>/dev/null || true)

if [ -z "$containers" ]; then
    echo "No Testcontainers containers found."
    exit 0
fi

count=$(echo "$containers" | wc -l)
echo "Found $count Testcontainers container(s). Removing..."

echo "$containers" | xargs podman rm -f 2>/dev/null || true

echo "Done."
