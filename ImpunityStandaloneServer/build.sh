#!/bin/bash

echo "Building Impunity Standalone Server"
dotnet build -c Release

mkdir -p ../bin/StandaloneServer
cp bin/Release/net8.0/ImpunityStandaloneServer ../bin/StandaloneServer
cp bin/Release/net8.0/ImpunityStandaloneServer.deps.json ../bin/StandaloneServer
cp bin/Release/net8.0/ImpunityStandaloneServer.dll ../bin/StandaloneServer
cp bin/Release/net8.0/ImpunityStandaloneServer.runtimeconfig.json ../bin/StandaloneServer
cp bin/Release/net8.0/UltraLiteDB.dll ../bin/StandaloneServer