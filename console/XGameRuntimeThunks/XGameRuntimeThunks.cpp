// xgameruntime.thunks.dll for Xbox consoles.
//
// WHY THIS EXISTS
// ---------------
// GDK.Net P/Invokes every Gaming Runtime entry point from "xgameruntime.thunks.dll". Microsoft
// ships that redistributable in the GDK's `windows` tree only - there is no console build of it,
// because a C++ console title reaches the X* API by statically linking xgameruntime.lib, and until
// now nothing on Xbox needed the API as a *DLL*.
//
// This module is that missing redistributable. It uses exactly the mechanism upstream GDK.Net used
// for its own xgameruntime.extras.dll, the re-export shim it retired once GDK edition 260404 began
// exporting the entry points the shim existed to reach: statically link the GDK's xgameruntime.lib
// and re-export its stubs by name through a .def file.
// There is deliberately no wrapper code here - the linker resolves every name in the EXPORTS list
// straight out of the lib, so the exported functions ARE the GDK's own stubs, with the GDK's own
// signatures and calling convention. Nothing in this project can drift from the GDK headers, and
// no hand-written marshalling exists anywhere in the path.
//
// The .def is generated at build time from the installed GXDK's own xgameruntime.lib rather than
// checked in, so it cannot go stale against a GDK update: a symbol the lib does not have is never
// asked for, and a symbol the lib gains is picked up on the next build.
//
// This is a console-only project. On the desktop the real redistributable already exists and this
// must not be used - see docs/xbox-console-build.md.
//
// LINKAGE NOTES
// -------------
// Unlike the retired desktop shim, no advapi32 is needed: on console the registry-backed XSystem entry
// points resolve out of the Gaming.Xbox platform's own umbrella libraries.
//
// This translation unit is intentionally almost empty. The anchor symbol below just guarantees a
// non-empty object file for the linker.

extern "C" int XGameRuntimeThunksAnchor = 0;
