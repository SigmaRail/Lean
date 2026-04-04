#
#   LEAN Docker Container with ClickHouseDataProvider
#   Custom build from source
#

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

WORKDIR /Lean

# Copy source code
COPY . .

# Build LEAN
RUN dotnet restore QuantConnect.Lean.sln && \
    dotnet build QuantConnect.Lean.sln -c Release

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:6.0

# Install Python for Python algorithms
RUN apt-get update && \
    apt-get install -y python3 python3-pip && \
    pip3 install --no-cache-dir pythonnet pandas numpy scipy scikit-learn && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /Lean

# Copy built binaries from build stage
COPY --from=build /Lean/Launcher/bin/Release/net6.0/ /Lean/Launcher/bin/Release/
COPY --from=build /Lean/Algorithm.Python/bin/Release/net6.0/ /Lean/Algorithm.Python/bin/Release/

# Set working directory
WORKDIR /Lean/Launcher/bin/Release

ENTRYPOINT [ "dotnet", "QuantConnect.Lean.Launcher.dll" ]
