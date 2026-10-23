using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace ILStrip;

/// <summary>
/// Dynamically loads a candidate or baseline assembly stripping library at runtime
/// and binds its stripping entry point to a high-performance delegate.
/// </summary>
public sealed class StripperInvoker
{
    private readonly Action<string, string> _stripAction;

    public string AssemblyPath { get; }
    public string Name { get; }
    public string EntryPointDisplay { get; }

    private StripperInvoker(string assemblyPath, string name, string entryPointDisplay, Action<string, string> stripAction)
    {
        AssemblyPath = assemblyPath;
        Name = name;
        EntryPointDisplay = entryPointDisplay;
        _stripAction = stripAction;
    }

    /// <summary>
    /// Executes the stripper on the specified input assembly and writes the output.
    /// </summary>
    public void Invoke(string inputPath, string outputPath)
    {
        _stripAction(inputPath, outputPath);
    }

    /// <summary>
    /// Dynamically loads a stripper assembly from the given path.
    /// Supports:
    /// 1. Static methods: StripAssembly(string, string), Strip(string, string), Execute(string, string)
    /// 2. Any public static method taking (string, string) returning void or bool
    /// 3. Static Main(string[] args)
    /// </summary>
    public static StripperInvoker Load(string assemblyPath, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
            throw new ArgumentException("Assembly path must not be null or empty.", nameof(assemblyPath));

        string fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Stripper assembly not found at: '{fullPath}'", fullPath);

        string assemblyDir = Path.GetDirectoryName(fullPath)!;
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string candidate = Path.Combine(assemblyDir, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };

        // Load assembly using default load context so shared framework types resolve smoothly
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        string name = displayName ?? Path.GetFileNameWithoutExtension(fullPath);

        // Candidate search: Look for known method names first
        var candidateNames = new[] { "StripAssembly", "Strip", "Execute", "Run" };
        var publicTypes = assembly.GetExportedTypes();

        foreach (var methodName in candidateNames)
        {
            foreach (var type in publicTypes)
            {
                var method = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string), typeof(string) },
                    modifiers: null);

                if (method != null)
                {
                    Action<string, string> action = CreateActionFromTwoParamMethod(method);
                    return new StripperInvoker(fullPath, name, $"{type.FullName}.{method.Name}", action);
                }
            }
        }

        // Next: Look for ANY public static method taking two strings and returning void or bool
        foreach (var type in publicTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(string) &&
                    (method.ReturnType == typeof(void) || method.ReturnType == typeof(bool)))
                {
                    Action<string, string> action = CreateActionFromTwoParamMethod(method);
                    return new StripperInvoker(fullPath, name, $"{type.FullName}.{method.Name}", action);
                }
            }
        }

        // Next: Check for CLI Main(string[] args)
        var entryPoint = assembly.EntryPoint;
        if (entryPoint != null)
        {
            var prms = entryPoint.GetParameters();
            if (prms.Length == 1 && prms[0].ParameterType == typeof(string[]))
            {
                Action<string, string> action = (inPath, outPath) =>
                {
                    object[] args = new object[] { new string[] { inPath, outPath } };
                    entryPoint.Invoke(null, args);
                };
                return new StripperInvoker(fullPath, name, $"{entryPoint.DeclaringType?.FullName}.{entryPoint.Name}(string[])", action);
            }
        }

        throw new InvalidOperationException(
            $"Could not find a valid stripping entry point in '{fullPath}'. " +
            $"Expected a public static method taking (string inputPath, string outputPath), e.g., StripAssembly or Strip.");
    }

    private static Action<string, string> CreateActionFromTwoParamMethod(MethodInfo method)
    {
        if (method.ReturnType == typeof(void))
        {
            return (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), method);
        }
        else if (method.ReturnType == typeof(bool))
        {
            var func = (Func<string, string, bool>)Delegate.CreateDelegate(typeof(Func<string, string, bool>), method);
            return (inPath, outPath) =>
            {
                bool success = func(inPath, outPath);
                if (!success)
                    throw new InvalidOperationException($"Stripper '{method.Name}' returned false for input '{inPath}'.");
            };
        }
        else
        {
            return (inPath, outPath) => method.Invoke(null, new object[] { inPath, outPath });
        }
    }
}
