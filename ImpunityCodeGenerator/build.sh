#!/bin/bash
# Builds the Roslyn source generator and copies it into the Unity project.
#
# Rebuilding the dll always produces a byte-different binary (deterministic
# builds are off for source generators), so git sees Assets/Plugins/*.dll as
# dirty on every build even when nothing changed. To avoid that churn, the
# copied dll's mtime is stamped to match ImpunityCodeGenerator.cs, and a
# matching pair is treated as "already up to date". Pass --force to rebuild
# regardless.

cd "$(dirname "$0")" || exit 1

SOURCE=ImpunityCodeGenerator.cs
BUILT=bin/Release/netstandard2.0/ImpunityCodeGenerator.dll
PLUGIN=../ImpunityUnity/Assets/Plugins/ImpunityCodeGenerator.dll
BINCOPY=../bin/ImpunityCodeGenerator.dll

mtime() { stat -f %m "$1" 2>/dev/null || stat -c %Y "$1" 2>/dev/null; }

if [ "$1" != "--force" ] && [ -f "$PLUGIN" ] && [ "$(mtime "$SOURCE")" = "$(mtime "$PLUGIN")" ]; then
    echo "Impunity Code Generation dll is up to date (matches $SOURCE), skipping build."
    echo "Run './build.sh --force' to rebuild anyway."

    # Keep ../bin in sync even when we skip the build.
    mkdir -p ../bin
    if ! cmp -s "$PLUGIN" "$BINCOPY"; then
        cp "$PLUGIN" "$BINCOPY"
        touch -r "$SOURCE" "$BINCOPY"
    fi
    exit 0
fi

echo "Building Impunity Code Generation dll"
dotnet build -c Release || exit 1

echo "Copying Impunity Code Generation dll into test project"
cp "$BUILT" "$PLUGIN"
# Stamp the copy with the source's mtime so the next run can detect it's current.
touch -r "$SOURCE" "$PLUGIN"

echo "Copying Impunity Code Generation dll into bin directory"
mkdir -p ../bin
cp "$BUILT" "$BINCOPY"
touch -r "$SOURCE" "$BINCOPY"
