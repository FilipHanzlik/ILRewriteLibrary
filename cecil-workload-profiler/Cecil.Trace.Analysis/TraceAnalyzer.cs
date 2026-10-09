using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cecil.Trace.Analysis {

	public sealed class CallCounts {
		public long Entered { get; set; }
		public long Returned { get; set; }
		public long Threw { get; set; }
	}

	public sealed class MethodSummary {
		public long MethodId { get; set; }
		public string Name { get; set; }
		public string DeclaringType { get; set; }
		public string Signature { get; set; }
		public string Category { get; set; }
		public string Classification { get; set; }
		public Dictionary<string, CallCounts> Counts { get; } = new Dictionary<string, CallCounts> ();
		internal bool Constructor;
		internal bool Void;
		internal bool [] OutParameters;
	}

	public sealed class IssueSummary {
		public string Origin { get; set; }
		public string Reason { get; set; }
		public long AffectedCalls { get; set; }
		public List<string> Examples { get; } = new List<string> ();
	}

	public sealed class BodyAccessSummary {
		public long CachedAtEntry { get; set; }
		public long NotCachedAtEntry { get; set; }
	}

	public sealed class AnalysisReport {
		public int SchemaVersion { get; set; } = 1;
		public bool IsComplete { get; set; }
		public long Records { get; set; }
		public long Calls { get; set; }
		public int Threads { get; set; }
		public double? ExternalReadWriteRatio { get; set; }
		public long CompletedBodyDecodes { get; set; }
		public long DeclaredIlBytes { get; set; }
		public long DecodedInstructions { get; set; }
		public JsonElement? Assembly { get; set; }
		public List<string> Errors { get; } = new List<string> ();
		public List<string> Limitations { get; } = new List<string> ();
		public Dictionary<string, Dictionary<string, CallCounts>> Categories { get; } = new Dictionary<string, Dictionary<string, CallCounts>> ();
		public Dictionary<string, BodyAccessSummary> BodyAccesses { get; } = new Dictionary<string, BodyAccessSummary> ();
		public List<MethodSummary> Methods { get; } = new List<MethodSummary> ();
		public List<IssueSummary> Issues { get; } = new List<IssueSummary> ();
	}

	public static class TraceAnalyzer {
		static readonly HashSet<string> categories = new HashSet<string> { "read", "write", "create", "resolve", "materialize", "emit", "lifetime", "other" };
		static readonly HashSet<string> origins = new HashSet<string> { "external", "internal", "unknown" };
		static readonly HashSet<string> kinds = new HashSet<string> { "null", "scalar", "string", "enum", "type", "member", "value", "array", "bytes", "reference", "unavailable" };

		public static AnalysisReport Analyze (TextReader reader)
		{
			if (reader == null) throw new ArgumentNullException (nameof (reader));
			return new State ().Read (reader);
		}

		sealed class ActiveCall {
			internal long Id;
			internal string Origin;
			internal MethodSummary Method;
			internal CallCounts CategoryCounts;
			internal CallCounts MethodCounts;
			internal readonly HashSet<string> Issues = new HashSet<string> ();
		}

		sealed class State {
			readonly AnalysisReport report = new AnalysisReport ();
			readonly Dictionary<long, MethodSummary> methods = new Dictionary<long, MethodSummary> ();
			readonly Dictionary<int, Stack<ActiveCall>> threads = new Dictionary<int, Stack<ActiveCall>> ();
			readonly HashSet<long> active = new HashSet<long> ();
			readonly Dictionary<string, IssueSummary> issues = new Dictionary<string, IssueSummary> ();
			long sequence, line;
			bool session, ended, shutdown, can_continue;

			internal AnalysisReport Read (TextReader reader)
			{
				string text;
				while ((text = reader.ReadLine ()) != null) {
					line++;
					try {
						using var json = JsonDocument.Parse (text);
						Record (json.RootElement);
						report.Records++;
					} catch (JsonException error) {
						report.Errors.Add ($"Line {line}: invalid JSON: {error.Message}");
						break;
					} catch (InvalidDataException error) {
						report.Errors.Add ($"Line {line}: {error.Message}");
						break;
					}
				}
				if (!session) report.Errors.Add ("Missing session header.");
				if (!ended) report.Errors.Add ("Missing session-end; the recording is incomplete.");
				if (active.Count != 0) report.Errors.Add ($"{active.Count} calls remain open.");
				report.IsComplete = report.Errors.Count == 0;
				report.Threads = threads.Count;
				if (threads.Count > 1) report.Limitations.Add ("Multiple threads were observed; replay requires an explicit scheduler, not just sequentially sorted call entries.");
				report.Limitations.Add ("Producer-reference validation checks temporal order and active calls, not the entire reconstructed object graph.");
				report.Limitations.Add ("Issue counts are affected calls per reason, deduplicated across entry/leave; internal value gaps are not automatically external replay blockers.");
				report.Limitations.Add ("External requests including callbacks require a driver; do not independently replay the internal calls they caused.");
				if (report.Categories.TryGetValue ("external", out var external) && external.TryGetValue ("write", out var writes) && writes.Entered != 0)
					report.ExternalReadWriteRatio = (double) (external.TryGetValue ("read", out var reads) ? reads.Entered : 0) / writes.Entered;
				return report;
			}

			void Record (JsonElement record)
			{
				Require (record.ValueKind == JsonValueKind.Object, "A record must be an object.");
				long next = Number (record, "sequence");
				Require (next == sequence + 1, "Sequence must be contiguous and strictly increasing.");
				sequence = next;
				string kind = Text (record, "event");
				if (ended) {
					Require (can_continue && shutdown && kind == "shutdown-continued", "Records found after session-end without an explicit shutdown continuation.");
					ended = false;
					can_continue = false;
					return;
				}
				if (!session) {
					Require (kind == "session" && sequence == 1, "First record must be session.");
					Require (Number (record, "schemaVersion") == 1, "Unsupported trace schema.");
					session = true;
					report.Assembly = Property (record, "assembly").Clone ();
					foreach (var limitation in Array (record, "limitations").EnumerateArray ()) report.Limitations.Add (String (limitation));
					return;
				}
				switch (kind) {
				case "method": Method (record); break;
				case "enter": Enter (record); break;
				case "leave": Leave (record); break;
				case "shutdown-start":
					Require (!shutdown, "Duplicate shutdown-start.");
					Require (Number (record, "openCalls") == active.Count && Number (record, "totalCalls") == report.Calls, "Shutdown counters do not match the trace.");
					shutdown = true;
					break;
				case "session-end":
					Require (Number (record, "openCalls") == 0 && active.Count == 0, "Session closed with open calls.");
					Require (Number (record, "totalCalls") == report.Calls, "Session call total disagrees with entries.");
					Require (!Boolean (record, "recorderFailed"), "Recorder reported a capture/write failure.");
					ended = true;
					can_continue = record.TryGetProperty ("shutdownCheckpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.True;
					break;
				default: throw new InvalidDataException ("Unexpected event: " + kind);
				}
			}

			void Method (JsonElement record)
			{
				long id = Number (record, "methodId");
				Require (id > 0 && !methods.ContainsKey (id), "Invalid or duplicate method ID.");
				string category = Text (record, "category");
				Require (categories.Contains (category), "Unknown operation category.");
				string classification = Text (record, "classification");
				Require (classification == "heuristic-v1", "Unknown classification version.");
				var return_type = Property (record, "returnType");
				var method = new MethodSummary {
					MethodId = id, Name = Text (record, "name"), DeclaringType = Text (record, "declaringType"),
					Signature = Text (record, "signature"), Category = category, Classification = classification,
					Constructor = Boolean (record, "isConstructor"),
					Void = return_type.ValueKind == JsonValueKind.Null || String (return_type) == "System.Void" || String (return_type).StartsWith ("System.Void,", StringComparison.Ordinal),
					OutParameters = Array (record, "parameters").EnumerateArray ().Select (p => Boolean (p, "out")).ToArray ()
				};
				methods.Add (id, method);
				report.Methods.Add (method);
			}

			void Enter (JsonElement record)
			{
				long id = Number (record, "callId");
				Require (id == sequence, "callId must equal its entry sequence.");
				int thread = Thread (record);
				if (!threads.TryGetValue (thread, out var stack)) threads.Add (thread, stack = new Stack<ActiveCall> ());
				var parent = Property (record, "parentCallId");
				Require (stack.Count == 0 ? parent.ValueKind == JsonValueKind.Null :
					Integer (parent, "parentCallId") == stack.Peek ().Id, "Parent call does not match this thread's stack.");
				Require (Number (record, "depth") == stack.Count, "Call depth does not match nesting.");
				Require (methods.TryGetValue (Number (record, "methodId"), out var method), "Method descriptor not defined before call.");
				string origin = Text (record, "origin");
				Require (origins.Contains (origin), "Unknown call origin.");
				if (!report.Categories.TryGetValue (origin, out var counts)) report.Categories.Add (origin, counts = new Dictionary<string, CallCounts> ());
				if (!counts.TryGetValue (method.Category, out var category)) counts.Add (method.Category, category = new CallCounts ());
				if (!method.Counts.TryGetValue (origin, out var method_counts)) method.Counts.Add (origin, method_counts = new CallCounts ());
				var call = new ActiveCall { Id = id, Origin = origin, Method = method, CategoryCounts = category, MethodCounts = method_counts };
				stack.Push (call);
				active.Add (id);
				report.Calls++;
				category.Entered++;
				method_counts.Entered++;
				if (!method.Constructor) Snapshot (Property (record, "receiver"), call, "receiver", false, null, new HashSet<long> ());
				Arguments (record, call, true);
				Issues (record, call);
				if (record.TryGetProperty ("observations", out var observations) && observations.TryGetProperty ("bodyWasCachedAtEntry", out var cached)) {
					Require (cached.ValueKind == JsonValueKind.True || cached.ValueKind == JsonValueKind.False, "Invalid body cache observation.");
					if (!report.BodyAccesses.TryGetValue (origin, out var accesses)) report.BodyAccesses.Add (origin, accesses = new BodyAccessSummary ());
					if (cached.GetBoolean ()) accesses.CachedAtEntry++; else accesses.NotCachedAtEntry++;
				}
			}

			void Leave (JsonElement record)
			{
				int thread = Thread (record);
				Require (threads.TryGetValue (thread, out var stack) && stack.Count != 0, "Leave has no matching active thread call.");
				var call = stack.Peek ();
				Require (call.Id == Number (record, "callId"), "Leave is out of nesting order.");
				string status = Text (record, "status");
				Require (status == "returned" || status == "threw", "Unknown completion status.");
				if (status == "returned") {
					Snapshot (Property (record, "receiver"), call, "receiver", true, null, new HashSet<long> ());
					Snapshot (Property (record, "result"), call, "result", true, call.Method.Void ? "void" : null, new HashSet<long> ());
					Arguments (record, call, false);
					call.CategoryCounts.Returned++;
					call.MethodCounts.Returned++;
					if (record.TryGetProperty ("observations", out var observations) && observations.TryGetProperty ("materializationCompleted", out _)) {
						Require (Boolean (observations, "materializationCompleted"), "Unexpected incomplete materialization observation.");
						report.CompletedBodyDecodes++;
						report.DeclaredIlBytes += Number (observations, "declaredIlBytes");
						report.DecodedInstructions += Number (observations, "decodedInstructionCount");
					}
				} else {
					Text (Property (record, "exception"), "type");
					call.CategoryCounts.Threw++;
					call.MethodCounts.Threw++;
				}
				Issues (record, call);
				active.Remove (call.Id);
				stack.Pop ();
			}

			void Arguments (JsonElement record, ActiveCall call, bool entry)
			{
				var arguments = Array (record, "arguments");
				Require (arguments.GetArrayLength () == call.Method.OutParameters.Length, "Argument count does not match the method descriptor.");
				int i = 0;
				foreach (var argument in arguments.EnumerateArray ()) {
					Snapshot (argument, call, "arguments[" + i + "]", !entry, entry && call.Method.OutParameters [i] ? "out-unassigned" : null, new HashSet<long> ());
					i++;
				}
			}

			void Snapshot (JsonElement value, ActiveCall call, string path, bool leaving, string allowed_missing, HashSet<long> seen)
			{
				Require (value.ValueKind == JsonValueKind.Object, "Snapshot must be an object at " + path);
				string kind = Text (value, "kind"), reconstruction = Text (value, "reconstruction");
				Require (kinds.Contains (kind), "Unknown snapshot kind: " + kind);
				Require (reconstruction == "value" || reconstruction == "snapshot" || reconstruction == "reference" || reconstruction == "unavailable", "Unknown reconstruction kind.");
				if (value.TryGetProperty ("id", out var identity)) {
					long id = Integer (identity, "object ID");
					Require (id > 0, "Invalid object ID.");
					bool previous = !seen.Add (id);
					if (value.TryGetProperty ("sameSnapshot", out var same) && same.ValueKind == JsonValueKind.True)
						Require (previous, "Alias has no preceding object in this snapshot.");
				}
				if (value.TryGetProperty ("sourceCallId", out var producer)) {
					long source = Integer (producer, "sourceCallId");
					Require (source > 0 && source < sequence, "Object producer must precede this snapshot.");
					Require (!active.Contains (source) || (leaving && source == call.Id), "Object producer has not completed.");
					Text (value, "sourcePath");
				}
				if (reconstruction == "unavailable") {
					string reason = Text (value, "reason");
					if (reason != allowed_missing) Issue (call, reason, path);
				}
				if (reconstruction == "reference" && !value.TryGetProperty ("sourceCallId", out _) && !value.TryGetProperty ("sameSnapshot", out _))
					Issue (call, "reference-has-no-producer", path);
				if (kind == "value" || value.TryGetProperty ("fields", out _)) {
					foreach (var field in Array (value, "fields").EnumerateArray ())
						Snapshot (Property (field, "value"), call, path + ".field(" + Property (field, "name").ToString () + ")", leaving, null, seen);
				}
				if (kind == "array" && reconstruction == "snapshot") {
					var lengths = Array (value, "lengths").EnumerateArray ().ToArray ();
					var bounds = Array (value, "lowerBounds");
					Require (lengths.Length > 0 && lengths.Length == bounds.GetArrayLength (), "Array rank/bounds mismatch.");
					var dimensions = lengths.Select (length => Integer (length, "array dimension")).ToArray ();
					long count = dimensions.Contains (0L) ? 0 : 1;
					foreach (long dimension in dimensions) {
						Require (dimension >= 0 && dimension <= int.MaxValue, "Invalid array length.");
						Require (dimension == 0 || count <= int.MaxValue / dimension, "Array snapshot is too large.");
						count *= dimension;
					}
					foreach (var bound in bounds.EnumerateArray ()) {
						long lower = Integer (bound, "array lower bound");
						Require (lower >= int.MinValue && lower <= int.MaxValue, "Invalid array lower bound.");
					}
					var items = Array (value, "items");
					Require (items.GetArrayLength () == count, "Array snapshot count disagrees with its dimensions.");
					int i = 0;
					foreach (var item in items.EnumerateArray ()) Snapshot (item, call, path + "[" + i++ + "]", leaving, null, seen);
				}
				if (kind == "bytes" && reconstruction == "snapshot") {
					string base64 = Text (value, "base64");
					Require (Convert.TryFromBase64String (base64, new byte [base64.Length], out var size) && size == Number (value, "length"), "Invalid byte snapshot.");
				}
				if (kind == "scalar" || kind == "string" || kind == "enum" || kind == "type" || kind == "member") Property (value, "value");
			}

			void Issues (JsonElement record, ActiveCall call)
			{
				if (!record.TryGetProperty ("replayIssues", out var entries)) return;
				Require (entries.ValueKind == JsonValueKind.Array, "replayIssues must be an array.");
				foreach (var issue in entries.EnumerateArray ()) Issue (call, String (issue), "call");
			}

			void Issue (ActiveCall call, string reason, string path)
			{
				if (!call.Issues.Add (reason)) return;
				string key = call.Origin + ":" + reason;
				if (!issues.TryGetValue (key, out var issue)) {
					issue = new IssueSummary { Origin = call.Origin, Reason = reason };
					issues.Add (key, issue);
					report.Issues.Add (issue);
				}
				issue.AffectedCalls++;
				if (issue.Examples.Count < 8) issue.Examples.Add ($"call {call.Id}, method {call.Method.MethodId}, {path}");
			}
		}

		static JsonElement Property (JsonElement value, string name)
		{
			Require (value.ValueKind == JsonValueKind.Object && value.TryGetProperty (name, out _), "Missing property: " + name);
			return value.GetProperty (name);
		}
		static JsonElement Array (JsonElement value, string name)
		{
			var result = Property (value, name);
			Require (result.ValueKind == JsonValueKind.Array, "Expected array: " + name);
			return result;
		}
		static string String (JsonElement value)
		{
			Require (value.ValueKind == JsonValueKind.String, "Expected string.");
			return value.GetString ();
		}
		static string Text (JsonElement value, string name) => String (Property (value, name));
		static long Number (JsonElement value, string name)
		{
			return Integer (Property (value, name), name);
		}
		static long Integer (JsonElement value, string name)
		{
			Require (value.ValueKind == JsonValueKind.Number && value.TryGetInt64 (out _), "Expected integer: " + name);
			return value.GetInt64 ();
		}
		static int Thread (JsonElement value)
		{
			long thread = Number (value, "threadId");
			Require (thread > 0 && thread <= int.MaxValue, "Invalid managed thread ID.");
			return (int) thread;
		}
		static bool Boolean (JsonElement value, string name)
		{
			var property = Property (value, name);
			Require (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False, "Expected boolean: " + name);
			return property.GetBoolean ();
		}
		static void Require (bool condition, string message)
		{
			if (!condition) throw new InvalidDataException (message);
		}
	}
}
