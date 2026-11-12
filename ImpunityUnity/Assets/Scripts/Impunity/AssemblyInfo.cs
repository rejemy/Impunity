// The shared test sources (Assets/Tests/Shared) are compiled twice: by the dotnet ImpunityTests project, which is
// granted internals by ImpunityRuntime.csproj's <InternalsVisibleTo>, and by Unity as the ImpunitySharedTests asmdef,
// which is granted them here. Editor only: no DLL build defines UNITY_EDITOR, so the shipped runtimes carry no grant.
#if UNITY_EDITOR
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ImpunitySharedTests")]
#endif
