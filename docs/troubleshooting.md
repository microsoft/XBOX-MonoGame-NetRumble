# Troubleshooting

Symptoms that came up while building this sample, with the cause and the fix. Most of them
apply to any C# game built against the Microsoft GDK, PlayFab and PlayFab Party rather than
to NetRumble specifically.

## Build and restore

### Restore fails with NU1603

**Symptom.** `dotnet build` fails during restore with `NU1603`, reported as an error rather
than a warning.

**Cause.** XBOX GDK.NET pins `Microsoft.NET.ILLink.Tasks` to the 8.0 feature band and builds
with `TreatWarningsAsErrors`. With a newer SDK installed and no live nuget.org feed,
restore resolves a later package than the pin asks for, raises `NU1603`, and that project
promotes it to an error.

**Fix.** Pass `/p:NoWarn=NU1603`.

```powershell
dotnet build NetRumble.slnx /p:NoWarn=NU1603
```

It has to be a **global** property. Suppressing it through `ProjectReference` metadata does
not work, because `NU1603` is raised while restore evaluates XBOX GDK.NET itself, and
XBOX GDK.NET does not inherit the `AdditionalProperties` of a project that references it.

`tools\Build-Gdkx.ps1` and `tools\Build-GdkPc.ps1` already pass it.

### Three MSB3030 errors about missing Party binaries

**Symptom.** The solution fails to build with three `MSB3030` errors naming `Party.dll`,
`PlayFabCore.dll` and `libHttpClient.dll`.

**Cause.** A copy step is pointing at a path outside the repository.

**Fix.** Those three binaries are vendored under `third_party/party/` precisely so a
standalone clone builds. Check that the `PartyRedistributable` item group resolves to that
folder. See [the vendored binaries README](../third_party/party/README.md).

### `dotnet build` fails with MSB4278 on a .vcxproj

**Symptom.** `MSB4278` when a build reaches a C++ project.

**Cause.** `dotnet build` cannot build `.vcxproj` files.

**Fix.** Use the build scripts, which shell out to the Visual Studio copy of MSBuild.

## Runtime and platform

### `XGameGetXboxTitleId` returns ERROR_NOT_FOUND

**Symptom.** `XGameGetXboxTitleId` fails with `0x80070490`, which is `ERROR_NOT_FOUND`, and
every XBOX services check behind it fails too.

**Cause.** An unpackaged GDK process reads its title identity from `MicrosoftGame.config`
in the executable's own directory. Having the file under `packaging\` is not enough,
because that copy only reaches a package layout.

**Fix.** Stage `packaging\MicrosoftGame.config` beside the executable. The platform spike's
project file does this already.

### E_GAMERUNTIME_NOT_INITIALIZED or a hard crash inside Party

**Symptom.** `0x89240100`, which is `E_GAMERUNTIME_NOT_INITIALIZED`. Party can also
terminate the process with an access violation instead of returning an error a `try`/`catch`
could see.

**Cause.** `PFInitialize`, `PartyInitialize` and the binaries behind them require
`XGameRuntimeInitialize` first. Called too early, native code dereferences runtime state
that does not exist yet.

**Fix.** Initialize in the order Gaming Runtime, then PlayFab, then Party. In this sample
`GameRuntimeHost` owns that lifetime and is idempotent, so any entry point can ask for
initialization without tracking who got there first.

This is also why a packaged title can exit immediately on launch with no managed exception.
Anything that calls a GDK entry point from a constructor runs before initialization.

### GDK entry points cannot be resolved with `DllImport`

**Symptom.** `[LibraryImport("XGameRuntime.dll")]` cannot resolve `XGameRuntimeInitialize`,
`XUserAddAsync`, `XTaskQueueCreate` or any other documented GDK entry point.

**Cause.** `XGameRuntime.dll` is the runtime host that ships with Windows. It exports only
a small private surface. The documented flat API is resolved through the static library
`xgameruntime.lib` when that library is linked into a native module, so those names have
no DLL export anywhere.

**Fix.** Use XBOX GDK.NET, or a native shim that links the static library and re-exports
what you need. The general lesson: probe for an export you actually call rather than for a
file name, and when a native SDK ships a `.lib` beside its headers, check whether the API
lives in the library before writing a single `DllImport`.

### PlayFab's native SDK cannot complete async calls from managed code

**Symptom.** The native PlayFab DLLs appear to export the functions you want, but managed
code cannot complete the async operations they return.

**Cause.** `PlayFabCore.dll`, `PlayFabServices.dll`, `PlayFabMultiplayer.dll`,
`libHttpClient.dll` and `Party.dll` export no `XAsync*` or `XTaskQueue*` symbols, so there
is no way to drive their completion from managed code. A task queue created through the GDK
is not interchangeable with the desktop PlayFab implementation either.

**Fix.** Call PlayFab's documented HTTPS JSON API from plain C#. This sample's
`PlayFabRestClient` handles URLs, token headers, envelopes, error mapping and response
parsing with no GDK or native dependency.

### Settings do not persist in a packaged build

**Symptom.** Saving throws `UnauthorizedAccessException` on every write once the title is
packaged, while the same code works unpackaged.

**Cause.** A packaged title's install folder is read-only.

**Fix.** Write to persistent local storage instead, and declare it in the game
configuration. Keep a writable fallback so a refusal degrades rather than crashes.

### A packaged build fails after a change that works unpackaged

Check reflection first. NativeAOT removes anything it cannot see a static path to, and
reflection-based `System.Text.Json` is the most common offender. See
[Reflection-based JSON fails under NativeAOT](#reflection-based-json-fails-under-nativeaot).

## Packaging and install

### Install times out with 0x800705b4

**Symptom.**

```text
Starting installation. See the Microsoft Store app for further details.
Timed out waiting for a response from the Store. See the Microsoft Store app for more
information on this package installation.
Failed with 0x800705b4
```

**Cause.** `0x800705b4` is `ERROR_TIMEOUT`. `wdapp install` does not install anything
itself. It hands the package to the Microsoft Store's install service and waits, so the
timeout reports the Store service failing to answer inside that window rather than a
verdict on the package. A package that installs in seconds on one machine and times out on
another is the expected shape of this problem.

**Fix.** Work through these in order. Each step is cheap, and the first two resolve most
occurrences.

1. **Check the Store app.** Open the Microsoft Store, go to Library, then Downloads and
   updates, and let any queued download finish. A Store busy servicing another package will
   not answer in time. The title's own install often appears there and completes on its own
   after the timeout was reported.
2. **Retry.** The timeout aborts the wait, not the install. Run `wdapp list` first. If the
   package is already registered it landed and there is nothing to do. Otherwise run the
   same `wdapp install` again.
3. **Restart the install services**, then retry.
   ```powershell
   Get-Service AppXSvc, ClipSVC, InstallService | Restart-Service -Force
   ```
   `InstallService` is the one that answers `wdapp`. The other two do the registration.
4. **Remove a half-installed copy.** A previous attempt can leave a partial registration
   that blocks the next one without showing up in `wdapp list`.
   ```powershell
   Get-AppxPackage *NetRumble* | Remove-AppxPackage
   ```
5. **Re-check sandbox, Developer Mode and account.** All three have to agree, and the
   sandbox switch has to have completed. Confirm the current sandbox with your GDK tooling,
   confirm Developer Mode is on under Settings, System, For developers, and confirm that
   the account signed into the **Store**, not only into Windows, is the one entitled to the
   sandbox.
6. **Re-download the package.** A truncated or partially copied package is rejected late,
   after the same wait. Compare the file size against a build that installed successfully
   elsewhere.
7. **Reboot, then retry.** This clears a wedged install service that a service restart did
   not.

If none of that works, the Store's own log is the only remaining source of detail. Look in
Event Viewer under Applications and Services Logs, Microsoft, Windows,
`AppXDeploymentServer/Operational`, and include the failing entry when you open an issue.
`0x800705b4` on its own carries no information about the cause.

This is an environment failure rather than a packaging defect. The same package installs
normally elsewhere.

### The package is far larger than the layout

**Symptom.** A package many times the size of the files that are supposed to be in it.

**Cause.** Something is building into a directory underneath the layout's output directory,
and the packaging step stages it recursively. A previous package left inside the layout
causes the same thing, so each build includes every earlier build.

**Fix.** Build intermediate outputs outside the layout tree, and delete any previous
package directory before generating a new layout map.

## Content and rendering

### Some characters render as question marks

**Symptom.** Characters such as U+2026 (horizontal ellipsis) and U+2014 (em dash) render
as a literal `?`.

**Cause.** `SpriteFont` silently substitutes `DefaultCharacter` for any character the font
does not declare. The text source can be perfectly correct while the compiled `.xnb` is
not.

**Fix.** Declare every character you use in the `.spritefont` `CharacterRegions`. Then
verify against the compiled `.xnb`, because that is the artifact the game actually loads.

If you parse `.spritefont` XML yourself, load it with `LoadOptions.PreserveWhitespace`.
`XDocument.Load` discards whitespace-only text nodes, so a region beginning with
`<Start>&#32;</Start>` otherwise reads as an empty string.

### A custom DX12 effect renders nothing

**Symptom.** Objects drawn with a custom effect are absent from every frame.

**Causes and fixes.**

- `vs_3_0` and `ps_3_0` produce `"Invalid DirectX 12 vertex profile"`. Use Shader Model 6
  with DXC syntax: `Texture2D`, `SamplerState`, `register(t0)`, `register(s0)`. The legacy
  `sampler2D`, `tex2D` and `sampler_state` forms are invalid on this path.
- A custom vertex shader replaces `SpriteBatch`'s internal effect, so `MatrixTransform` is
  no longer set for you. Unset, it is a zero matrix and everything collapses. Set it
  explicitly, including the y-down orthographic projection.
- `.fx` uniform initializers are discarded with `"Initializer of external global will be
  ignored"`. Set every uniform from code.

Note also that `SpriteBatch` effects are per batch rather than per object, so objects that
need a custom effect have to be drawn in their own pass. That changes draw ordering.

## Threading and async

### "Collection was modified" from transport code

**Symptom.** An intermittent `InvalidOperationException` from transport code, reproducing
on some runs and not others.

**Cause.** MonoGame installs no `SynchronizationContext`, so `await` continuations resume
on a thread-pool thread even when they go on to touch screens or transport state. Two
threads then walk the same collection.

**Fix.** Install a synchronization context that queues to the game thread and drain it at
the start of `Update`. This sample uses `GameThreadContext`. Reserve `ConfigureAwait(false)`
for platform work that is deliberately staying off the game thread.

### Reflection-based JSON fails under NativeAOT

**Symptom.** `IL3053` or `IL3050` at compile time, and a throw at runtime in a published
build.

**Cause.** `JsonSerializer` overloads that take `body.GetType()` carry
`RequiresDynamicCode`, and NativeAOT runs with `IsDynamicCodeSupported=false`.

**Fix.** Use a source-generated `JsonSerializerContext` and pass an explicit
`JsonTypeInfo<T>` at the call site, so a missing registration is a compile error rather
than a runtime failure. Do not paper over it by rooting the whole assembly in a runtime
directives file.

## Test harness

### The offline spike reports three failures on a GDK machine

**Symptom.** `dotnet run --project tools\NetRumble.PlatformSpike -- --platform=offline`
reports failures including `0x89240100`.

**Cause.** Two of the checks call Party and XBOX services directly to verify the native
ABI, regardless of which provider is selected. The offline provider deliberately does not
initialize the Gaming Runtime, so those calls fail.

**Fix.** Nothing to fix. Read those as environment checks rather than as offline-provider
regressions. The default and `gamecore` modes are the regression gate.

### A security test passes even with the check removed

If you write a test that forges traffic, confirm that it fails when you disable the
defence it is meant to exercise. One test in this repository passed with its token check
disabled, because the forged datagram arrived from a different ephemeral source port and
was rejected earlier for a reason the test was not measuring. See
[Design notes](design-notes.md#a-passing-test-that-proved-nothing).
