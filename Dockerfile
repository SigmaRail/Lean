#
#   LEAN Docker Container with ClickHouseDataProvider
#   Cross platform deployment for multiple brokerages
#

# Use base system
FROM quantconnect/lean:foundation

MAINTAINER mdiggory

LABEL description="Custom LEAN engine with ClickHouseDataProvider for ClickHouse data integration"

# Install debugpy and PyDevD for remote python debugging
RUN pip install --no-cache-dir ptvsd==4.3.2 debugpy~=1.6.7 pydevd-pycharm~=231.9225.15

# Install vsdbg for remote C# debugging in Visual Studio and Visual Studio Code
RUN wget https://aka.ms/getvsdbgsh -O - 2>/dev/null | /bin/sh /dev/stdin -v 17.10.20209.7 -l /root/vsdbg

# Create Data directory structure for LEAN
RUN mkdir -p /Data/equity/usa/daily && \
    mkdir -p /Data/symbol-properties && \
    echo "symbol,market,security-type,listing-date,delisting-date,strike-price,option-type,underlying-symbol,underlying-security-type,contract-multiplier,minimum-price-variation" > /Data/symbol-properties/symbol-properties-database.csv

# Copy market hours database
COPY ./Data/market-hours /Data/market-hours

# Copy pre-built binaries (must be built locally first)
COPY ./Launcher/bin/Release/ /Lean/Launcher/bin/Release/
COPY ./Optimizer.Launcher/bin/Release/ /Lean/Optimizer.Launcher/bin/Release/
COPY ./Report/bin/Release/ /Lean/Report/bin/Release/
COPY ./DownloaderDataProvider/bin/Release/ /Lean/DownloaderDataProvider/bin/Release/

# Can override with '-w'
WORKDIR /Lean/Launcher/bin/Release

ENTRYPOINT [ "dotnet", "QuantConnect.Lean.Launcher.dll" ]
