using System;
using System.Reflection;

[assembly: AssemblyMetadata("Purpose", "DLL exploration")]

namespace DllExplorerSample;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ExplorationAttribute : Attribute
{
    public ExplorationAttribute(string note)
    {
        Note = note;
    }

    public string Note { get; }

    public int Revision { get; set; }
}

[Exploration("A type to inspect", Revision = 1)]
public class Calculator
{
    public const int DefaultDivisor = 2;

    private int _callCount;

    public int CallCount => _callCount;

    public string Label { get; set; } = "Demo calculator";

    [Exploration("Adds two integers", Revision = 2)]
    public int Add(int left, int right)
    {
        CountCall();
        return left + right;
    }

    public int DivideOrDefault(int dividend, int divisor)
    {
        try
        {
            return dividend / divisor;
        }
        catch (DivideByZeroException)
        {
            return -1;
        }
    }

    public void PrintGreeting()
    {
        Console.WriteLine("Hello from the DLL");
    }

    public static T Identity<T>(T value)
    {
        return value;
    }

    public static int Twice(int value)
    {
        return HiddenHelper.Double(value);
    }

    private void CountCall()
    {
        _callCount++;
    }

    private sealed class HiddenHelper
    {
        public static int Double(int value)
        {
            return value * 2;
        }
    }
}
