#!/bin/bash

echo "Building Impunity Runtime dlls"
dotnet build -c Release
dotnet build -c Debug

echo "Copying Impunity Runtime dlls into bin directory"
mkdir -p ../bin/Release
cp bin/Release/netstandard2.1/Impunity.dll ../bin/Release
cp bin/Release/netstandard2.1/Impunity.pdb ../bin/Release
cp bin/Release/netstandard2.1/Impunity.xml ../bin/Release

mkdir -p ../bin/Debug
cp bin/Debug/netstandard2.1/Impunity.dll ../bin/Debug
cp bin/Debug/netstandard2.1/Impunity.pdb ../bin/Debug
cp bin/Debug/netstandard2.1/Impunity.xml ../bin/Debug
