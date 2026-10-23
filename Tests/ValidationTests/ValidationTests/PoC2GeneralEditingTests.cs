using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SrmMetadataBuilderRewrite;
using Xunit;

namespace ValidationTests;

public class PoC2GeneralEditingTests
{
    private static readonly string s_sampleDll = Path.Combine(TestConfiguration.DataDir, "input", "MauiStripSample.dll");

    [Fact]
    public void TestPartialRewrite_PreservesUnmodifiedBodies_AndInjectsCustomBody()
    {
        Assert.True(File.Exists(s_sampleDll), $"Sample DLL not found: {s_sampleDll}");

        string outputPath = Path.Combine(Path.GetTempPath(), $"PoC2_Partial_{Guid.NewGuid():N}.dll");
        try
        {
            MethodDefinitionHandle targetMethodHandle;
            string targetMethodName;
            int originalTargetRva;

            using (var context = AssemblyRewriteContext.Load(s_sampleDll))
            {
                var reader = context.Reader;
                var editor = context.Editor;

                // Pick a method that has an IL body
                var candidate = reader.MethodDefinitions
                    .Select(h => (Handle: h, Def: reader.GetMethodDefinition(h)))
                    .First(m => m.Def.RelativeVirtualAddress != 0 && !m.Def.Name.IsNil);

                targetMethodHandle = candidate.Handle;
                targetMethodName = reader.GetString(candidate.Def.Name);
                originalTargetRva = candidate.Def.RelativeVirtualAddress;

                // Create a custom method body: ldc.i4.s 42, ret
                var codeBuilder = new BlobBuilder();
                var il = new InstructionEncoder(codeBuilder);
                il.LoadConstantI4(42);
                il.OpCode(ILOpCode.Ret);

                var customBodyHandle = editor.CreateMethodBody(il, maxStack: 1);

                // Edit ONLY the target method; leave all other methods untouched!
                editor.Edit(targetMethodHandle)
                      .SetBody(customBodyHandle)
                      .SetImplAttributes(candidate.Def.ImplAttributes | MethodImplAttributes.NoInlining);

                context.Writer.Write(outputPath);
            }

            // Verify with PEReader
            using var peIn = new PEReader(File.OpenRead(s_sampleDll));
            using var peOut = new PEReader(File.OpenRead(outputPath));
            var mrIn = peIn.GetMetadataReader();
            var mrOut = peOut.GetMetadataReader();

            // Target method verification
            var modifiedMethod = mrOut.GetMethodDefinition(targetMethodHandle);
            Assert.Equal(targetMethodName, mrOut.GetString(modifiedMethod.Name));
            Assert.NotEqual(0, modifiedMethod.RelativeVirtualAddress);

            var modifiedBody = peOut.GetMethodBody(modifiedMethod.RelativeVirtualAddress);
            Assert.NotNull(modifiedBody);
            byte[] ilBytes = modifiedBody.GetILBytes();
            // ldc.i4.s 42 (0x1F, 0x2A) + ret (0x2A)
            Assert.Equal(new byte[] { 0x1F, 42, 0x2A }, ilBytes);

            // Verify that other methods with bodies retained their valid bodies!
            int verifiedOtherBodies = 0;
            foreach (var handle in mrIn.MethodDefinitions)
            {
                if (handle == targetMethodHandle) continue;

                var origMethod = mrIn.GetMethodDefinition(handle);
                if (origMethod.RelativeVirtualAddress == 0) continue;

                var outMethod = mrOut.GetMethodDefinition(handle);
                Assert.NotEqual(0, outMethod.RelativeVirtualAddress);

                var origBody = peIn.GetMethodBody(origMethod.RelativeVirtualAddress);
                var outBody = peOut.GetMethodBody(outMethod.RelativeVirtualAddress);

                Assert.NotNull(outBody);
                Assert.Equal(origBody.GetILBytes(), outBody.GetILBytes());
                Assert.Equal(origBody.MaxStack, outBody.MaxStack);

                verifiedOtherBodies++;
                if (verifiedOtherBodies >= 10) break; // Verify sample of 10 bodies
            }

            Assert.True(verifiedOtherBodies > 0);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void TestGeneralEditing_TypeAndFieldAndMethodMutations()
    {
        Assert.True(File.Exists(s_sampleDll), $"Sample DLL not found: {s_sampleDll}");

        string outputPath = Path.Combine(Path.GetTempPath(), $"PoC2_GeneralMutations_{Guid.NewGuid():N}.dll");
        try
        {
            TypeDefinitionHandle targetTypeHandle;
            FieldDefinitionHandle targetFieldHandle;
            MethodDefinitionHandle targetMethodHandle;

            using (var context = AssemblyRewriteContext.Load(s_sampleDll))
            {
                var reader = context.Reader;
                var editor = context.Editor;

                // Pick first non-<Module> TypeDef
                targetTypeHandle = reader.TypeDefinitions.Skip(1).First();
                var targetType = reader.GetTypeDefinition(targetTypeHandle);

                // Pick a field and method from this type if available
                targetFieldHandle = reader.FieldDefinitions.First();
                targetMethodHandle = reader.MethodDefinitions.First(h => reader.GetMethodDefinition(h).RelativeVirtualAddress != 0);

                // Edit TypeDef
                var newTypeName = editor.GetOrAddString("RenamedTypeByPoC2");
                var newNamespace = editor.GetOrAddString("Custom.Mutated.Namespace");
                editor.Edit(targetTypeHandle)
                      .SetName(newTypeName)
                      .SetNamespace(newNamespace)
                      .SetAttributes(TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);

                // Edit FieldDef
                var newFieldName = editor.GetOrAddString("_mutatedField42");
                editor.Edit(targetFieldHandle)
                      .SetName(newFieldName)
                      .SetAttributes(FieldAttributes.Public | FieldAttributes.Static);

                // Edit MethodDef: Clear body (make bodyless)
                var newMethodName = editor.GetOrAddString("ClearedBodyMethod");
                editor.Edit(targetMethodHandle)
                      .SetName(newMethodName)
                      .ClearBody();

                context.Writer.Write(outputPath);
            }

            // Verify mutations in output assembly
            using var peOut = new PEReader(File.OpenRead(outputPath));
            var mrOut = peOut.GetMetadataReader();

            // Verify TypeDef
            var mutatedType = mrOut.GetTypeDefinition(targetTypeHandle);
            Assert.Equal("RenamedTypeByPoC2", mrOut.GetString(mutatedType.Name));
            Assert.Equal("Custom.Mutated.Namespace", mrOut.GetString(mutatedType.Namespace));
            Assert.Equal(TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed, mutatedType.Attributes);

            // Verify FieldDef
            var mutatedField = mrOut.GetFieldDefinition(targetFieldHandle);
            Assert.Equal("_mutatedField42", mrOut.GetString(mutatedField.Name));
            Assert.Equal(FieldAttributes.Public | FieldAttributes.Static, mutatedField.Attributes);

            // Verify MethodDef
            var mutatedMethod = mrOut.GetMethodDefinition(targetMethodHandle);
            Assert.Equal("ClearedBodyMethod", mrOut.GetString(mutatedMethod.Name));
            Assert.Equal(0, mutatedMethod.RelativeVirtualAddress);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void TestRawMethodBodyInjection()
    {
        Assert.True(File.Exists(s_sampleDll), $"Sample DLL not found: {s_sampleDll}");

        string outputPath = Path.Combine(Path.GetTempPath(), $"PoC2_RawBody_{Guid.NewGuid():N}.dll");
        try
        {
            MethodDefinitionHandle targetMethodHandle;

            using (var context = AssemblyRewriteContext.Load(s_sampleDll))
            {
                var reader = context.Reader;
                var editor = context.Editor;

                targetMethodHandle = reader.MethodDefinitions.First(h => reader.GetMethodDefinition(h).RelativeVirtualAddress != 0);

                // Tiny header format for method body:
                // byte 0: (IL byte size << 2) | 0x02 (TinyFormat)
                // bytes 1..N: IL instructions
                // E.g., for 1-byte ret (0x2A):
                // header = (1 << 2) | 0x02 = 0x06
                // followed by 0x2A
                byte[] rawTinyBody = new byte[] { 0x06, 0x2A };
                var rawBodyHandle = editor.CreateRawMethodBody(rawTinyBody);

                editor.Edit(targetMethodHandle)
                      .SetBody(rawBodyHandle);

                context.Writer.Write(outputPath);
            }

            using var peOut = new PEReader(File.OpenRead(outputPath));
            var mrOut = peOut.GetMetadataReader();
            var method = mrOut.GetMethodDefinition(targetMethodHandle);
            Assert.NotEqual(0, method.RelativeVirtualAddress);

            var body = peOut.GetMethodBody(method.RelativeVirtualAddress);
            Assert.NotNull(body);
            Assert.Equal(new byte[] { 0x2A }, body.GetILBytes());
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
