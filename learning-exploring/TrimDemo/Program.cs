using DllExplorerSample;

namespace TrimDemo
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var calculator = new Calculator();
            Console.WriteLine(calculator.Add(2, 3));
        }
    }
}
