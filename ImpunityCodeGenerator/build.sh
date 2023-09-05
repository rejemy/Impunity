#!/bin/bash

echo "Building Impunity Code Generation dll"
dotnet build -c Release

echo "Copying Impunity Code Generation dll into test project"
cp bin/Release/netstandard2.0/ImpunityCodeGenerator.dll ../ImpunityUnity/Assets/Plugins

echo "Copying Impunity Code Generation dll into bin directory"
mkdir -p ../bin
cp bin/Release/netstandard2.0/ImpunityCodeGenerator.dll ../bin
