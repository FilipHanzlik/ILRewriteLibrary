using System;
using System.IO;

namespace Cecil.Trace.Weaver {

	internal static class Program {
		static int Main (string [] args)
		{
			if (args.Length == 1 && args [0] == "--help") {
				Console.WriteLine ("Cecil.Trace.Weaver <input.dll> <output.dll> [--key <cecil.snk>] [--core <diagnostic Mono.Cecil.dll>]");
				Console.WriteLine ("Creates a separate signed instrumented assembly and <output.dll>.coverage.json. Existing files are never overwritten.");
				return 0;
			}
			try {
				if (args.Length < 2)
					throw new ArgumentException ("Expected input and output DLL paths. Use --help.");
				string key = null, core = null;
				for (int i = 2; i < args.Length; i += 2) {
					if (i + 1 == args.Length)
						throw new ArgumentException ("Missing value for " + args [i]);
					switch (args [i]) {
					case "--key": key = args [i + 1]; break;
					case "--core": core = args [i + 1]; break;
					default: throw new ArgumentException ("Unknown option " + args [i]);
					}
				}
				var report = TraceWeaver.Weave (args [0], args [1], key, core);
				Console.WriteLine ($"Instrumented {report.InstrumentedMethods} of {report.Methods.Count} methods; coverage: {Path.GetFullPath (args [1])}.coverage.json");
				return 0;
			} catch (ArgumentException error) {
				Console.Error.WriteLine (error.Message);
				return 2;
			} catch (IOException error) {
				Console.Error.WriteLine (error.Message);
				return 1;
			} catch (UnauthorizedAccessException error) {
				Console.Error.WriteLine (error.Message);
				return 1;
			} catch (InvalidOperationException error) {
				Console.Error.WriteLine (error.Message);
				return 1;
			} catch (NotSupportedException error) {
				Console.Error.WriteLine (error.Message);
				return 1;
			}
		}
	}
}
