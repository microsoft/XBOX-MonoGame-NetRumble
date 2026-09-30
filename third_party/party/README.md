# PlayFab Party redistributable (x64)

The three binaries in this folder are the Microsoft PlayFab Party redistributable and its
dependencies. They are vendored for two reasons: so that `tools/NetRumble.PlatformSpike`
can exercise the real native ABI, which `PartyInteropChecks` does, and so that a clone of
this repository builds with nothing else beside it on disk.

| File | Version |
|---|---|
| `Party.dll` | 2.3.2604.02003 |
| `PlayFabCore.dll` | 2.0.2604.02003 |
| `libHttpClient.dll` | 2.0.2604.02003 |

## They have to be staged beside the executable

Party loads `PlayFabCore.dll` and `libHttpClient.dll` itself through the normal search
order, so all three have to sit next to the executable. Resolving `Party.dll` alone from
this folder leaves it to fault when it reaches for its siblings.

The `PartyRedistributable` item group in `NetRumble.PlatformSpike.csproj` does this, and it
matches the shipping layout anyway.

## Which target these are correct for

These are desktop builds, so they are correct for the Windows build and for the platform
spike. They will not load on XBOX Series X|S, where the package layout takes its native
dependencies from the installed GDK instead. See
[Building for XBOX Series X|S](../../docs/xbox-console-build.md).

## Not vendored here

`Microsoft.Xbox.Services.C.Thunks.dll`. The same project copies it out of an installed
Microsoft GDK.

## Upgrading

Replacing these with a newer Party release is a file copy plus a version bump in the table
above. The bindings are not this repository's to maintain. XBOX GDK.NET owns them and
pins them to the 2.3.x flat C API.

Check a version bump by running the platform spike, which names any entry point that stops
resolving.

## License

These binaries are not covered by this repository's MIT license. They ship under their own
Microsoft terms.
