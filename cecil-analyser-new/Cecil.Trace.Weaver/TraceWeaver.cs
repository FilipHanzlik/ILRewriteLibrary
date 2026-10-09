using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Cecil.Trace.Weaver {

	public sealed class CoverageReport {
		public int SchemaVersion { get; set; } = 1;
		public string Input { get; set; }
		public string Output { get; set; }
		public string Assembly { get; set; }
		public string InputSha256 { get; set; }
		public string OutputSha256 { get; set; }
		public Guid InputMvid { get; set; }
		public Guid OutputMvid { get; set; }
		public bool SymbolsPreserved { get; set; }
		public int InstrumentedMethods { get; set; }
		public List<MethodCoverage> Methods { get; } = new List<MethodCoverage> ();
		public List<string> ExposedFields { get; } = new List<string> ();
		public string [] Limitations { get; set; } = new [] {
			"Direct field access and writes through returned arrays or non-Cecil APIs are not intercepted.",
			"Constructor entry follows base/this initialization; exceptions before that entry are not observed for the derived constructor.",
			"Skipped methods are not traced. Unknown argument representations have explicit unavailable snapshots.",
			"Only this assembly was woven. Instrument each companion separately using the same diagnostic core.",
			"Origins use managed caller frames; consumer inlining, tail calls and native/runtime transitions can obscure boundaries."
		};
	}

	public sealed class MethodCoverage {
		public int OriginalToken { get; set; }
		public string Signature { get; set; }
		public string Status { get; set; }
		public string Reason { get; set; }
		public string [] ValueGaps { get; set; }
	}

	public static class TraceWeaver {
		const string runtime_namespace = "Mono.Cecil.Diagnostics";
		const string marker_name = runtime_namespace + ".CecilTraceInstrumentedAttribute";
		static readonly HashSet<string> supported_assemblies = new HashSet<string> (StringComparer.Ordinal) {
			"Mono.Cecil", "Mono.Cecil.Rocks", "Mono.Cecil.Pdb", "Mono.Cecil.Mdb"
		};

		public static CoverageReport Weave (string input, string output, string key_path = null, string core_path = null)
		{
			input = Path.GetFullPath (input);
			output = Path.GetFullPath (output);
			if (string.Equals (input, output, StringComparison.OrdinalIgnoreCase))
				throw new ArgumentException ("Input and output must be different files.");
			string manifest = output + ".coverage.json";
			if (File.Exists (output) || File.Exists (manifest))
				throw new IOException ("Output or coverage manifest already exists; choose a new output path.");
			if (!Directory.Exists (Path.GetDirectoryName (output)))
				throw new DirectoryNotFoundException ("Create the output directory before weaving.");
			using var resolver = new DefaultAssemblyResolver ();
			resolver.AddSearchDirectory (Path.GetDirectoryName (input));
			resolver.AddSearchDirectory (Path.GetDirectoryName (typeof (object).Assembly.Location));
			if (core_path != null)
				resolver.AddSearchDirectory (Path.GetDirectoryName (Path.GetFullPath (core_path)));
			var reader = new ReaderParameters {
				AssemblyResolver = resolver, InMemory = true, ReadSymbols = true,
				SymbolReaderProvider = new DefaultSymbolReaderProvider (false)
			};
			using var module = ModuleDefinition.ReadModule (input, reader);
			if (module.Assembly == null || !supported_assemblies.Contains (module.Assembly.Name.Name))
				throw new NotSupportedException ("Only the core Cecil and Rocks/Pdb/Mdb assemblies are supported.");
			if (module.Assembly.Modules.Count != 1)
				throw new NotSupportedException ("Multi-module assemblies are not supported.");
			if (module.Assembly.CustomAttributes.Any (a => a.AttributeType.FullName == marker_name))
				throw new InvalidOperationException ("Input has already been instrumented.");
			if (module.HasSymbols && !module.SymbolReader.GetType ().Name.Contains ("PortablePdb"))
				throw new NotSupportedException ("Only portable and embedded portable PDBs are supported; symbols will not be silently discarded.");
			if (!module.HasSymbols && module.HasDebugHeader && module.GetDebugHeader ().Entries.Any (e => e.Directory.Type == ImageDebugType.CodeView || e.Directory.Type == ImageDebugType.EmbeddedPortablePdb))
				throw new InvalidOperationException ("Input refers to debug symbols that could not be loaded. Supply the matching portable PDB before weaving.");
			if (module.Assembly.Name.HasPublicKey && key_path == null)
				throw new ArgumentException ("Strong-named input requires --key. Signing will not be silently removed.");
			byte [] key = key_path == null ? null : File.ReadAllBytes (key_path);
			byte [] public_key = module.Assembly.Name.PublicKey.ToArray ();
			using var companion_core = module.Assembly.Name.Name == "Mono.Cecil" ? null :
				ModuleDefinition.ReadModule (core_path ?? throw new ArgumentException ("Companion weaving requires --core with the diagnostic core DLL."),
					new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
			var core = companion_core ?? module;
			var trace = core.GetType (runtime_namespace + ".CecilTrace");
			var marker = core.GetType (marker_name);
			if (core.Assembly?.Name.Name != "Mono.Cecil" || trace == null || marker == null)
				throw new InvalidOperationException ("Core lacks diagnostic hooks. Build it with -p:CecilTraceSupport=true.");
			if (companion_core != null && !module.AssemblyReferences.Any (r => r.FullName == core.Assembly.Name.FullName))
				throw new InvalidOperationException ("Companion's Cecil reference does not match the supplied diagnostic core identity.");
			var hooks = new Hooks (module, trace, marker);
			var report = new CoverageReport {
				Input = input, Output = output, Assembly = module.Assembly.Name.FullName,
				InputMvid = module.Mvid, InputSha256 = Hash (File.ReadAllBytes (input)), SymbolsPreserved = module.HasSymbols
			};
			foreach (var type in module.GetTypes ().ToArray ()) {
				if (Visible (type))
					report.ExposedFields.AddRange (type.Fields.Where (f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly).Select (f => f.FullName));
				foreach (var method in type.Methods.ToArray ()) {
					string reason = SkipReason (method);
					var coverage = new MethodCoverage {
						OriginalToken = method.MetadataToken.ToInt32 (), Signature = method.FullName,
						Status = reason == null ? "instrumented" : "skipped", Reason = reason,
						ValueGaps = ValueGaps (method).ToArray ()
					};
					report.Methods.Add (coverage);
					if (reason != null)
						continue;
					Instrument (method, hooks);
					report.InstrumentedMethods++;
				}
			}
			module.Assembly.CustomAttributes.Add (new CustomAttribute (hooks.Marker));
			using var bytes = new MemoryStream ();
			module.Write (bytes, new WriterParameters {
				StrongNameKeyBlob = key, DeterministicMvid = true, Timestamp = 0,
				WriteSymbols = module.HasSymbols,
				SymbolWriterProvider = module.HasSymbols ? new NamedEmbeddedSymbols (output) : null
			});
			byte [] image = bytes.ToArray ();
			using (var verification = ModuleDefinition.ReadModule (new MemoryStream (image))) {
				if (!public_key.SequenceEqual (verification.Assembly.Name.PublicKey))
					throw new InvalidOperationException ("Signing key does not match the input assembly's public key.");
				report.OutputMvid = verification.Mvid;
			}
			report.OutputSha256 = Hash (image);
			var json = JsonSerializer.SerializeToUtf8Bytes (report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
			bool created_output = false, created_manifest = false, complete = false;
			try {
				using (var destination = new FileStream (output, FileMode.CreateNew, FileAccess.Write)) {
					created_output = true;
					using (var coverage = new FileStream (manifest, FileMode.CreateNew, FileAccess.Write)) {
						created_manifest = true;
						destination.Write (image, 0, image.Length);
						coverage.Write (json, 0, json.Length);
					}
				}
				complete = true;
			} finally {
				if (!complete) {
					if (created_output) File.Delete (output);
					if (created_manifest) File.Delete (manifest);
				}
			}
			return report;
		}

		static string Hash (byte [] bytes)
		{
			return Convert.ToHexString (SHA256.HashData (bytes)).ToLowerInvariant ();
		}

		static bool Visible (TypeDefinition type)
		{
			return type.DeclaringType == null ? type.IsPublic :
				(type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) && Visible (type.DeclaringType);
		}

		static string SkipReason (MethodDefinition method)
		{
			for (var type = method.DeclaringType; type != null; type = type.DeclaringType)
				if (type.Namespace == runtime_namespace)
					return "recorder-excluded-to-prevent-recursion";
			if (!method.HasBody || method.Body.Instructions.Count == 0)
				return "no-managed-body";
			if (method.CallingConvention == MethodCallingConvention.VarArg)
				return "vararg-call-signature";
			if (method.ReturnType.IsByReference)
				return "managed-reference-return";
			if (method.Body.Instructions.Any (i => i.OpCode == OpCodes.Jmp || i.OpCode == OpCodes.Tail))
				return "jmp-or-tail-call-semantics";
			if (method.IsConstructor && !method.IsStatic && !method.DeclaringType.IsValueType && ConstructorStart (method) == null)
				return "constructor-initializer-not-recognized";
			return null;
		}

		static IEnumerable<string> ValueGaps (MethodDefinition method)
		{
			if (method.IsConstructor && !method.IsStatic)
				yield return method.DeclaringType.IsValueType ? "uninitialized-value-type-receiver-at-entry" : "constructor-initializer-region";
			foreach (var parameter in method.Parameters) {
				var reason = UnsupportedValue (parameter.ParameterType);
				if (reason != null) yield return parameter.Name + ":" + reason;
			}
			var result = UnsupportedValue (method.ReturnType);
			if (result != null) yield return "result:" + result;
		}

		static string UnsupportedValue (TypeReference type)
		{
			while (type is ByReferenceType || type is OptionalModifierType || type is RequiredModifierType)
				type = ((TypeSpecification) type).ElementType;
			if (type.IsPointer || type.IsFunctionPointer || type.MetadataType == MetadataType.TypedByReference)
				return "pointer-or-typed-reference";
			if (type.GetElementType ().FullName == "System.Span`1" || type.GetElementType ().FullName == "System.ReadOnlySpan`1")
				return "byref-like-value";
			if (type is TypeDefinition definition && definition.CustomAttributes.Any (a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute"))
				return "byref-like-value";
			return null;
		}

		static Instruction ConstructorStart (MethodDefinition method)
		{
			return method.Body.Instructions.FirstOrDefault (i => i.OpCode == OpCodes.Call && i.Operand is MethodReference called && called.Name == ".ctor" &&
				(called.DeclaringType.GetElementType ().FullName == method.DeclaringType.FullName ||
				 called.DeclaringType.GetElementType ().FullName == method.DeclaringType.BaseType?.GetElementType ().FullName))?.Next;
		}

		static TypeReference SelfType (TypeDefinition type)
		{
			if (!type.HasGenericParameters) return type;
			var self = new GenericInstanceType (type);
			foreach (var parameter in type.GenericParameters) self.GenericArguments.Add (parameter);
			return self;
		}

		static MethodReference SelfMethod (MethodDefinition method, TypeReference self_type)
		{
			var reference = new MethodReference (method.Name, method.ReturnType, self_type) {
				HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention
			};
			foreach (var parameter in method.Parameters) reference.Parameters.Add (new ParameterDefinition (parameter.ParameterType));
			foreach (var parameter in method.GenericParameters) reference.GenericParameters.Add (new GenericParameter (parameter.Name, reference));
			if (!method.HasGenericParameters) return reference;
			var instance = new GenericInstanceMethod (reference);
			foreach (var parameter in method.GenericParameters) instance.GenericArguments.Add (parameter);
			return instance;
		}

		static void Instrument (MethodDefinition method, Hooks hooks)
		{
			var body = method.Body;
			var il = body.GetILProcessor ();
			var original = body.Instructions.ToArray ();
			foreach (var instruction in original) ExpandBranch (instruction);
			var start = method.IsConstructor && !method.IsStatic && !method.DeclaringType.IsValueType ? ConstructorStart (method) : original [0];
			var call = new VariableDefinition (hooks.CallType);
			var exception = new VariableDefinition (hooks.ExceptionType);
			var result = method.ReturnType.MetadataType == MetadataType.Void ? null : new VariableDefinition (method.ReturnType);
			body.Variables.Add (call);
			body.Variables.Add (exception);
			if (result != null) body.Variables.Add (result);
			var self_type = SelfType (method.DeclaringType);
			var prologue = new List<Instruction> {
				Instruction.Create (OpCodes.Ldnull), Instruction.Create (OpCodes.Stloc, call),
				Instruction.Create (OpCodes.Call, hooks.Enabled), Instruction.Create (OpCodes.Brfalse, start),
				Instruction.Create (OpCodes.Ldtoken, SelfMethod (method, self_type)), Instruction.Create (OpCodes.Ldtoken, self_type)
			};
			Receiver (prologue, method, self_type, hooks, true);
			Arguments (prologue, method, hooks, true);
			prologue.Add (Instruction.Create (OpCodes.Call, hooks.Enter));
			prologue.Add (Instruction.Create (OpCodes.Stloc, call));
			foreach (var instruction in prologue) il.InsertBefore (start, instruction);
			var epilogue = Instruction.Create (OpCodes.Nop);
			foreach (var instruction in original) {
				if (instruction.OpCode != OpCodes.Ret) continue;
				if (result == null) {
					instruction.OpCode = OpCodes.Leave;
					instruction.Operand = epilogue;
				} else {
					instruction.OpCode = OpCodes.Stloc;
					instruction.Operand = result;
					il.InsertAfter (instruction, Instruction.Create (OpCodes.Leave, epilogue));
				}
			}
			var original_end = Instruction.Create (OpCodes.Leave, epilogue);
			foreach (var handler in body.ExceptionHandlers) {
				if (handler.TryEnd == null) handler.TryEnd = original_end;
				if (handler.HandlerEnd == null) handler.HandlerEnd = original_end;
			}
			il.Append (original_end);
			var handler_start = Instruction.Create (OpCodes.Stloc, exception);
			var rethrow = Instruction.Create (OpCodes.Rethrow);
			foreach (var instruction in new [] {
				handler_start, Instruction.Create (OpCodes.Ldloc, call), Instruction.Create (OpCodes.Brfalse, rethrow),
				Instruction.Create (OpCodes.Ldloc, call), Instruction.Create (OpCodes.Ldloc, exception),
				Instruction.Create (OpCodes.Call, hooks.Fail), rethrow, epilogue
			}) il.Append (instruction);
			var return_start = result == null ? Instruction.Create (OpCodes.Ret) : Instruction.Create (OpCodes.Ldloc, result);
			var completion = new List<Instruction> {
				Instruction.Create (OpCodes.Ldloc, call), Instruction.Create (OpCodes.Brfalse, return_start), Instruction.Create (OpCodes.Ldloc, call)
			};
			Receiver (completion, method, self_type, hooks, false);
			if (result == null) Missing (completion, hooks, "void");
			else if (UnsupportedValue (method.ReturnType) is string reason) Missing (completion, hooks, reason);
			else {
				completion.Add (Instruction.Create (OpCodes.Ldloc, result));
				Box (completion, method.ReturnType);
			}
			Arguments (completion, method, hooks, false);
			completion.Add (Instruction.Create (OpCodes.Call, hooks.Complete));
			foreach (var instruction in completion) il.Append (instruction);
			il.Append (return_start);
			if (result != null) il.Append (Instruction.Create (OpCodes.Ret));
			body.ExceptionHandlers.Add (new ExceptionHandler (ExceptionHandlerType.Catch) {
				CatchType = hooks.ExceptionType, TryStart = start, TryEnd = handler_start,
				HandlerStart = handler_start, HandlerEnd = epilogue
			});
			method.ImplAttributes = (method.ImplAttributes & ~MethodImplAttributes.AggressiveInlining) | MethodImplAttributes.NoInlining;
		}

		static void Receiver (List<Instruction> code, MethodDefinition method, TypeReference self, Hooks hooks, bool entry)
		{
			if (method.IsStatic) code.Add (Instruction.Create (OpCodes.Ldnull));
			else if (UnsupportedValue (method.DeclaringType) is string reason) Missing (code, hooks, reason);
			else if (entry && method.IsConstructor && method.DeclaringType.IsValueType) Missing (code, hooks, "uninitialized-value-type-receiver");
			else {
				code.Add (Instruction.Create (OpCodes.Ldarg_0));
				if (method.DeclaringType.IsValueType) {
					code.Add (Instruction.Create (OpCodes.Ldobj, self));
					code.Add (Instruction.Create (OpCodes.Box, self));
				}
			}
		}

		static void Arguments (List<Instruction> code, MethodDefinition method, Hooks hooks, bool entry)
		{
			code.Add (Instruction.Create (OpCodes.Ldc_I4, method.Parameters.Count));
			code.Add (Instruction.Create (OpCodes.Newarr, method.Module.TypeSystem.Object));
			foreach (var parameter in method.Parameters) {
				code.Add (Instruction.Create (OpCodes.Dup));
				code.Add (Instruction.Create (OpCodes.Ldc_I4, parameter.Index));
				var reason = UnsupportedValue (parameter.ParameterType);
				if (entry && parameter.ParameterType.IsByReference && parameter.IsOut && !parameter.IsIn) Missing (code, hooks, "out-unassigned");
				else if (reason != null) Missing (code, hooks, reason);
				else {
					code.Add (Instruction.Create (OpCodes.Ldarg, parameter));
					TypeReference type = parameter.ParameterType;
					while (type is OptionalModifierType || type is RequiredModifierType) type = ((TypeSpecification) type).ElementType;
					if (type is ByReferenceType byref) {
						type = byref.ElementType;
						code.Add (Instruction.Create (OpCodes.Ldobj, type));
					}
					Box (code, type);
				}
				code.Add (Instruction.Create (OpCodes.Stelem_Ref));
			}
		}

		static void Box (List<Instruction> code, TypeReference type)
		{
			if (type.IsValueType || type.IsGenericParameter) code.Add (Instruction.Create (OpCodes.Box, type));
		}

		static void Missing (List<Instruction> code, Hooks hooks, string reason)
		{
			code.Add (Instruction.Create (OpCodes.Ldstr, reason));
			code.Add (Instruction.Create (OpCodes.Call, hooks.Unavailable));
		}

		static void ExpandBranch (Instruction instruction)
		{
			switch (instruction.OpCode.Code) {
			case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
			case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
			case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
			case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
			case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
			case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
			case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
			case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
			case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
			case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
			case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
			case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
			case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
			case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
			}
		}

		sealed class Hooks {
			internal readonly MethodReference Enabled, Enter, Complete, Fail, Unavailable, Marker;
			internal readonly TypeReference CallType, ExceptionType;
			internal Hooks (ModuleDefinition module, TypeDefinition trace, TypeDefinition marker)
			{
				MethodReference Import (string name) => module.ImportReference (trace.Methods.Single (m => m.Name == name));
				Enabled = Import ("get_IsEnabled");
				Enter = Import ("Enter");
				Complete = Import ("Complete");
				Fail = Import ("Fail");
				Unavailable = Import ("Unavailable");
				CallType = Enter.ReturnType;
				ExceptionType = Fail.Parameters [1].ParameterType;
				Marker = module.ImportReference (marker.Methods.Single (m => m.IsConstructor));
			}
		}

		sealed class NamedEmbeddedSymbols : ISymbolWriterProvider {
			readonly string file_name;
			internal NamedEmbeddedSymbols (string file_name)
			{
				this.file_name = file_name;
			}
			public ISymbolWriter GetSymbolWriter (ModuleDefinition module, string ignored)
			{
				// The PE is staged in memory; the embedded provider still requires its intended filename.
				return new EmbeddedPortablePdbWriterProvider ().GetSymbolWriter (module, file_name);
			}
			public ISymbolWriter GetSymbolWriter (ModuleDefinition module, Stream stream)
			{
				return GetSymbolWriter (module, file_name);
			}
		}
	}
}
