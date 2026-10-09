using Mono.Cecil;

namespace Reading
{
    internal class Program
    {
        private static string assemblyPath = "C:\\Users\\t-fhanzlik\\OneDrive - Microsoft\\Documents\\projects\\IL rewrite\\docs\\learning-exploring\\dll-inspection\\bin\\Release\\net10.0\\DllExplorerSample.dll";
        static void Main(string[] args)
        {
            var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        }
    }
}

