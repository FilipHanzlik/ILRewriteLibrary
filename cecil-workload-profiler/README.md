# Cecil Workload Profiler: Setup & Execution Guide

This guide covers the end-to-end process for building, weaving, linking, and capturing trace baselines from the instrumented `Mono.Cecil` project on Linux.

## 1. Pre-Build Configuration

When building Cecil on modern Linux distributions, legacy SHA-1 strong-name signing will fail. Ensure the following configurations are set before compiling:

1. **Disable Signing:** In `cecil-builder/Mono.Cecil.csproj`, set `<SignAssembly>false</SignAssembly>`.
2. **Remove Public Keys:** In `AssemblyInfo.cs`, remove `PublicKey=...` from all `[InternalsVisibleTo]` declarations so unsigned companion assemblies can access internal members.
3. **Debug Symbols:** Force `<DebugType>portable</DebugType>` across all projects.

## 2. Compile Cecil & Companions

Build the entire Cecil source tree in Release mode with tracing support enabled.

```bash
cd cecil-builder
dotnet build -c Release -p:CecilTraceSupport=true
cd ..
```

**Expected Artifacts:**
* `cecil-builder/bin/Release/netstandard2.0/Mono.Cecil.dll` (Core)
* `cecil-builder/rocks/bin/Release/netstandard2.0/Mono.Cecil.Rocks.dll`
* `cecil-builder/symbols/pdb/bin/Release/netstandard2.0/Mono.Cecil.Pdb.dll`
* `cecil-builder/symbols/mdb/bin/Release/netstandard2.0/Mono.Cecil.Mdb.dll`

## 3. Weave the Core Assembly

Run the custom Weaver to inject tracking hooks into the core `Mono.Cecil.dll`. 

> **Critical:** The output file MUST be named exactly `Mono.Cecil.dll`. Do not use suffixes like `.analytics.dll`, or the runtime will fail with a `FileNotFoundException` during execution. 

```bash
dotnet run --project Cecil.Trace.Weaver/Cecil.Trace.Weaver.csproj -c Release -- \
  --input ./cecil-builder/bin/Release/netstandard2.0/Mono.Cecil.dll \
  --output ./output/Mono.Cecil.dll
```

*Note: Companion assemblies (Rocks, Pdb, Mdb) do not need to be woven. They route their operations through the core assembly, triggering the hooks automatically.*

## 4. Configure the Workload Project

Update `Cecil.Trace.Workload/Cecil.Trace.Workload.csproj` to link the freshly woven core library and the un-woven companions. Use absolute paths to prevent MSBuild caching and resolution errors.

```xml
<ItemGroup>
  <!-- 1. The woven core library -->
  <Reference Include="Mono.Cecil">
    <HintPath>/home/filip/Documents/MFF/bakalářka/cecil-workload-profiler/output/Mono.Cecil.dll</HintPath>
  </Reference>

  <!-- 2. The un-woven companion assemblies -->
  <Reference Include="Mono.Cecil.Rocks">
    <HintPath>/home/filip/Documents/MFF/bakalářka/cecil-workload-profiler/cecil-builder/rocks/bin/Release/netstandard2.0/Mono.Cecil.Rocks.dll</HintPath>
  </Reference>
  <Reference Include="Mono.Cecil.Pdb">
    <HintPath>/home/filip/Documents/MFF/bakalářka/cecil-workload-profiler/cecil-builder/symbols/pdb/bin/Release/netstandard2.0/Mono.Cecil.Pdb.dll</HintPath>
  </Reference>
  <Reference Include="Mono.Cecil.Mdb">
    <HintPath>/home/filip/Documents/MFF/bakalářka/cecil-workload-profiler/cecil-builder/symbols/mdb/bin/Release/netstandard2.0/Mono.Cecil.Mdb.dll</HintPath>
  </Reference>
</ItemGroup>
```

## 5. Execute Scenarios & Capture Traces

The tracer requires the `CECIL_TRACE_FILE` environment variable to flush in-memory traces to disk. Additionally, the Workload throws an `IOException` if output dummy files already exist from a previous run.

**The standard execution sequence:**

```bash
# 1. Clean dummy files from previous runs
rm -f ./workload-out.dll ./workload-out.dll.input.dll ./workload-out.dll.mdb.dll*

# 2. Force MSBuild to drop cached paths
dotnet clean Cecil.Trace.Workload/Cecil.Trace.Workload.csproj -c Release

# 3. Execute the workload and define the trace output path
CECIL_TRACE_FILE=./traces/readwrite.jsonl dotnet run \
  --project Cecil.Trace.Workload/Cecil.Trace.Workload.csproj -c Release -- \
  --scenario readwrite \
  --output ./workload-out.dll
```

### Automation Script (`run_all.sh`)

Save this script to the root directory to automatically run all workload scenarios and generate their respective trace files:

```bash
#!/bin/bash

# Ensure output directory exists
mkdir -p ./traces

# Define the scenarios
SCENARIOS=("readwrite" "values" "edges" "shapes" "companions" "shutdown")

# Clean project cache once
dotnet clean Cecil.Trace.Workload/Cecil.Trace.Workload.csproj -c Release

for SCENARIO in "${SCENARIOS[@]}"; do
    echo "Running scenario: $SCENARIO..."
    
    # Clean previous artifacts
    rm -f ./workload-out.dll ./workload-out.dll.input.dll ./workload-out.dll.mdb.dll*
    
    # Execute with trace variable
    CECIL_TRACE_FILE="./traces/${SCENARIO}.jsonl" \
    dotnet run --project Cecil.Trace.Workload/Cecil.Trace.Workload.csproj -c Release -- \
      --scenario "$SCENARIO" \
      --output ./workload-out.dll
      
    echo "Completed $SCENARIO. Trace saved to ./traces/${SCENARIO}.jsonl"
    echo "----------------------------------------"
done
```

## 6. Troubleshooting

| Symptom | Root Cause | Solution |
| :--- | :--- | :--- |
| **`error CS0234: The namespace 'Rocks' does not exist`** | The `<HintPath>` is pointing to a folder, a `.pdb` file, or a file that does not exist. | Ensure the path points directly to the `.dll` file using absolute paths. |
| **`FileNotFoundException: Could not load assembly 'Mono.Cecil'`** | The Weaver output was saved as `Mono.Cecil.analytics.dll`. | Rename the file in `/output/` to `Mono.Cecil.dll` and update the csproj. |
| **`IOException: Workload outputs must not already exist`** | A previous run left dummy artifacts on the disk. | Run `rm -f ./workload-out.dll*` before executing. |
| **Workload succeeds, but no `.jsonl` trace is found** | The `CECIL_TRACE_FILE` variable was not passed. | Prepend `CECIL_TRACE_FILE=./path/to/trace.jsonl` to the `dotnet run` command. |