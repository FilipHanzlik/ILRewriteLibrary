using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cecil.Trace.Analysis;
using Cecil.Trace.Weaver;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using NUnit.Framework;

namespace Cecil.Trace.Tests {

	[TestFixture]
	public class TraceTests {
		string root, staging, original, woven, driver_directory;
		CoverageReport coverage;

		[OneTimeSetUp]
		public void BuildInstrumentedFixture ()
		{
			var directory = new DirectoryInfo (TestContext.CurrentContext.TestDirectory);
			string framework = directory.Name, configuration = directory.Parent.Name;
			while (directory != null && !File.Exists (Path.Combine (directory.FullName, "Mono.Cecil.csproj"))) directory = directory.Parent;
			Assert.That (directory, Is.Not.Null, "Cannot locate Cecil source root.");
			root = directory.FullName;
			staging = Path.Combine (root, "artifacts", "trace-tests", Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (staging);
			original = typeof (ModuleDefinition).Assembly.Location;
			woven = Path.Combine (staging, "Mono.Cecil.dll");
			driver_directory = Path.Combine (root, "artifacts", "bin", "Cecil.Trace.Workload", configuration, framework);
			coverage = TraceWeaver.Weave (original, woven, Path.Combine (root, "cecil.snk"));
			TestContext.Progress.WriteLine ("Trace fixture: " + staging);
		}

		[OneTimeTearDown]
		public void RemoveFixture ()
		{
			if (staging != null && Directory.Exists (staging) && TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed)
				Directory.Delete (staging, true);
		}

		[Test]
		public void ReadWriteMatchesUninstrumentedWithTracingOnAndOff ()
		{
			var baseline = Run ("readwrite", original, false);
			var disabled = Run ("readwrite", woven, false);
			var enabled = Run ("readwrite", woven, true);
			foreach (var execution in new [] { baseline, disabled, enabled }) {
				Assert.That (execution.Result.GetProperty ("answer").GetInt32 (), Is.EqualTo (42));
				Assert.That (execution.Result.GetProperty ("unreadBodyStillDeferred").GetBoolean (), Is.True);
				Assert.That (execution.Result.GetProperty ("outputSha256").GetString (), Is.EqualTo (baseline.Result.GetProperty ("outputSha256").GetString ()));
			}
			Assert.That (File.Exists (baseline.Log), Is.False);
			Assert.That (File.Exists (disabled.Log), Is.False);
			var report = Analyze (enabled.Log);
			Assert.That (report.CompletedBodyDecodes, Is.EqualTo (1));
			Assert.That (report.DecodedInstructions, Is.EqualTo (5));
			Assert.That (report.BodyAccesses ["external"].CachedAtEntry, Is.GreaterThan (0));
			Assert.That (report.BodyAccesses ["external"].NotCachedAtEntry, Is.GreaterThan (0));
			Assert.That (report.Categories ["external"].ContainsKey ("write"), Is.True);
			Assert.That (report.Categories ["internal"].ContainsKey ("read"), Is.True);
			Assert.That (report.ExternalReadWriteRatio, Is.Not.Null);
			Assert.That (report.Methods.Any (m => m.Name == "TryGetTypeReference" && m.Counts.ContainsKey ("external")), Is.True);
			var records = Records (enabled.Log);
			Assert.That (records [0].GetProperty ("assembly").GetProperty ("path").GetString (), Is.EqualTo (enabled.Result.GetProperty ("loadedCecil").GetString ()));
			Assert.That (records [0].GetProperty ("assembly").GetProperty ("sha256").GetString (), Is.EqualTo (enabled.Result.GetProperty ("loadedCecilSha256").GetString ()));
		}

		[Test]
		public void ValuesGenericsFaultRecoveryAndBoundedReplay ()
		{
			var execution = Run ("values", woven, true);
			var report = Analyze (execution.Log);
			Assert.That (execution.Result.GetProperty ("caughtExpectedFault").GetBoolean (), Is.True);
			Assert.That (execution.Result.GetProperty ("sum").GetInt32 (), Is.EqualTo (8));
			Assert.That (execution.Result.GetProperty ("upperBound").GetInt32 (), Is.EqualTo (5));
			Assert.That (report.Methods.Any (m => m.Counts.Values.Any (c => c.Threw > 0)), Is.True);
			var records = Records (execution.Log);
			var methods = records.Where (r => r.GetProperty ("event").GetString () == "method").ToDictionary (r => r.GetProperty ("methodId").GetInt64 ());
			var calls = records.Where (r => r.GetProperty ("event").GetString () == "enter").ToDictionary (r => r.GetProperty ("callId").GetInt64 ());
			var objects = new Dictionary<long, object> ();
			int reconstructed = 0;
			bool removed = false, special_string = false, nan = false, negative_zero = false, aliases = false;
			foreach (var leave in records.Where (r => r.GetProperty ("event").GetString () == "leave" && r.GetProperty ("status").GetString () == "returned")) {
				var enter = calls [leave.GetProperty ("callId").GetInt64 ()];
				if (enter.GetProperty ("origin").GetString () != "external") continue;
				var method = methods [enter.GetProperty ("methodId").GetInt64 ()];
				var type = method.GetProperty ("declaringType").GetString ();
				string name = method.GetProperty ("name").GetString ();
				var arguments = enter.GetProperty ("arguments");
				if (type.StartsWith ("Mono.Collections.Generic.Collection`1[[Mono.Cecil.Cil.Instruction,", StringComparison.Ordinal)) {
					if (name == ".ctor") {
						objects [leave.GetProperty ("receiver").GetProperty ("id").GetInt64 ()] = new Collection<Instruction> ();
						reconstructed++;
					} else if (name == "Add" || name == "Remove") {
						var collection = (Collection<Instruction>) objects [enter.GetProperty ("receiver").GetProperty ("id").GetInt64 ()];
						var item = (Instruction) objects [arguments [0].GetProperty ("id").GetInt64 ()];
						if (name == "Add") collection.Add (item);
						else {
							removed = collection.Remove (item);
							Assert.That (removed, Is.EqualTo (leave.GetProperty ("result").GetProperty ("value").GetBoolean ()));
						}
						reconstructed++;
					}
				} else if (type.StartsWith ("Mono.Cecil.Cil.Instruction,", StringComparison.Ordinal)) {
					if (name == "Create" && arguments.GetArrayLength () == 2) {
						var opcode = (OpCode) Decode (arguments [0], objects);
						var argument = Decode (arguments [1], objects);
						Instruction value;
						if (argument is int integer) value = Instruction.Create (opcode, integer);
						else if (argument is string text) {
							value = Instruction.Create (opcode, text);
							special_string = text == "quote\"\\\r\n\u03a9\ud800";
						} else if (argument is double number) {
							value = Instruction.Create (opcode, number);
							nan = BitConverter.DoubleToInt64Bits (number) == unchecked ((long) 0xfff8000000000042);
						} else if (argument is float single) {
							value = Instruction.Create (opcode, single);
							negative_zero = BitConverter.SingleToInt32Bits (single) == int.MinValue;
						} else if (argument is Instruction [] targets) {
							value = Instruction.Create (opcode, targets);
							aliases = ReferenceEquals (targets [0], targets [1]);
						} else throw new InvalidOperationException ("Unexpected replay value type.");
						objects [leave.GetProperty ("result").GetProperty ("id").GetInt64 ()] = value;
						reconstructed++;
					} else if (name == "set_Operand") {
						((Instruction) objects [enter.GetProperty ("receiver").GetProperty ("id").GetInt64 ()]).Operand = Decode (arguments [0], objects);
						reconstructed++;
					}
				}
			}
			var replayed_collection = objects.Values.OfType<Collection<Instruction>> ().Single ();
			Assert.That (reconstructed, Is.GreaterThanOrEqualTo (9));
			Assert.That (removed && aliases && special_string && nan && negative_zero, Is.True);
			Assert.That (replayed_collection.Count, Is.EqualTo (1));
			Assert.That (replayed_collection [0].Operand, Is.EqualTo (37));
		}

		[Test]
		public void CallbacksThreadsOpaqueValuesAndSnapshotLimitsAreExplicit ()
		{
			var execution = Run ("edges", woven, true);
			var report = Analyze (execution.Log);
			Assert.That (execution.Result.GetProperty ("opaqueCollected").GetBoolean (), Is.True, "Recorder retained an observed object.");
			Assert.That (execution.Result.GetProperty ("threadSum").GetInt32 (), Is.EqualTo (6));
			Assert.That (execution.Result.GetProperty ("callbackCount").GetInt32 (), Is.EqualTo (1));
			Assert.That (report.Threads, Is.GreaterThanOrEqualTo (4));
			foreach (string reason in new [] { "callback-reentry-requires-driver", "object-has-no-external-producer", "snapshot-string-limit", "snapshot-byte-limit", "stream-needs-adapter" })
				Assert.That (report.Issues.Any (i => i.Origin == "external" && i.Reason == reason), Is.True, reason);
			var records = Records (execution.Log);
			Assert.That (records.Any (r => r.GetProperty ("event").GetString () == "enter" &&
				r.GetProperty ("origin").GetString () == "external" &&
				r.GetProperty ("parentCallId").ValueKind == JsonValueKind.Number &&
				r.GetProperty ("caller").GetString ().Contains ("CallbackResolver")), Is.True);
		}

		[Test]
		public void CoveragePreservesSignaturesAndRefusesOverwriteAndDoubleWeaving ()
		{
			Assert.That (coverage.InstrumentedMethods, Is.GreaterThan (3000));
			Assert.That (coverage.ExposedFields.Any (f => f.Contains ("OpCodes::Nop")), Is.True);
			Assert.That (coverage.Methods.Where (m => m.Status == "skipped").All (m => m.Reason == "no-managed-body" || m.Reason == "recorder-excluded-to-prevent-recursion"), Is.True);
			using var input = ModuleDefinition.ReadModule (original);
			using var output = ModuleDefinition.ReadModule (woven, new ReaderParameters { ReadSymbols = true });
			Assert.That (output.HasSymbols, Is.True);
			Assert.That (input.Assembly.Name.FullName, Is.EqualTo (output.Assembly.Name.FullName));
			Assert.That (output.GetTypes ().SelectMany (t => t.Methods).Select (m => m.FullName), Is.EqualTo (input.GetTypes ().SelectMany (t => t.Methods).Select (m => m.FullName)));
			Assert.Throws<IOException> (() => TraceWeaver.Weave (original, woven, Path.Combine (root, "cecil.snk")));
			Assert.Throws<InvalidOperationException> (() => TraceWeaver.Weave (woven, Path.Combine (staging, "twice.dll"), Path.Combine (root, "cecil.snk")));
			Assert.Throws<ArgumentException> (() => TraceWeaver.Weave (original, Path.Combine (staging, "unsigned.dll")));
		}

		[Test]
		public void CompanionAssembliesShareOneRecorder ()
		{
			var companions = new Dictionary<string, string> ();
			foreach (string name in new [] { "Mono.Cecil.Rocks", "Mono.Cecil.Pdb", "Mono.Cecil.Mdb" }) {
				string destination = Path.Combine (staging, name + ".dll");
				var report = TraceWeaver.Weave (Path.Combine (driver_directory, name + ".dll"), destination, Path.Combine (root, "cecil.snk"), original);
				Assert.That (report.InstrumentedMethods, Is.GreaterThan (0), name);
				Assert.That (report.SymbolsPreserved, Is.True, name);
				companions.Add (name + ".dll", destination);
			}
			var baseline = Run ("companions", original, false);
			var enabled = Run ("companions", woven, true, companions);
			foreach (string metric in new [] { "allTypes", "portableSequencePoints", "portableBytes", "mdbSequencePoints" })
				Assert.That (enabled.Result.GetProperty (metric).GetInt64 (), Is.EqualTo (baseline.Result.GetProperty (metric).GetInt64 ()), metric);
			Assert.That (enabled.Result.GetProperty ("portableSequencePoints").GetInt32 (), Is.EqualTo (1));
			Assert.That (enabled.Result.GetProperty ("mdbSequencePoints").GetInt32 (), Is.EqualTo (1));
			var analysis = Analyze (enabled.Log);
			foreach (string name in new [] { "Mono.Cecil.Rocks", "Mono.Cecil.Pdb", "Mono.Cecil.Mdb" })
				Assert.That (analysis.Methods.Any (m => m.DeclaringType.Contains (name + ",") && m.Counts.Count > 0), Is.True, name);
			var records = Records (enabled.Log);
			Assert.That (records.Count (r => r.GetProperty ("event").GetString () == "session"), Is.EqualTo (1));
			Assert.That (records.Count (r => r.GetProperty ("event").GetString () == "session-end"), Is.EqualTo (1));
		}

		[Test]
		public void AnalyzerRejectsIncompleteMalformedAndFaultedLogs ()
		{
			var execution = Run ("values", woven, true);
			var lines = File.ReadAllLines (execution.Log);
			Assert.That (TraceAnalyzer.Analyze (new StringReader (string.Join ("\n", lines.Take (lines.Length - 1)))).IsComplete, Is.False);
			Assert.That (TraceAnalyzer.Analyze (new StringReader ("{\"event\":")).IsComplete, Is.False);
			var footer = JsonNode.Parse (lines [lines.Length - 1]);
			footer ["recorderFailed"] = true;
			var corrupt = lines.Take (lines.Length - 1).Append (footer.ToJsonString ());
			Assert.That (TraceAnalyzer.Analyze (new StringReader (string.Join ("\n", corrupt))).Errors.Any (e => e.Contains ("capture/write")), Is.True);
			corrupt = lines.Take (2).Concat (lines.Skip (3));
			Assert.That (TraceAnalyzer.Analyze (new StringReader (string.Join ("\n", corrupt))).Errors.Any (e => e.Contains ("Sequence")), Is.True);
			int entry_index = Array.FindIndex (lines, text => {
				using var document = JsonDocument.Parse (text);
				var record = document.RootElement;
				return record.GetProperty ("event").GetString () == "enter" && record.GetProperty ("arguments").GetArrayLength () > 0;
			});
			foreach (var bad in new [] {
				new JsonObject { ["kind"] = "reference", ["reconstruction"] = "reference", ["id"] = "not-an-integer" },
				new JsonObject { ["kind"] = "unavailable", ["reconstruction"] = "unavailable" },
				new JsonObject { ["kind"] = "reference", ["reconstruction"] = "reference", ["id"] = 1, ["sourceCallId"] = long.MaxValue, ["sourcePath"] = "result" }
			}) {
				var record = JsonNode.Parse (lines [entry_index]);
				record ["arguments"] [0] = bad;
				var malformed = (string []) lines.Clone ();
				malformed [entry_index] = record.ToJsonString ();
				Assert.That (TraceAnalyzer.Analyze (new StringReader (string.Join ("\n", malformed))).IsComplete, Is.False);
			}
		}

		[Test]
		public void ArraysPreserveShapeCyclesAndExplicitDepthGaps ()
		{
			var execution = Run ("shapes", woven, true);
			Analyze (execution.Log);
			Assert.That (execution.Result.GetProperty ("cyclePreserved").GetBoolean (), Is.True);
			var snapshots = Records (execution.Log).Where (r => r.GetProperty ("event").GetString () == "enter" && r.GetProperty ("origin").GetString () == "external")
				.SelectMany (r => r.GetProperty ("arguments").EnumerateArray ()).ToArray ();
			var matrix = snapshots.Single (s => s.GetProperty ("kind").GetString () == "array" && s.GetProperty ("lengths").GetArrayLength () == 2);
			Assert.That (matrix.GetProperty ("lowerBounds").EnumerateArray ().Select (n => n.GetInt32 ()), Is.EqualTo (new [] { -1, 3 }));
			Assert.That (matrix.GetProperty ("items").EnumerateArray ().Select (n => n.GetProperty ("value").GetString ()), Is.EqualTo (new [] { "7", "0", "0", "9" }));
			var cycle = snapshots.First (s => s.GetProperty ("kind").GetString () == "array" && s.GetProperty ("items") [0].TryGetProperty ("sameSnapshot", out _));
			Assert.That (cycle.GetProperty ("id").GetInt64 (), Is.EqualTo (cycle.GetProperty ("items") [0].GetProperty ("id").GetInt64 ()));
			var limited = Run ("shapes", woven, true, limits: new Dictionary<string, string> { ["CECIL_TRACE_MAX_DEPTH"] = "1" });
			var report = Analyze (limited.Log);
			Assert.That (report.Issues.Any (i => i.Origin == "external" && i.Reason == "snapshot-depth-limit"), Is.True);
			Assert.That (Records (limited.Log).Any (r => r.GetProperty ("event").GetString () == "leave" &&
				r.TryGetProperty ("result", out var result) && result.GetProperty ("kind").GetString () == "array" &&
				result.TryGetProperty ("items", out var items) && items.EnumerateArray ().Any (item =>
					item.TryGetProperty ("sourceCallId", out _) && item.GetProperty ("reconstruction").GetString () == "unavailable" &&
					item.GetProperty ("reason").GetString () == "snapshot-depth-limit")), Is.True, "A producer must not hide missing mutable array state.");
		}

		[Test]
		public void RecorderNeverOverwritesAnExistingLog ()
		{
			var execution = Run ("values", woven, true);
			byte [] before = File.ReadAllBytes (execution.Log);
			var second = Start (execution.Directory, "values", execution.Log);
			Assert.That (second.ExitCode, Is.Not.EqualTo (0));
			Assert.That (second.Error, Does.Contain ("already exists"));
			Assert.That (File.ReadAllBytes (execution.Log), Is.EqualTo (before));
		}

		[Test]
		public void LaterProcessExitHandlersRemainObservableWithoutBreakingShutdown ()
		{
			var execution = Run ("shutdown", woven, true);
			Analyze (execution.Log);
			var records = Records (execution.Log);
			Assert.That (records.Count (r => r.GetProperty ("event").GetString () == "shutdown-start"), Is.EqualTo (1));
			Assert.That (records.Count (r => r.GetProperty ("event").GetString () == "session-end"), Is.GreaterThan (1));
			Assert.That (records.Any (r => r.GetProperty ("event").GetString () == "shutdown-continued"), Is.True);
			Assert.That (records.Last ().GetProperty ("event").GetString (), Is.EqualTo ("session-end"));
			var lines = File.ReadAllLines (execution.Log);
			int continuation = Array.FindIndex (lines, line => line.Contains ("\"event\":\"shutdown-continued\""));
			Assert.That (TraceAnalyzer.Analyze (new StringReader (string.Join ("\n", lines.Take (continuation + 1)))).IsComplete, Is.False);
		}

		sealed class Execution {
			internal string Directory, Log;
			internal JsonElement Result;
		}
		sealed class ProcessResult {
			internal int ExitCode;
			internal string Output, Error;
		}

		Execution Run (string scenario, string cecil, bool enabled, Dictionary<string, string> companions = null, Dictionary<string, string> limits = null)
		{
			string directory = Path.Combine (staging, Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			foreach (var file in Directory.GetFiles (driver_directory)) {
				string name = Path.GetFileName (file);
				if (name == "Mono.Cecil.dll" || name == "Mono.Cecil.pdb") continue;
				File.Copy (companions != null && companions.TryGetValue (name, out var replacement) ? replacement : file, Path.Combine (directory, name));
			}
			File.Copy (cecil, Path.Combine (directory, "Mono.Cecil.dll"));
			string log = Path.Combine (directory, "calls.jsonl");
			var process = Start (directory, scenario, enabled ? log : null, limits);
			Assert.That (process.ExitCode, Is.EqualTo (0), process.Error + "\n" + process.Output);
			using var json = JsonDocument.Parse (process.Output.Trim ());
			return new Execution { Directory = directory, Log = log, Result = json.RootElement.Clone () };
		}

		ProcessResult Start (string directory, string scenario, string trace, Dictionary<string, string> limits = null)
		{
			var start = new ProcessStartInfo (Path.Combine (root, ".dotnet", OperatingSystem.IsWindows () ? "dotnet.exe" : "dotnet")) {
				WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
			};
			start.ArgumentList.Add (Path.Combine (directory, "Cecil.Trace.Workload.dll"));
			start.ArgumentList.Add ("--scenario"); start.ArgumentList.Add (scenario);
			start.ArgumentList.Add ("--output"); start.ArgumentList.Add (Path.Combine (directory, "sample.dll"));
			start.Environment.Remove ("CECIL_TRACE_FILE");
			if (trace != null) start.Environment ["CECIL_TRACE_FILE"] = trace;
			foreach (var key in start.Environment.Keys.Where (k => k.StartsWith ("CECIL_TRACE_MAX_", StringComparison.Ordinal)).ToArray ()) start.Environment.Remove (key);
			if (limits != null)
				foreach (var limit in limits) start.Environment [limit.Key] = limit.Value;
			using var process = Process.Start (start);
			var output = process.StandardOutput.ReadToEndAsync ();
			var error = process.StandardError.ReadToEndAsync ();
			if (!process.WaitForExit (120000)) {
				process.Kill (true);
				Assert.Fail ("Diagnostic workload timed out.");
			}
			return new ProcessResult { ExitCode = process.ExitCode, Output = output.GetAwaiter ().GetResult (), Error = error.GetAwaiter ().GetResult () };
		}

		static AnalysisReport Analyze (string path)
		{
			using var reader = File.OpenText (path);
			var report = TraceAnalyzer.Analyze (reader);
			Assert.That (report.IsComplete, Is.True, string.Join ("\n", report.Errors));
			return report;
		}
		static JsonElement [] Records (string path)
		{
			return File.ReadLines (path).Select (line => {
				using var json = JsonDocument.Parse (line);
				return json.RootElement.Clone ();
			}).ToArray ();
		}

		static object Decode (JsonElement value, Dictionary<long, object> objects)
		{
			string kind = value.GetProperty ("kind").GetString ();
			if (kind == "null") return null;
			if (kind == "reference") return objects [value.GetProperty ("id").GetInt64 ()];
			var type = Type.GetType (value.GetProperty ("type").GetString (), throwOnError: true);
			if (kind == "value") {
				object boxed = Activator.CreateInstance (type);
				foreach (var field in value.GetProperty ("fields").EnumerateArray ())
					type.GetField (field.GetProperty ("name").GetString (), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue (boxed, Decode (field.GetProperty ("value"), objects));
				return boxed;
			}
			if (kind == "array") {
				var items = value.GetProperty ("items").EnumerateArray ().ToArray ();
				var array = Array.CreateInstance (type.GetElementType (), items.Length);
				for (int i = 0; i < items.Length; i++) array.SetValue (Decode (items [i], objects), i);
				return array;
			}
			var scalar = value.GetProperty ("value");
			if (kind == "string") {
				if (!value.TryGetProperty ("encoding", out _)) return scalar.GetString ();
				byte [] bytes = Convert.FromBase64String (scalar.GetString ());
				var chars = new char [bytes.Length / 2];
				for (int i = 0; i < chars.Length; i++) chars [i] = (char) (bytes [2 * i] | bytes [2 * i + 1] << 8);
				return new string (chars);
			}
			if (type == typeof (double)) return BitConverter.ToDouble (Convert.FromBase64String (scalar.GetString ()));
			if (type == typeof (float)) return BitConverter.ToSingle (Convert.FromBase64String (scalar.GetString ()));
			if (type == typeof (bool)) return scalar.GetBoolean ();
			if (type.IsEnum) return Enum.Parse (type, scalar.GetString ());
			return Convert.ChangeType (scalar.GetString (), type, System.Globalization.CultureInfo.InvariantCulture);
		}
	}
}
