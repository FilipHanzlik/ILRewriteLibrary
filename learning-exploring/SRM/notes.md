# SRM Reading — Cheat Sheet

**System.Reflection.Metadata (SRM)** provides low-level access to .NET metadata and related PE/IL data. Unlike Cecil, it does not expose a rich, mutable assembly object graph.

Most reading types are in `System.Reflection.Metadata`. PE-related types are in `System.Reflection.PortableExecutable`. Token helpers and encoding/building APIs are in `System.Reflection.Metadata.Ecma335`.

## 1. Opening an Assembly

```csharp
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using var stream = new FileStream(
    inputPath,
    FileMode.Open,
    FileAccess.Read,
    FileShare.Read);

using var peReader = new PEReader(stream);
MetadataReader reader = peReader.GetMetadataReader();
```

| Component | Role |
|---|---|
| `FileStream` | Access to the input file |
| `PEReader` | Access to PE structure, metadata, and method bodies |
| `MetadataReader` | Interpretation of metadata tables and heaps |

- `FileAccess.Read` controls your access.
- `FileShare.Read` permits other readers while restricting ordinary concurrent writers on Windows.
- `using` disposes at scope exit, including through `return` or an exception—not immediately after the last use.
- Multiple `using` declarations are disposed in reverse order.
- Disposal releases resources; it is not the same as garbage collection.

## 2. Input Loading versus Object Creation

| `PEStreamOptions` | Behavior |
|---|---|
| `Default` | Read image data lazily |
| `PrefetchMetadata` | Read metadata upfront; other image content is unavailable |
| `PrefetchEntireImage` | Read the whole image upfront |

```csharp
using var peReader = new PEReader(
    stream, PEStreamOptions.PrefetchEntireImage);
```

**Loading all DLL bytes does not construct objects for every type, method, and instruction.**

Once obtained, the metadata reader accesses metadata in memory. Individual property accesses do not normally cause separate file reads.

Keep the owning `PEReader` alive while using its metadata views.

## 3. The Central Navigation Pattern

```text
Handle → metadata record → another handle or a direct value
                              ↓
                         decoded heap value
```

```csharp
foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
{
    TypeDefinition type = reader.GetTypeDefinition(handle);
    string name = reader.GetString(type.Name);
}
```

| Expression | Meaning |
|---|---|
| `handle` | Identifies a `TypeDef` row |
| `type` | Lightweight view of that row |
| `type.Name` | Handle identifying text in `#Strings` |
| `reader.GetString(type.Name)` | Decoded C# string |

The type-handle collection generates handles from row numbers as it enumerates. It does not require a prebuilt list of type objects.

`GetTypeDefinition` creates a lightweight view. Properties such as `Name` then access the relevant columns.

## 4. Handles and IsNil

A handle is a small value type identifying metadata within a reader's context.

It is **not**:
- An ordinary object reference.
- A memory address.
- A globally unique identifier.

```csharp
var parentHandle = type.GetDeclaringType();

if (!parentHandle.IsNil)
{
    var parent = reader.GetTypeDefinition(parentHandle);
}
```

**`IsNil` means “no value.”** For
