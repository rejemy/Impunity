#!/bin/bash
set -e

echo "Building Impunity Runtime dlls (all three variants via the solution)"
dotnet build ImpunityRuntime.sln -c Release
dotnet build ImpunityRuntime.sln -c Debug

# Stage each variant's artifacts into its own folder under ../bin/Runtime/<Variant>/<Config>.
# Assembly name -> variant folder:
#   ImpunityRuntime           -> Standalone  (no Unity, no UNITY_WEBGL)
#   ImpunityUnityRuntime      -> Unity       (UnityEngine, non-WebGL branches)
#   ImpunityUnityWebGLRuntime -> UnityWebGL  (UnityEngine + UNITY_WEBGL; needs the jslib alongside)
stage_variant() {
  local assembly="$1"   # e.g. ImpunityRuntime
  local variant="$2"    # e.g. Standalone
  local config="$3"     # Release | Debug
  local src="bin/${config}/netstandard2.1"
  local dst="../bin/Runtime/${variant}/${config}"
  mkdir -p "${dst}"
  cp "${src}/${assembly}.dll" "${dst}/"
  cp "${src}/${assembly}.pdb" "${dst}/"
  cp "${src}/${assembly}.xml" "${dst}/"
}

echo "Copying Impunity Runtime dlls into bin directory"
for config in Release Debug; do
  stage_variant ImpunityRuntime           Standalone "${config}"
  stage_variant ImpunityUnityRuntime      Unity      "${config}"
  stage_variant ImpunityUnityWebGLRuntime UnityWebGL "${config}"
  # The WebGL WebSocket transport's [DllImport("__Internal")] bindings resolve to this jslib at Unity build time.
  cp ../ImpunityUnity/Assets/Plugins/ImpunityWebSocket.jslib "../bin/Runtime/UnityWebGL/${config}/"
done
