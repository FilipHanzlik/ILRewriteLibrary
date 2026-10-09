using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Reading
{
    internal class Program
    {
        private static string assemblyPath = "C:\\Users\\t-fhanzlik\\OneDrive - Microsoft\\Documents\\projects\\IL rewrite\\docs\\learning-exploring\\dll-inspection\\bin\\Release\\net10.0\\DllExplorerSample.dll";

        static void Main(string[] args)
        {
            printAssemblyInfo();

            Console.WriteLine();

            printTypes();

            Console.WriteLine();

            printCallTarget(13);
        }

        private static void printAssemblyInfo()
        {
            using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using PEReader peReader = new PEReader(fs);

            var metadataReader = peReader.GetMetadataReader();

            var assemblyDefinition = metadataReader.GetAssemblyDefinition();

            var nameHandle = assemblyDefinition.Name;
            var version = assemblyDefinition.Version;

            string name = metadataReader.GetString(nameHandle);

            Console.WriteLine($"Assembly name: {name}");
            Console.WriteLine($"Assembly version: {version}");
        }

        private static void printTypes()
        {
            using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using PEReader peReader = new PEReader(fs);

            var metadataReader = peReader.GetMetadataReader();

            foreach (var typeHandle in metadataReader.TypeDefinitions)
            {
                var type = metadataReader.GetTypeDefinition(typeHandle);
                string typeinfo = getTypeInfoString(type, metadataReader);
                Console.WriteLine(typeinfo);
                printFieldsOfType(type, metadataReader);
                printMethodsOfType(type, metadataReader, peReader);

                

            }
        }

        private static string getTypeInfoString(TypeDefinition type, MetadataReader reader)
        {
            StringHandle namespaceHandle = type.Namespace;
            StringHandle nameHandle = type.Name;

            string namespacestring = reader.GetString(namespaceHandle);
            string typename = reader.GetString(nameHandle);

            return $"Type: {typename} in namespace {namespacestring}";


        }

        private static void printFieldsOfType(TypeDefinition type, MetadataReader reader)
        {
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                var fieldname = reader.GetString(field.Name);
                var fieldFlags = field.Attributes;

                Console.WriteLine($"Field: {fieldname} with flags {fieldFlags}");
            }
        }

        private static void printMethodsOfType(TypeDefinition type, MetadataReader reader, PEReader peReader)
        {
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var fieldname = reader.GetString(method.Name);
                var fieldFlags = method.Attributes;
                var rva = method.RelativeVirtualAddress;

                Console.WriteLine($"Method: {fieldname} with flags {fieldFlags}  (RVA: {rva})");

                if (reader.StringComparer.Equals(method.Name, "Add"))
                {
                    var body = peReader.GetMethodBody(rva);
                    Console.WriteLine($"    Max stack: {body.MaxStack}");
                    Console.WriteLine($"    Local signature is nil: {body.LocalSignature.IsNil}");
                    Console.WriteLine($"    Exception regions: {body.ExceptionRegions.Length}");
                    Console.WriteLine($"    IL bytes: {Convert.ToHexString(body.GetILContent().AsSpan())}");
                }
            }
        }

        private static void printCallTarget(int methodRow)
        {
            using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using PEReader peReader = new PEReader(fs);

            var reader = peReader.GetMetadataReader();

            var methodHandle = MetadataTokens.MethodDefinitionHandle(methodRow);
            var method = reader.GetMethodDefinition(methodHandle);

            var fieldname = reader.GetString(method.Name);
            var fieldFlags = method.Attributes;
            var rva = method.RelativeVirtualAddress;

            Console.WriteLine($"Method targeted by call from add: {fieldname} with flags {fieldFlags}  (RVA: {rva})");

        }
    }
}
