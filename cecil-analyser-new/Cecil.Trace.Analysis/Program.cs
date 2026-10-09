using System;
using System.IO;
using System.Text.Json;

namespace Cecil.Trace.Analysis {

	internal static class Program {
		static int Main (string [] args)
		{
			if (args.Length == 1 && args [0] == "--help") {
				Console.WriteLine ("Cecil.Trace.Analysis analyze <trace.jsonl> [--output <new-report.json>]");
				Console.WriteLine ("Validates and summarizes an ordered trace. Incomplete/invalid traces return exit code 1. This is not a universal replay executor.");
				return 0;
			}
			try {
				if ((args.Length != 2 && args.Length != 4) || args [0] != "analyze" || (args.Length == 4 && args [2] != "--output"))
					throw new ArgumentException ("Expected analyze <trace.jsonl> [--output <new-report.json>].");
				using var input = File.OpenText (args [1]);
				var report = TraceAnalyzer.Analyze (input);
				if (args.Length == 4) {
					using var output = new FileStream (args [3], FileMode.CreateNew, FileAccess.Write);
					JsonSerializer.Serialize (output, report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
				}
				Console.WriteLine ($"Trace: {(report.IsComplete ? "complete" : "INVALID/INCOMPLETE")}; calls: {report.Calls}; threads: {report.Threads}");
				foreach (var origin in report.Categories)
					foreach (var category in origin.Value)
						Console.WriteLine ($"  {origin.Key,-8} {category.Key,-12} entered={category.Value.Entered} returned={category.Value.Returned} threw={category.Value.Threw}");
				Console.WriteLine ("External read/write call ratio: " + (report.ExternalReadWriteRatio?.ToString ("G6", System.Globalization.CultureInfo.InvariantCulture) ?? "not defined (no writes)"));
				Console.WriteLine ($"Completed body decodes: {report.CompletedBodyDecodes}; declared IL bytes: {report.DeclaredIlBytes}; decoded instructions: {report.DecodedInstructions}");
				Console.WriteLine ($"Reconstruction issue groups: {report.Issues.Count}. Categories are heuristic-v1; a complete trace is not a guarantee of replayability.");
				foreach (var error in report.Errors) Console.Error.WriteLine (error);
				return report.IsComplete ? 0 : 1;
			} catch (ArgumentException error) {
				Console.Error.WriteLine (error.Message);
				return 2;
			} catch (IOException error) {
				Console.Error.WriteLine (error.Message);
				return 1;
			} catch (UnauthorizedAccessException error) {
				Console.Error.WriteLine (error.Message);
				return 1;
			}
		}
	}
}
