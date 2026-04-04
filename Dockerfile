#
#   LEAN Docker Container with ClickHouseDataProvider
#   Based on official QuantConnect LEAN image
#

FROM quantconnect/lean:latest

LABEL maintainer="mdiggory"
LABEL description="Custom LEAN engine with ClickHouseDataProvider for ClickHouse data integration"

# Copy custom ClickHouseDataProvider
COPY Engine/DataFeeds/ClickHouseDataProvider.cs /Lean/Engine/DataFeeds/

# Note: This is a runtime-only image. The ClickHouseDataProvider.cs is included
# for reference but would need to be compiled separately. For a production setup,
# you would build the entire LEAN solution with the custom provider included.

# For now, this image can be used with pre-compiled custom LEAN binaries
# mounted as volumes at runtime.

WORKDIR /Lean/Launcher/bin/Debug

ENTRYPOINT [ "dotnet", "QuantConnect.Lean.Launcher.dll" ]
