using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using ManifestResourceAttributes = Mono.Cecil.ManifestResourceAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace Cecil.Trace.Workload {

	internal static class Program {
		static int Main (string [] args)
		{
			if (args.Length == 1 && args [0] == "--help") {
				Console.WriteLine ("Cecil.Trace.Workload --scenario readwrite|values|edges|shapes|companions|shutdown --output <new-assembly.dll>");
				return 0;
			}
			if (args.Length != 4 || args [0] != "--scenario" || args [2] != "--output")
				throw new ArgumentException ("Expected --scenario readwrite|values|edges|shapes|companions|shutdown --output <new-assembly.dll>");
			var result = new Dictionary<string, object> ();
			switch (args [1]) {
			case "readwrite": ReadWrite (Path.GetFullPath (args [3]), result); break;
			case "values": Values (result); break;
			case "edges": Edges (result); break;
			case "shapes": Shapes (result); break;
			case "companions": Companions (Path.GetFullPath (args [3]), result); break;
			case "shutdown":
				Instruction.Create (OpCodes.Nop);
				AppDomain.CurrentDomain.ProcessExit += (_, _) => {
					var instruction = Instruction.Create (OpCodes.Ldc_I4, 73);
					if ((int) instruction.Operand != 73) throw new InvalidOperationException ("Shutdown call changed behavior.");
				};
				break;
			default: throw new ArgumentException ("Unknown scenario " + args [1]);
			}
			var path = typeof (ModuleDefinition).Assembly.Location;
			result.Add ("loadedCecil", path);
			result.Add ("loadedCecilSha256", Hash (path));
			result.Add ("scenario", args [1]);
			Console.WriteLine (JsonSerializer.Serialize (result));
			return 0;
		}

		static void ReadWrite (string output, Dictionary<string, object> result)
		{
			string input = output + ".input.dll";
			if (File.Exists (input) || File.Exists (output)) throw new IOException ("Workload outputs must not already exist.");
			using (var module = ModuleDefinition.CreateModule ("TraceSample.dll", ModuleKind.Dll)) {
				var type = new TypeDefinition ("Demo", "Probe", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
				module.Types.Add (type);
				var answer = new MethodDefinition ("Answer", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Int32);
				type.Methods.Add (answer);
				var il = answer.Body.GetILProcessor ();
				var target = Instruction.Create (OpCodes.Ldc_I4, 41);
				il.Append (Instruction.Create (OpCodes.Br_S, target));
				il.Append (Instruction.Create (OpCodes.Ldc_I4_M1));
				il.Append (Instruction.Create (OpCodes.Pop));
				il.Append (target);
				il.Append (Instruction.Create (OpCodes.Ret));
				var untouched = new MethodDefinition ("Untouched", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
				untouched.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
				type.Methods.Add (untouched);
				module.Resources.Add (new EmbeddedResource ("bytes", ManifestResourceAttributes.Private, new byte [] { 1, 2, 3 }));
				WriteNew (module, input);
			}
			using (var module = ModuleDefinition.ReadModule (input, new ReaderParameters { ReadingMode = ReadingMode.Deferred })) {
				var type = module.GetType ("Demo.Probe");
				var answer = type.Methods.Single (m => m.Name == "Answer");
				var body = answer.Body;
				if (!ReferenceEquals (body, answer.Body)) throw new InvalidOperationException ("Body cache changed identity.");
				body.Instructions.Single (i => i.OpCode == OpCodes.Ldc_I4).Operand = 42;
				var body_field = typeof (MethodDefinition).GetField ("body", BindingFlags.NonPublic | BindingFlags.Instance);
				var untouched = type.Methods.Single (m => m.Name == "Untouched");
				if (body_field.GetValue (untouched) != null) throw new InvalidOperationException ("Unread body was materialized.");
				result.Add ("typeReferenceFound", module.TryGetTypeReference ("System.Object", out var object_type));
				result.Add ("objectType", object_type?.FullName);
				result.Add ("enumeratedTypes", module.GetTypes ().Count ());
				WriteNew (module, output);
				result.Add ("unreadBodyStillDeferred", body_field.GetValue (untouched) == null);
			}
			var context = new AssemblyLoadContext ("trace-workload-output", isCollectible: true);
			try {
				var assembly = context.LoadFromAssemblyPath (output);
				result.Add ("answer", assembly.GetType ("Demo.Probe").GetMethod ("Answer").Invoke (null, null));
			} finally {
				context.Unload ();
			}
			result.Add ("outputSha256", Hash (output));
		}

		static void WriteNew (ModuleDefinition module, string output)
		{
			using var file = new FileStream (output, FileMode.CreateNew, FileAccess.ReadWrite);
			module.Write (file, new WriterParameters { DeterministicMvid = true, Timestamp = 0 });
		}

		static void Companions (string output, Dictionary<string, object> result)
		{
			string mdb_file = output + ".mdb.dll";
			if (File.Exists (output) || File.Exists (mdb_file) || File.Exists (mdb_file + ".mdb"))
				throw new IOException ("Companion workload outputs must not already exist.");
			using var module = ModuleDefinition.CreateModule ("Symbols.dll", ModuleKind.Dll);
			module.Mvid = new Guid ("e91cedbf-04d0-4cb2-83ec-b2fb9e640163");
			var type = new TypeDefinition ("Demo", "Symbols", TypeAttributes.Public, module.TypeSystem.Object);
			module.Types.Add (type);
			var method = new MethodDefinition ("Probe", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
			type.Methods.Add (method);
			var ret = Instruction.Create (OpCodes.Ret);
			method.Body.Instructions.Add (ret);
			method.DebugInformation.SequencePoints.Add (new SequencePoint (ret, new Document ("probe.cs")) { StartLine = 1, EndLine = 1, StartColumn = 1, EndColumn = 2 });
			result.Add ("allTypes", module.GetAllTypes ().Count ());
			using var symbols = new MemoryStream ();
			using var image = new MemoryStream ();
			module.Write (image, new WriterParameters { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider (), SymbolStream = symbols, Timestamp = 0 });
			image.Position = 0;
			symbols.Position = 0;
			using (var portable = ModuleDefinition.ReadModule (image, new ReaderParameters {
				ReadSymbols = true, SymbolReaderProvider = new Mono.Cecil.Pdb.PdbReaderProvider (), SymbolStream = symbols
			})) {
				result.Add ("portableSequencePoints", portable.Types [1].Methods [0].DebugInformation.SequencePoints.Count);
				using var rewritten = new FileStream (output, FileMode.CreateNew, FileAccess.ReadWrite);
				using var rewritten_symbols = new MemoryStream ();
				portable.Write (rewritten, new WriterParameters {
					WriteSymbols = true, SymbolWriterProvider = new Mono.Cecil.Pdb.PdbWriterProvider (), SymbolStream = rewritten_symbols, Timestamp = 0
				});
				result.Add ("portableBytes", rewritten_symbols.Length);
			}
			module.Write (mdb_file, new WriterParameters { WriteSymbols = true, SymbolWriterProvider = new Mono.Cecil.Mdb.MdbWriterProvider (), Timestamp = 0 });
			using var mdb = ModuleDefinition.ReadModule (mdb_file, new ReaderParameters { ReadSymbols = true, SymbolReaderProvider = new Mono.Cecil.Mdb.MdbReaderProvider () });
			result.Add ("mdbSequencePoints", mdb.Types [1].Methods [0].DebugInformation.SequencePoints.Count);
		}

		static void Values (Dictionary<string, object> result)
		{
			var instructions = new Collection<Instruction> ();
			var instruction = Instruction.Create (OpCodes.Ldc_I4, 12);
			instruction.Operand = 37;
			instructions.Add (instruction);
			instructions.Add (instruction);
			result.Add ("removed", instructions.Remove (instruction));
			result.Add ("count", instructions.Count);
			result.Add ("operand", instructions [0].Operand);
			var dimension = new ArrayDimension (1, 3);
			dimension.UpperBound = 5;
			result.Add ("upperBound", dimension.UpperBound);
			var numbers = new Collection<int> { 3, 5 };
			int sum = 0;
			foreach (int value in numbers) sum += value;
			result.Add ("sum", sum);
			bool fault = false;
			try { _ = numbers [10]; } catch (ArgumentOutOfRangeException) { fault = true; }
			result.Add ("caughtExpectedFault", fault);
			result.Add ("countAfterFault", numbers.Count);
			Instruction.Create (OpCodes.Ldstr, "quote\"\\\r\n\u03a9\ud800");
			Instruction.Create (OpCodes.Ldc_R8, BitConverter.Int64BitsToDouble (unchecked ((long) 0xfff8000000000042)));
			Instruction.Create (OpCodes.Ldc_R4, -0.0f);
			Instruction.Create (OpCodes.Switch, new [] { instruction, instruction });
		}

		static void Shapes (Dictionary<string, object> result)
		{
			var instruction = Instruction.Create (OpCodes.Nop);
			var cycle = new object [1];
			cycle [0] = cycle;
			instruction.Operand = cycle;
			result.Add ("cyclePreserved", ReferenceEquals (instruction.Operand, cycle [0]));
			var matrix = Array.CreateInstance (typeof (int), new [] { 2, 2 }, new [] { -1, 3 });
			matrix.SetValue (7, -1, 3);
			matrix.SetValue (9, 0, 4);
			instruction.Operand = matrix;
			result.Add ("matrixValue", ((Array) instruction.Operand).GetValue (0, 4));
			instruction.Operand = new object [] { new [] { 8 } };
			result.Add ("nestedValue", ((int []) ((object []) instruction.Operand) [0]) [0]);
			var bytes = new byte [] { 0, 1, 255 };
			instruction.Operand = bytes;
			((byte []) instruction.Operand) [1] = 2;
			result.Add ("mutatedByte", ((byte []) instruction.Operand) [1]);
		}

		[MethodImpl (MethodImplOptions.NoInlining)]
		static void Edges (Dictionary<string, object> result)
		{
			using (var dependency = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("DiagnosticDependency", new Version (1, 0)), "DiagnosticDependency", ModuleKind.Dll)) {
				dependency.MainModule.Types.Add (new TypeDefinition ("Diagnostic", "Dependency", TypeAttributes.Public, dependency.MainModule.TypeSystem.Object));
				var resolver = new CallbackResolver (dependency);
				using var module = ModuleDefinition.CreateModule ("Requester", new ModuleParameters { AssemblyResolver = resolver });
				var reference = new TypeReference ("Diagnostic", "Dependency", module, dependency.Name);
				result.Add ("resolved", reference.Resolve ().Name);
				result.Add ("callbackCount", resolver.Calls);
			}
			var weak = ObserveOpaque ();
			GC.Collect ();
			GC.WaitForPendingFinalizers ();
			GC.Collect ();
			result.Add ("opaqueCollected", !weak.IsAlive);
			Instruction.Create (OpCodes.Ldstr, new string ('x', 16385));
			var resource = new EmbeddedResource ("large", ManifestResourceAttributes.Private, new byte [65537]);
			using (var stream = resource.GetResourceStream ()) result.Add ("streamLength", stream.Length);
			int sum = 0;
			var threads = Enumerable.Range (1, 3).Select (n => new Thread (() => {
				var value = Instruction.Create (OpCodes.Ldc_I4, n);
				Interlocked.Add (ref sum, (int) value.Operand);
			})).ToArray ();
			foreach (var thread in threads) thread.Start ();
			foreach (var thread in threads) thread.Join ();
			result.Add ("threadSum", sum);
		}

		[MethodImpl (MethodImplOptions.NoInlining)]
		static WeakReference ObserveOpaque ()
		{
			var instruction = Instruction.Create (OpCodes.Nop);
			var opaque = new Poison ();
			instruction.Operand = opaque;
			instruction.Operand = null;
			return new WeakReference (opaque);
		}

		sealed class Poison {
			public override string ToString () => throw new InvalidOperationException ("Recorder executed consumer ToString.");
		}

		sealed class CallbackResolver : IAssemblyResolver {
			readonly AssemblyDefinition dependency;
			internal int Calls;
			internal CallbackResolver (AssemblyDefinition dependency) { this.dependency = dependency; }
			[MethodImpl (MethodImplOptions.NoInlining)]
			public AssemblyDefinition Resolve (AssemblyNameReference name)
			{
				Calls++;
				if (dependency.MainModule.GetType ("Diagnostic.Dependency") == null) throw new InvalidOperationException ("Missing callback type.");
				return dependency;
			}
			public AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters) => Resolve (name);
			public void Dispose () { }
		}

		static string Hash (string path) => Convert.ToHexString (SHA256.HashData (File.ReadAllBytes (path))).ToLowerInvariant ();
	}
}
