# Cecil calculator demo

A small console application that rewrites a copy of the earlier
[calculator sample](../../docs/learning-exploring/dll-inspection/Calculator.cs)
using the **original, uninstrumented dotnet/cecil build**. No tracing or weaving
is enabled by this project.

## Run

With the .NET 10 SDK, open a terminal in this directory and run:

```powershell
dotnet run
```

Run from this directory rather than inside `CecilAnalysisFork`, whose
`global.json` selects a different SDK.

The project copies the existing Release/net10.0 calculator DLL and portable PDB
into `bin\Debug\net10.0\input`. Each run writes a new DLL under
`bin\Debug\net10.0\rewritten\<UTC timestamp>`, with updated portable symbols
embedded in the DLL. The original input is not overwritten.

If the original sample has not been built, build it first:

```powershell
dotnet build "..\..\docs\learning-exploring\dll-inspection\DllExplorerSample.csproj" -c Release
```

You can also provide explicit paths:

```powershell
dotnet run -- "<calculator.dll>" "<new-output.dll>"
```

The input must be the original calculator sample with matching portable or
embedded portable symbols. An existing output file is rejected.

## Code to follow

- [Program.cs](Program.cs): the explicit `Main` entry point and
  `RewriteCalculator`, which performs all the Cecil operations.
- [DemoVerification.cs](DemoVerification.cs): executes the original and rewritten
  calculators in separate assembly load contexts and checks the results using
  ordinary .NET reflection, not additional Cecil operations.

The rewrite makes three changes:

1. `Add(2, 3)` returns `15` instead of `5` by adding a bonus of 10. The original
   call-count update is retained.
2. `PrintGreeting` prints `Hello from the Cecil-rewritten DLL`.
3. The exploration attribute is removed from `Add`.

The demo also verifies that division, its divide-by-zero exception handler,
and the nested helper used by `Twice` still work. A SHA-256 comparison checks
that the input DLL was not changed.

## Cecil reference

[CecilCalculatorDemo.csproj](CecilCalculatorDemo.csproj) references:

```text
..\CecilAnalysisFork\artifacts\trace-baseline\Mono.Cecil.dll
```

This is the preserved uninstrumented build of the selected dotnet/cecil baseline
at commit `9e14a6389353d0a2795e88c2025643315cb77e7a`, not the upstream
`Mono.Cecil` NuGet package or an instrumented DLL. It contains no recorder.

The reference is controlled by the `CecilAssemblyPath` MSBuild property so a
different DLL can be selected during a later logging walkthrough. For now the
default remains the original build. The console prints the loaded Cecil
assembly's identity, path, and SHA-256 so the actual dependency is visible.
