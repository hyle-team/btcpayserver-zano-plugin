#!/bin/sh
set -eu

dotnet test --project BTCPayServer.Plugins.IntegrationTests.csproj \
  -c "${CONFIGURATION_NAME}" --no-build --no-restore \
  --coverlet --coverlet-include "[BTCPayServer.Plugins.Zano]*" \
  --coverlet-output-format cobertura --results-directory /coverage/integration
