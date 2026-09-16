#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet restore src/TorPos.App/TorPos.App.csproj
dotnet build src/TorPos.App/TorPos.App.csproj -c Release
dotnet publish src/TorPos.App/TorPos.App.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64
echo "Hazır: publish/linux-x64/"
