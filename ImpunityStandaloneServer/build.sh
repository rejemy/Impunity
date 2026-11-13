#!/bin/bash

echo "Building Impunity Standalone Server"
dotnet build -c Release

mkdir -p ../bin/StandaloneServer
cp bin/Release/net10.0/ImpunityStandaloneServer ../bin/StandaloneServer
cp bin/Release/net10.0/ImpunityStandaloneServer.deps.json ../bin/StandaloneServer
cp bin/Release/net10.0/ImpunityStandaloneServer.dll ../bin/StandaloneServer
cp bin/Release/net10.0/ImpunityStandaloneServer.runtimeconfig.json ../bin/StandaloneServer
cp bin/Release/net10.0/UltraLiteDB.dll ../bin/StandaloneServer
# Native libraries for the WebRTC transport (the server runs without WebRTC if its platform has none).
if [ -d bin/Release/net10.0/runtimes ]; then
	rm -rf ../bin/StandaloneServer/runtimes
	cp -R bin/Release/net10.0/runtimes ../bin/StandaloneServer/
fi
