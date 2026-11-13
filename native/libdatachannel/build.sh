#!/bin/bash
# Builds the libdatachannel shared library the standalone server loads for its WebRTC transport, for the platform
# this script runs on, and puts it (with a manifest) under ImpunityStandaloneServer/runtimes/<rid>/native/.
#
# The result is one shared library with libjuice, usrsctp and Mbed TLS linked in statically, so the server needs
# nothing else installed. Sources are pinned by tag AND commit below; bump both together.
#
# Needs: git, cmake (3.21+), a C/C++ toolchain, python3. On Windows run it from Git Bash with Visual Studio installed.
# The official binaries come from .github/workflows/native-libdatachannel.yml, which runs this script on each platform.
#
#   ./build.sh            build for this machine
#   WORK_DIR=/tmp/x ./build.sh   keep sources and build trees somewhere else (default: ./.work, git-ignored)
set -euo pipefail

LIBDATACHANNEL_TAG=v0.24.6
LIBDATACHANNEL_COMMIT=6b1e2e620f1e37f0eafeee702eaea0043cb305fd
MBEDTLS_TAG=v3.6.7
MBEDTLS_COMMIT=068ff080b369adfac81509f9b57b2afabaf82dc5
MACOS_DEPLOYMENT_TARGET=12.0

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
WORK="${WORK_DIR:-$HERE/.work}"

case "$(uname -s)-$(uname -m)" in
	Darwin-arm64)             RID=osx-arm64;   LIB=libdatachannel.dylib ;;
	Linux-x86_64)             RID=linux-x64;   LIB=libdatachannel.so ;;
	Linux-aarch64)            RID=linux-arm64; LIB=libdatachannel.so ;;
	MINGW*-x86_64|MSYS*-x86_64) RID=win-x64;   LIB=datachannel.dll ;;
	*) echo "Unsupported platform: $(uname -s)-$(uname -m). The server runs without WebRTC here." >&2; exit 1 ;;
esac

OUT="$REPO/ImpunityStandaloneServer/runtimes/$RID/native"
echo "Building libdatachannel $LIBDATACHANNEL_TAG + Mbed TLS $MBEDTLS_TAG for $RID"

# Clones a tag and fails unless it is the commit we pinned: a moved tag must not change what we ship.
fetch() {
	local url=$1 tag=$2 commit=$3 dir=$4
	if [ ! -d "$dir/.git" ]; then
		git -c advice.detachedHead=false clone -q --depth 1 --branch "$tag" --recurse-submodules --shallow-submodules "$url" "$dir"
	fi
	local actual
	actual="$(git -C "$dir" rev-parse HEAD)"
	if [ "$actual" != "$commit" ]; then
		echo "$dir is at $actual, expected $commit ($tag)" >&2
		exit 1
	fi
}

mkdir -p "$WORK"
fetch https://github.com/Mbed-TLS/mbedtls.git "$MBEDTLS_TAG" "$MBEDTLS_COMMIT" "$WORK/mbedtls"
fetch https://github.com/paullouisageneau/libdatachannel.git "$LIBDATACHANNEL_TAG" "$LIBDATACHANNEL_COMMIT" "$WORK/libdatachannel"

COMMON=(-DCMAKE_BUILD_TYPE=Release -DCMAKE_POSITION_INDEPENDENT_CODE=ON)
LDC_EXTRA=()
case "$RID" in
	osx-arm64)
		COMMON+=(-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOS_DEPLOYMENT_TARGET)
		;;
	linux-*)
		# No dependency on the host's libstdc++: only glibc is left, and its floor is that of the build machine.
		# --no-undefined: a shared library may be linked with symbols left unresolved, which only shows up when it
		# is loaded. Make that a build error instead.
		LDC_EXTRA+=("-DCMAKE_SHARED_LINKER_FLAGS=-static-libstdc++ -static-libgcc -Wl,--no-undefined")
		# libdatachannel's find module puts the Mbed TLS archives on the link line as tls, crypto, x509. GNU ld reads
		# archives once, in order, so what x509 needs from crypto (mbedtls_pk_write_pubkey and friends) was never
		# pulled in. Naming crypto again at the very end of the line resolves it.
		LDC_EXTRA+=("-DCMAKE_CXX_STANDARD_LIBRARIES=$WORK/mbedtls-install-$RID/lib/libmbedcrypto.a")
		;;
	win-x64)
		# Static C runtime everywhere, so the server needs no Visual C++ redistributable.
		COMMON+=(-A x64 -DCMAKE_POLICY_DEFAULT_CMP0091=NEW "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded" -DMSVC_STATIC_RUNTIME=ON)
		;;
esac

JOBS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"

# --- Mbed TLS, static, nothing but the libraries built. libdatachannel's DTLS code refers to the DTLS-SRTP API even
# in a build without media, and Mbed TLS leaves that off by default. It is switched on in the source tree's config
# header (not on the compiler command line) so the installed headers libdatachannel compiles against agree with the
# library.
MBED_PREFIX="$WORK/mbedtls-install-$RID"
PYTHON="$(command -v python3 || command -v python)"
"$PYTHON" "$WORK/mbedtls/scripts/config.py" -f "$WORK/mbedtls/include/mbedtls/mbedtls_config.h" set MBEDTLS_SSL_DTLS_SRTP
cmake -S "$WORK/mbedtls" -B "$WORK/mbedtls-build-$RID" "${COMMON[@]}" \
	-DENABLE_TESTING=OFF -DENABLE_PROGRAMS=OFF -DUSE_STATIC_MBEDTLS_LIBRARY=ON -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
	-DMBEDTLS_FATAL_WARNINGS=OFF -DCMAKE_INSTALL_PREFIX="$MBED_PREFIX" -DCMAKE_INSTALL_LIBDIR=lib
cmake --build "$WORK/mbedtls-build-$RID" --config Release -j "$JOBS"
cmake --install "$WORK/mbedtls-build-$RID" --config Release

# --- libdatachannel: data channels only, libjuice for ICE, Mbed TLS for DTLS.
LDC_FLAGS=(-DNO_MEDIA=ON -DNO_WEBSOCKET=ON -DNO_EXAMPLES=ON -DNO_TESTS=ON -DUSE_NICE=OFF -DUSE_GNUTLS=OFF
	-DUSE_MBEDTLS=ON -DBUILD_SHARED_LIBS=ON -DBUILD_SHARED_DEPS_LIBS=OFF -DPREFER_SYSTEM_LIB=OFF)
cmake -S "$WORK/libdatachannel" -B "$WORK/libdatachannel-build-$RID" "${COMMON[@]}" "${LDC_FLAGS[@]}" \
	"-DCMAKE_PREFIX_PATH=$MBED_PREFIX" "-DMbedTLS_ROOT=$MBED_PREFIX" ${LDC_EXTRA[@]+"${LDC_EXTRA[@]}"}
cmake --build "$WORK/libdatachannel-build-$RID" --config Release -j "$JOBS" --target datachannel

# --- Collect, strip, and check what the result depends on.
mkdir -p "$OUT"
BUILD="$WORK/libdatachannel-build-$RID"
case "$RID" in
	osx-arm64)
		cp "$BUILD/libdatachannel.0.dylib" "$OUT/$LIB" 2>/dev/null || cp -L "$BUILD/libdatachannel.dylib" "$OUT/$LIB"
		strip -x "$OUT/$LIB"
		install_name_tool -id "@rpath/$LIB" "$OUT/$LIB"
		codesign --force --sign - "$OUT/$LIB"
		DEPS="$(otool -L "$OUT/$LIB" | tail -n +2 | awk '{print $1}' | grep -v "^@rpath/$LIB$" | sort | tr '\n' ' ')"
		BAD="$(echo "$DEPS" | tr ' ' '\n' | grep -v -E '^(/usr/lib/|/System/|$)' || true)"
		FLOOR="macOS $MACOS_DEPLOYMENT_TARGET"
		;;
	linux-*)
		cp -L "$BUILD/libdatachannel.so" "$OUT/$LIB"
		strip --strip-unneeded "$OUT/$LIB"
		DEPS="$(objdump -p "$OUT/$LIB" | awk '/NEEDED/ {print $2}' | sort | tr '\n' ' ')"
		BAD="$(echo "$DEPS" | tr ' ' '\n' | grep -v -E '^(libc\.so|libm\.so|libpthread\.so|libdl\.so|librt\.so|ld-linux|$)' || true)"
		FLOOR="$(objdump -T "$OUT/$LIB" | grep -o 'GLIBC_[0-9.]*' | sort -V | tail -1)"
		;;
	win-x64)
		cp "$BUILD/Release/$LIB" "$OUT/$LIB"
		DEPS="$(objdump -p "$OUT/$LIB" 2>/dev/null | awk '/DLL Name/ {print tolower($3)}' | sort | tr '\n' ' ' || true)"
		BAD="$(echo "$DEPS" | tr ' ' '\n' | grep -i -E '^(vcruntime|msvcp|ucrtbase|api-ms-win-crt|libssl|libcrypto)' || true)"
		FLOOR="Windows 10"
		;;
esac

if [ -n "${BAD:-}" ]; then
	echo "The built library depends on something a clean machine may not have:" >&2
	echo "$BAD" >&2
	exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
	SHA="$(sha256sum "$OUT/$LIB" | awk '{print $1}')"
else
	SHA="$(shasum -a 256 "$OUT/$LIB" | awk '{print $1}')"
fi

# The server checks this file before loading the library: the P/Invoke layer is written against one exact rtc.h.
cat > "$OUT/libdatachannel.manifest.json" <<EOF
{
  "rid": "$RID",
  "file": "$LIB",
  "sha256": "$SHA",
  "libdatachannel": { "tag": "$LIBDATACHANNEL_TAG", "commit": "$LIBDATACHANNEL_COMMIT" },
  "mbedtls": { "tag": "$MBEDTLS_TAG", "commit": "$MBEDTLS_COMMIT" },
  "flags": "${LDC_FLAGS[*]}",
  "dependencies": "$(echo "$DEPS" | sed 's/ *$//')",
  "floor": "${FLOOR:-}"
}
EOF

echo "Built $OUT/$LIB ($(wc -c < "$OUT/$LIB" | tr -d ' ') bytes), sha256 $SHA"
echo "Depends on: $DEPS"
