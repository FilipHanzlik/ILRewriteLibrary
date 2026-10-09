using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Mono.Cecil.Diagnostics {

	public sealed class TraceCall {
		internal long Id;
		internal int ThreadId;
		internal int Depth;
		internal long Started;
		internal TraceCall Parent;
		internal TraceMethod Method;
		internal string CallOrigin;
		internal bool Completed;

		public string Origin { get { return CallOrigin; } }
		public long CallId { get { return Id; } }
	}

	public static class CecilTrace {

		static readonly string trace_path = Environment.GetEnvironmentVariable ("CECIL_TRACE_FILE");
		static readonly Lazy<Recorder> recorder = new Lazy<Recorder> (() => new Recorder (trace_path), LazyThreadSafetyMode.ExecutionAndPublication);
		[ThreadStatic] static TraceCall current;
		[ThreadStatic] static bool recording;

		public static bool IsEnabled { get { return !recording && !string.IsNullOrEmpty (trace_path); } }

		[MethodImpl (MethodImplOptions.NoInlining)]
		public static TraceCall Enter (RuntimeMethodHandle method, RuntimeTypeHandle declaringType, object target, object [] arguments)
		{
			if (!IsEnabled)
				return null;
			recording = true;
			bool succeeded = false;
			try {
				var member = MethodBase.GetMethodFromHandle (method, declaringType);
				var caller = new StackFrame (2, false).GetMethod ();
				var origin = caller == null ? "unknown" : IsCecilAssembly (caller.Module.Assembly) ? "internal" : "external";
				var call = recorder.Value.Enter (member, caller, origin, target, arguments, current);
				current = call;
				succeeded = true;
				return call;
			} finally {
				if (!succeeded && recorder.IsValueCreated)
					recorder.Value.MarkFault ();
				recording = false;
			}
		}

		public static void Complete (TraceCall call, object target, object result, object [] arguments)
		{
			if (call == null)
				return;
			ValidateCurrent (call);
			recording = true;
			bool succeeded = false;
			try {
				recorder.Value.Leave (call, target, result, arguments, null);
				call.Completed = true;
				current = call.Parent;
				succeeded = true;
			} finally {
				if (!succeeded)
					recorder.Value.MarkFault ();
				recording = false;
			}
		}

		public static void Fail (TraceCall call, Exception exception)
		{
			if (call == null)
				return;
			if (exception == null)
				throw new ArgumentNullException (nameof (exception));
			ValidateCurrent (call);
			recording = true;
			bool succeeded = false;
			try {
				recorder.Value.Leave (call, null, null, null, exception);
				call.Completed = true;
				current = call.Parent;
				succeeded = true;
			} finally {
				if (!succeeded)
					recorder.Value.MarkFault ();
				recording = false;
			}
		}

		public static object Unavailable (string reason)
		{
			if (string.IsNullOrEmpty (reason))
				throw new ArgumentException ("A trace gap must have a reason.", nameof (reason));
			return new UnavailableTraceValue (reason);
		}

		public static void Flush ()
		{
			if (recorder.IsValueCreated)
				recorder.Value.Flush ();
		}

		static void ValidateCurrent (TraceCall call)
		{
			if (call.Completed || call != current || call.ThreadId != Thread.CurrentThread.ManagedThreadId)
				throw new InvalidOperationException ("Cecil trace call completion is out of order or on another thread.");
		}

		static bool IsCecilAssembly (Assembly assembly)
		{
			if (assembly == typeof (CecilTrace).Assembly)
				return true;
			var name = assembly.GetName ().Name;
			return name == "Mono.Cecil.Rocks" || name == "Mono.Cecil.Pdb" || name == "Mono.Cecil.Mdb";
		}

		sealed class Recorder {
			readonly object gate = new object ();
			readonly StreamWriter writer;
			readonly TraceValueWriter values = new TraceValueWriter ();
			readonly Dictionary<MethodBase, TraceMethod> methods = new Dictionary<MethodBase, TraceMethod> ();
			long sequence;
			long open_calls;
			long total_calls;
			bool shutting_down;
			bool footer_written;
			bool faulted;

			internal Recorder (string path)
			{
				int process_id;
				using (var process = Process.GetCurrentProcess ())
					process_id = process.Id;
				path = Path.GetFullPath (path.Replace ("{pid}", process_id.ToString (CultureInfo.InvariantCulture)));
				writer = new StreamWriter (new FileStream (path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding (false));
				bool initialized = false;
				try {
					writer.AutoFlush = true;
					var assembly = typeof (CecilTrace).Assembly;
					Write (TraceJson.Object ("event", "session", "schemaVersion", 1, "processId", process_id,
						"utc", DateTime.UtcNow.ToString ("O", CultureInfo.InvariantCulture),
						"assembly", TraceJson.Object ("name", assembly.FullName, "path", assembly.Location,
							"moduleMvid", assembly.ManifestModule.ModuleVersionId.ToString ("D"), "sha256", Fingerprint (assembly.Location)),
						"runtime", Environment.Version.ToString (), "stopwatchFrequency", Stopwatch.Frequency, "littleEndian", BitConverter.IsLittleEndian,
						"snapshotLimits", values.Limits,
						"limitations", new [] {
							"Only woven managed method bodies are observed; consult the weaver coverage manifest.",
							"Consumer field access, array writes, and mutation through non-Cecil APIs are not intercepted.",
							"Categories are heuristic logical operations, not proofs of purity; reads can lazily materialize or cache data.",
							"Identity is significant for reference objects. Reuse IDs and external producer results, not arbitrary fresh objects.",
							"Callbacks, streams, external services, incomplete snapshots and input files require an explicit replay driver or adapter.",
							"Constructor entry is after base/this initialization; failures in the initializer have no derived-constructor entry.",
							"Order is recorder observation order, not a deterministic schedule for concurrent execution.",
							"Origins use the immediate managed stack caller; missing frames are unknown, and uninstrumented/inlined callers can obscure boundaries.",
							"Managed by-reference storage locations/aliases and state after a throwing call are not captured.",
							"Timing, allocation and object lifetimes are perturbed. Use an uninstrumented binary for performance baselines.",
							"A recorder is shared per loaded Cecil assembly; use one instrumented Cecil load context per process."
						}));
					AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
					initialized = true;
				} finally {
					if (!initialized)
						writer.Dispose ();
				}
			}

			static string Fingerprint (string path)
			{
				if (string.IsNullOrEmpty (path))
					return null;
				using (var file = File.OpenRead (path))
				using (var hash = SHA256.Create ())
					return BitConverter.ToString (hash.ComputeHash (file)).Replace ("-", "").ToLowerInvariant ();
			}

			internal TraceCall Enter (MethodBase method, MethodBase caller, string origin, object target, object [] arguments, TraceCall parent)
			{
				lock (gate) {
					ContinueShutdown ();
					if (!methods.TryGetValue (method, out var descriptor)) {
						descriptor = new TraceMethod (methods.Count + 1, method);
						methods.Add (method, descriptor);
						Write (descriptor.Describe ());
					}
					var call = new TraceCall {
						Id = sequence + 1, ThreadId = Thread.CurrentThread.ManagedThreadId, Depth = parent == null ? 0 : parent.Depth + 1,
						Started = Stopwatch.GetTimestamp (), Parent = parent, Method = descriptor, CallOrigin = origin
					};
					var issues = new List<string> (descriptor.ReplayIssues);
					if (origin == "external" && parent != null)
						issues.Add ("callback-reentry-requires-driver");
					if (origin == "unknown")
						issues.Add ("caller-origin-unknown");
					if (origin == "external" && method.IsConstructor && target != null && target.GetType () != method.DeclaringType)
						issues.Add ("external-subclass-constructor-needs-driver");
					var record = TraceJson.Object ("event", "enter", "callId", call.Id, "threadId", call.ThreadId,
						"parentCallId", parent == null ? null : (object) parent.Id, "depth", call.Depth, "methodId", descriptor.Id,
						"origin", origin, "caller", caller == null ? null : caller.Module.Assembly.GetName ().Name + "!" + caller.DeclaringType?.FullName + "::" + caller.Name,
						"receiver", values.Capture (target), "arguments", values.CaptureArguments (arguments), "replayIssues", issues);
					if (method.Name == "get_Body" && target is MethodDefinition definition)
						record.Add ("observations", TraceJson.Object ("bodyWasCachedAtEntry", definition.body != null, "bodyRvaAtEntry", definition.rva));
					Write (record);
					open_calls++;
					total_calls++;
					return call;
				}
			}

			internal void Leave (TraceCall call, object target, object result, object [] arguments, Exception exception)
			{
				lock (gate) {
					ContinueShutdown ();
					var record = TraceJson.Object ("event", "leave", "callId", call.Id, "threadId", call.ThreadId,
						"status", exception == null ? "returned" : "threw", "elapsedTicks", Stopwatch.GetTimestamp () - call.Started);
					if (exception == null) {
						if (call.CallOrigin == "external") {
							if (call.Method.Method.IsConstructor && target != null && target.GetType () == call.Method.Method.DeclaringType)
								values.Bind (target, call.Id, "receiver");
							values.Bind (result, call.Id, "result");
							var parameters = call.Method.Method.GetParameters ();
							for (int i = 0; i < arguments.Length; i++)
								if (parameters [i].ParameterType.IsByRef)
									values.Bind (arguments [i], call.Id, "arguments[" + i.ToString (CultureInfo.InvariantCulture) + "]");
						}
						record.Add ("receiver", values.Capture (target));
						record.Add ("result", values.Capture (result));
						record.Add ("arguments", values.CaptureArguments (arguments));
						if (call.Method.Method.DeclaringType == typeof (Cil.CodeReader) &&
							call.Method.Method.Name == "ReadMethodBody" && result is Cil.MethodBody body)
							record.Add ("observations", TraceJson.Object ("materializationCompleted", true,
								"declaredIlBytes", body.code_size, "decodedInstructionCount", body.instructions == null ? 0 : body.instructions.size));
					} else {
						// Exception.Message is virtual; read the base backing field instead of executing consumer code.
						var message = typeof (Exception).GetField ("_message", BindingFlags.Instance | BindingFlags.NonPublic);
						record.Add ("exception", TraceJson.Object ("type", exception.GetType ().AssemblyQualifiedName,
							"id", values.GetIdentityId (exception), "message", message == null ? null : message.GetValue (exception) as string,
							"messageAvailable", message != null));
						record.Add ("replayIssues", new [] { "state-after-exception-not-captured" });
					}
					Write (record);
					open_calls--;
					WriteShutdownCheckpoint ();
				}
			}

			internal void Flush ()
			{
				lock (gate) {
					writer.Flush ();
				}
			}

			internal void MarkFault ()
			{
				lock (gate)
					faulted = true;
			}

			void OnProcessExit (object sender, EventArgs arguments)
			{
				lock (gate) {
					if (shutting_down)
						return;
					shutting_down = true;
					Write (TraceJson.Object ("event", "shutdown-start", "openCalls", open_calls, "totalCalls", total_calls));
					WriteShutdownCheckpoint ();
				}
			}

			void ContinueShutdown ()
			{
				if (!footer_written)
					return;
				footer_written = false;
				Write (TraceJson.Object ("event", "shutdown-continued"));
			}

			void WriteShutdownCheckpoint ()
			{
				if (!shutting_down || open_calls != 0 || footer_written)
					return;
				Write (TraceJson.Object ("event", "session-end", "openCalls", open_calls, "totalCalls", total_calls,
					"recorderFailed", faulted, "shutdownCheckpoint", true));
				footer_written = true;
				// Later ProcessExit handlers may still call Cecil. AutoFlush persists each checkpoint;
				// the OS closes this process-lifetime stream after all shutdown handlers have finished.
			}

			void Write (Dictionary<string, object> record)
			{
				record.Add ("sequence", ++sequence);
				writer.WriteLine (TraceJson.Serialize (record));
			}
		}
	}
}
