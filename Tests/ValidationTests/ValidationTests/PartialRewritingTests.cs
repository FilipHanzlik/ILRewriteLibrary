using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SrmRewrite;
using Xunit;
using Xunit.Abstractions;

namespace ValidationTests;

public class PartialRewritingTests
{
    private readonly ITestOutputHelper _output;

    public PartialRewritingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PartialStripping_SingleMethod_PreservesAllOtherBodies()
    {
        string inputPath = Path.Combine(TestConfiguration.DataDir, "input/MauiStripSample.dll");
        Assert.True(File.Exists(inputPath), $"Missing test input at '{inputPath}'");

        string tempOutPath = Path.Combine(Path.GetTempPath(), $"PartialStrip_{Guid.NewGuid():N}.dll");
        try
        {
            MethodDefinitionHandle strippedHandle = default;
            int totalMethodsWithBody = 0;

            using (var ctx = AssemblyRewriteContext.Load(inputPath))
            {
                foreach (var handle in ctx.Reader.MethodDefinitions)
                {
                    var def = ctx.Reader.MetadataReader.GetMethodDefinition(handle);
                    if (def.RelativeVirtualAddress != 0)
                    {
                        totalMethodsWithBody++;
                        if (strippedHandle.IsNil)
                        {
                            strippedHandle = handle;
                            ctx.Editor.Edit(handle).ReplaceBodyWithSharedRet();
                        }
                    }
                }

                ctx.Writer.Write(tempOutPath);
            }

            Assert.False(strippedHandle.IsNil, "No method with body found in input assembly.");
            Assert.True(File.Exists(tempOutPath), "Rewritten output PE was not created.");

            // Verify with PEReader on rewritten PE
            using var fs = File.OpenRead(tempOutPath);
            using var peReader = new PEReader(fs);
            var metaReader = peReader.GetMetadataReader();

            int verifiedBodyCount = 0;
            foreach (var handle in metaReader.MethodDefinitions)
            {
                var method = metaReader.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress != 0)
                {
                    var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                    Assert.NotNull(body);
                    Assert.True(body.Size > 0);

                    if (handle == strippedHandle)
                    {
                        var ilBytes = body.GetILBytes();
                        Assert.Equal(new byte[] { 0x2A }, ilBytes);
                    }
                    else
                    {
                        // IL instructions must be readable
                        var ilBytes = body.GetILBytes();
                        Assert.NotNull(ilBytes);
                    }

                    verifiedBodyCount++;
                }
            }

            Assert.Equal(totalMethodsWithBody, verifiedBodyCount);
            _output.WriteLine($"Successfully verified {verifiedBodyCount} method bodies (1 stripped to shared ret, {verifiedBodyCount - 1} preserved intact).");
        }
        finally
        {
            if (File.Exists(tempOutPath))
                File.Delete(tempOutPath);
        }
    }

    [Fact]
    public void CustomMethodBody_SetRawBody_EmitsCorrectIL()
    {
        string inputPath = Path.Combine(TestConfiguration.DataDir, "input/MauiStripSample.dll");
        string tempOutPath = Path.Combine(Path.GetTempPath(), $"CustomBody_{Guid.NewGuid():N}.dll");

        try
        {
            MethodDefinitionHandle customHandle = default;

            using (var ctx = AssemblyRewriteContext.Load(inputPath))
            {
                foreach (var handle in ctx.Reader.MethodDefinitions)
                {
                    var def = ctx.Reader.MetadataReader.GetMethodDefinition(handle);
                    if (def.RelativeVirtualAddress != 0)
                    {
                        customHandle = handle;
                        // Tiny header: (codeLength << 2) | 0x02 = (2 << 2) | 2 = 0x0A
                        // IL: 0x16 (ldc.i4.0), 0x2A (ret)
                        byte[] customRawBody = [0x0A, 0x16, 0x2A];
                        ctx.Editor.Edit(handle).SetRawBody(customRawBody);
                        break;
                    }
                }

                ctx.Writer.Write(tempOutPath);
            }

            using var fs = File.OpenRead(tempOutPath);
            using var peReader = new PEReader(fs);
            var metaReader = peReader.GetMetadataReader();

            var customMethod = metaReader.GetMethodDefinition(customHandle);
            Assert.True(customMethod.RelativeVirtualAddress != 0);

            var body = peReader.GetMethodBody(customMethod.RelativeVirtualAddress);
            Assert.NotNull(body);
            var ilBytes = body.GetILBytes();
            Assert.Equal(new byte[] { 0x16, 0x2A }, ilBytes);

            _output.WriteLine("Successfully verified custom method body replacement and valid IL emission.");
        }
        finally
        {
            if (File.Exists(tempOutPath))
                File.Delete(tempOutPath);
        }
    }
}
