# Mono.Cecil

Mono.Cecil exposes an **editable object model of .NET assemblies**. You read and modify the same objects, then write the result to a new assembly file.

Most types are in `Mono.Cecil`. IL-related types are in `Mono.Cecil.Cil`.

## 1. Object Hierarchy

```text
AssemblyDefinition
├── Name → AssemblyNameDefinition
├── CustomAttributes
└── Modules / MainModule → ModuleDefinition
    ├── AssemblyReferences → AssemblyNameReference
    ├── Resources
    ├── CustomAttributes
    └── Types → TypeDefinition
        ├── BaseType → TypeReference
        ├── Interfaces → InterfaceImplementation
        ├── GenericParameters
        ├── NestedTypes → TypeDefinition
        ├── Fields → FieldDefinition
        ├── Properties → PropertyDefinition
        ├── Events → EventDefinition
        ├── CustomAttributes
        └── Methods → MethodDefinition
            ├── Parameters → ParameterDefinition
            ├── ReturnType → TypeReference
            ├── GenericParameters
            ├── CustomAttributes
            └── Body → MethodBody
                ├── Instructions → Instruction
                ├── Variables → VariableDefinition
                └── ExceptionHandlers → ExceptionHandler
```

## 2. Assembly and Module

| Type | Purpose | Main members |
|---|---|---|
| `AssemblyDefinition` | Represents the assembly as a whole | `Name`, `FullName`, `MainModule`, `Modules`, `CustomAttributes`, `ReadAssembly(...)`, `CreateAssembly(...)`, `Write(...)`, `Dispose()` |
| `AssemblyNameReference` | Describes an assembly identity used as a reference | `Name`, `Version`, `Culture`, `PublicKeyToken`, `FullName` |
| `AssemblyNameDefinition` | Describes the assembly’s own identity; inherits from `AssemblyNameReference` | Inherited identity properties |
| `ModuleDefinition` | Provides access to module contents and related services | `Name`, `Assembly`, `Types`, `AssemblyReferences`, `Resources`, `TypeSystem`, `EntryPoint`, `Mvid`, `GetTypes()`, `GetType(...)`, `ImportReference(...)`, `LookupToken(...)` |

> `module.Types` contains top-level types. `module.GetTypes()` recursively includes nested types.
>
> Referenced assemblies are not members of `assembly.Modules`.

## 3. Types and Members

| Type | Represents | Useful properties |
|---|---|---|
| `TypeDefinition` | A declared class, struct, interface, enum, or delegate | `Name`, `Namespace`, `FullName`, `BaseType`, `DeclaringType`, `Methods`, `Fields`, `Properties`, `Events`, `NestedTypes`, `Interfaces`, `GenericParameters` |
| `MethodDefinition` | A method or constructor | `Name`, `DeclaringType`, `ReturnType`, `Parameters`, `GenericParameters`, `HasBody`, `Body`, `IsStatic`, `IsConstructor` |
| `FieldDefinition` | A field | `Name`, `DeclaringType`, `FieldType`, `IsStatic`, `IsInitOnly`, `IsLiteral`, `HasConstant`, `Constant` |
| `PropertyDefinition` | A property and its accessor associations | `Name`, `PropertyType`, `Parameters`, `GetMethod`, `SetMethod` |
| `EventDefinition` | An event and its accessor associations | `Name`, `EventType`, `AddMethod`, `RemoveMethod`, `InvokeMethod` |
| `ParameterDefinition` | A method or indexer parameter | `Name`, `Index`, `ParameterType`, `IsIn`, `IsOut`, `IsOptional`, `HasConstant`, `Constant` |
| `GenericParameter` | A type parameter such as `T` | `Name`, `Position`, `Owner`, `Constraints` |
| `InterfaceImplementation` | A type’s implementation of an interface | `InterfaceType`, `CustomAttributes` |

- Properties contain accessor associations, not IL bodies. Their accessor methods contain the code.
- Constructors are represented by `MethodDefinition`.
- A `TypeDefinition` brings together information from multiple metadata tables, not just one `TypeDef` row.

## 4. Definitions versus References

| Reference | Definition |
|---|---|
| `TypeReference` | `TypeDefinition` |
| `MethodReference` | `MethodDefinition` |
| `FieldReference` | `FieldDefinition` |

A **reference** describes or identifies a type or member. A **definition** represents its declaration and exposes additional information, such as members or a method body.

Definitions inherit from their corresponding reference types.

> A reference does not necessarily refer to something in an external assembly.

### Resolving and importing

```csharp
var definition = reference.Resolve();
var imported = destinationModule.ImportReference(reference);
```

- **`Resolve()`** attempts to find the referenced definition. It may require locating and reading dependencies.
- **`ImportReference(...)`** obtains a reference suitable for the destination module. It does not copy the implementation.

### Constructed types and methods

| Type | Example |
|---|---|
| `ArrayType` | `int[]` |
| `ByReferenceType` | The type used for a `ref int` parameter |
| `PointerType` | `int*` |
| `GenericInstanceType` | `List<int>` |
| `GenericInstanceMethod` | `Identity<int>(...)` |

## 5. Method Bodies and IL

Namespace: `Mono.Cecil.Cil`.

| Type | Main members |
|---|---|
| `MethodBody` | `Instructions`, `Variables`, `ExceptionHandlers`, `InitLocals`, `MaxStackSize`, `GetILProcessor()` |
| `Instruction` | `Offset`, `OpCode`, `Operand`, `Previous`, `Next`, `Create(...)` |
| `OpCodes` | Named opcodes such as `Ldarg_0`, `Call`, `Ldstr`, `Add`, `Ret` |
| `ILProcessor` | `Create(...)`, `Append(...)`, `InsertBefore(...)`, `InsertAfter(...)`, `Replace(...)`, `Remove(...)` |
| `VariableDefinition` | `VariableType`, `Index` |
| `ExceptionHandler` | `HandlerType`, `TryStart`, `TryEnd`, `HandlerStart`, `HandlerEnd`, `CatchType`, `FilterStart` |

An instruction’s operand depends on its opcode. Examples include:

- A string for `ldstr`.
- A method reference for `call`.
- Another instruction for a branch target.
- A numeric constant.
- No operand for instructions such as `add`.

> Check `method.HasBody` before inspecting IL. Abstract methods and ordinary P/Invoke declarations have no managed IL body.

## 6. Flags versus Custom Attributes

| Concept | Example | Cecil API |
|---|---|---|
| Metadata flags | `public`, `abstract`, `static` | `Attributes` and convenience properties such as `IsPublic`, `IsAbstract`, `IsStatic`, where applicable |
| Custom attributes | `[Exploration("example")]` | `CustomAttributes` |

Many flag properties inspect or modify bits in a single backing field. They are not individually stored Boolean fields.

### CustomAttribute

```text
CustomAttribute
├── AttributeType
├── Constructor
├── ConstructorArguments
├── Fields       → named arguments targeting attribute fields
└── Properties   → named arguments targeting attribute properties
```

Attributes belong to the object they annotate:

```csharp
assembly.CustomAttributes
type.CustomAttributes
method.CustomAttributes
```

These are separate collections.

## 7. Metadata Tokens

A metadata-table token identifies a table and a row within a module:

```text
0x02000003
  │     └── Row 3
  └──────── TypeDef table
```

Examples:

| Token | Meaning |
|---|---|
| `0x02000003` | `TypeDef`, row 3 |
| `0x06000005` | `MethodDef`, row 5 |
| `0x20000001` | `Assembly`, row 1 |

- Definition and reference objects expose `MetadataToken`.
- A token is not a file offset or memory address.
- Tokens are module-scoped, not globally unique.
- Tokens may change when metadata is rewritten.
- Newly created objects may not yet have an assigned output row.

## 8. Reading, Writing, and Creation Options

| Type | Purpose | Common options |
|---|---|---|
| `ReaderParameters` | Configure reading | `ReadingMode`, `InMemory`, `ReadSymbols`, `AssemblyResolver` |
| `WriterParameters` | Configure writing | `WriteSymbols`, `StrongNameKeyBlob`, `DeterministicMvid` |
| `ModuleParameters` | Configure module creation | `Kind`, `Runtime`, `Architecture` |

### Lazy loading

Deferred reading is the default. Accessing a property can trigger metadata reading and object creation.

Existence checks such as `HasMethods`, `HasFields`, and `HasCustomAttributes` can avoid constructing a collection merely to check whether it contains anything.

> `InMemory` controls input buffering. It is not the same as eagerly materializing the object model.

### Basic usage

```csharp
using var assembly = AssemblyDefinition.ReadAssembly(inputPath);
var module = assembly.MainModule;

foreach (var type in module.GetTypes())
{
    Console.WriteLine(type.FullName);
}

// After making edits:
assembly.Write(outputPath);
```

Keep the assembly alive while working with it, and use a separate output path for experiments.

## 9. Editing and Automatic Bookkeeping

Editing uses writable properties, mutable collections, and specialized helpers:

```csharp
type.Name = "RenamedCalculator";
method.CustomAttributes.Remove(attribute);
type.Methods.Remove(method);

var il = method.Body.GetILProcessor();
il.InsertBefore(existingInstruction, il.Create(OpCodes.Nop));
```

Automatic consistency comes from several mechanisms:

| Mechanism | Example |
|---|---|
| Property setters | Invalidate cached full names after a rename |
| Collection hooks | Maintain ownership when members are added or removed |
| Shared object references | A call operand still points to the same method object after it is renamed |
| Writer logic | Assign output tokens and encode metadata and method bodies |

A **hook** is a method called by another operation to run specialized behavior. For example, a collection’s `Add(...)` calls an overridden `OnAdd(...)` to set ownership.

## 10. Safety Boundary

Cecil handles binary encoding and some structural bookkeeping. It does not automatically make arbitrary transformations safe.

The transformation must handle:

- References to removed types or members.
- Callers affected by signature changes.
- Evaluation-stack correctness.
- Branch targets and exception-handler boundaries.
- External consumers and reflection-based names.
- Debug symbols and signing requirements.

> Successfully writing a DLL does not prove that the resulting program is valid.
