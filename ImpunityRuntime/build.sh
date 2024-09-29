#!/bin/bash

echo "Building Impunity Runtime dlls"
dotnet build -c Release
dotnet build -c Debug

echo "Copying Impunity Runtime dlls into bin directory"
mkdir -p ../bin/Runtime/Release
cp bin/Release/netstandard2.1/Impunity.dll ../bin/Runtime/Release
cp bin/Release/netstandard2.1/Impunity.pdb ../bin/Runtime/Release
cp bin/Release/netstandard2.1/Impunity.xml ../bin/Runtime/Release

mkdir -p ../bin/Runtime/Debug
cp bin/Debug/netstandard2.1/Impunity.dll ../bin/Runtime/Debug
cp bin/Debug/netstandard2.1/Impunity.pdb ../bin/Runtime/Debug
cp bin/Debug/netstandard2.1/Impunity.xml ../bin/Runtime/Debug
