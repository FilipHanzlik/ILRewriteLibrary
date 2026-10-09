using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace CecilCalculatorDemo;

internal static class DemoVerification
{
    public static void Verify(string inputPath, string outputPath, int bonus, string greeting)
    {
        var original = Observe(inputPath);
        var rewritten = Observe(outputPath);

        Require(original.Sum == 5, "The original Add(2, 3) must return 5.");
        Require(rewritten.Sum == original.Sum + bonus, "The rewritten addition must include the bonus.");
        Require(original.CallCount == 1 && rewritten.CallCount == 1, "Addition must still increment CallCount once.");
        Require(original.ExplorationAttributes == 1 && rewritten.ExplorationAttributes == 0,
            "Only the rewritten Add method must lose its ExplorationAttribute.");
        Require(original.Greeting == "Hello from the DLL" && rewritten.Greeting == greeting,
            "The rewritten greeting does not match the intended message.");
        Require(original.Quotient == 5 && rewritten.Quotient == 5, "Ordinary division must remain unchanged.");
        Require(original.DivideByZero == -1 && rewritten.DivideByZero == -1, "The exception handler must remain intact.");
        Require(original.Twice == 14 && rewritten.Twice == 14, "The nested helper must remain unchanged.");

        Console.WriteLine();
        Console.WriteLine($"Add(2, 3): {original.Sum} -> {rewritten.Sum}");
        Console.WriteLine($"CallCount after Add: {original.CallCount} -> {rewritten.CallCount}");
        Console.WriteLine($"Greeting: \"{original.Greeting}\" -> \"{rewritten.Greeting}\"");
        Console.WriteLine($"ExplorationAttribute count on Add: {original.ExplorationAttributes} -> {rewritten.ExplorationAttributes}");
        Console.WriteLine("Unchanged: division, divide-by-zero handling, and the nested helper used by Twice.");
    }

    private static Observation Observe(string path)
    {
        // Separate contexts let the original and rewritten assemblies keep the same assembly identity.
        var context = new AssemblyLoadContext(Path.GetFileName(path), isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            var type = assembly.GetType("DllExplorerSample.Calculator")
                ?? throw new TypeLoadException("The calculator type is missing.");
            var calculator = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not create the calculator.");
            var add = GetMethod(type, "Add");
            var sum = InvokeInt(add, calculator, 2, 3);

            if (type.GetProperty("CallCount")?.GetValue(calculator) is not int callCount)
                throw new InvalidOperationException("Could not read the calculator's CallCount.");

            using var text = new StringWriter(CultureInfo.InvariantCulture);
            var previousOutput = Console.Out;
            try
            {
                Console.SetOut(text);
                GetMethod(type, "PrintGreeting").Invoke(calculator, null);
            }
            finally
            {
                Console.SetOut(previousOutput);
            }

            var divide = GetMethod(type, "DivideOrDefault");
            return new Observation(
                sum,
                callCount,
                text.ToString().TrimEnd('\r', '\n'),
                add.GetCustomAttributesData().Count(attribute =>
                    attribute.AttributeType.FullName == "DllExplorerSample.ExplorationAttribute"),
                InvokeInt(divide, calculator, 10, 2),
                InvokeInt(divide, calculator, 10, 0),
                InvokeInt(GetMethod(type, "Twice"), null, 7));
        }
        finally
        {
            context.Unload();
        }
    }

    private static MethodInfo GetMethod(Type type, string name)
    {
        return type.GetMethod(name) ?? throw new MissingMethodException(type.FullName, name);
    }

    private static int InvokeInt(MethodInfo method, object? receiver, params object[] arguments)
    {
        return method.Invoke(receiver, arguments) is int result
            ? result
            : throw new InvalidOperationException($"{method.Name} did not return an integer.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Observation(
        int Sum,
        int CallCount,
        string Greeting,
        int ExplorationAttributes,
        int Quotient,
        int DivideByZero,
        int Twice);
}
