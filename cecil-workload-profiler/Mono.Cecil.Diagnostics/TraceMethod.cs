using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Mono.Cecil.Diagnostics {

	internal sealed class TraceMethod {
		internal readonly long Id;
		internal readonly MethodBase Method;
		internal readonly string Category;
		internal readonly string [] ReplayIssues;

		internal TraceMethod (long id, MethodBase method)
		{
			Id = id;
			Method = method;
			Category = Classify (method);
			var issues = new List<string> ();
			var parameters = method.GetParameters ();
			if ((method.Name == "ReadAssembly" || method.Name == "ReadModule") && parameters.Length > 0 && parameters [0].ParameterType == typeof (string))
				issues.Add ("input-files-must-be-preserved");
			if (method.Name == "Write" || method.Name == "WriteSymbols")
				issues.Add ("output-path-needs-remapping");
			if (method.IsConstructor && !method.IsStatic && !method.DeclaringType.IsValueType)
				issues.Add ("constructor-initializer-region-not-traced-as-parent");
			if (method.Name == ".cctor")
				issues.Add ("runtime-type-initializer-not-an-api-request");
			if (parameters.Any (parameter => parameter.ParameterType.IsByRef))
				issues.Add ("managed-reference-location-not-recorded");
			ReplayIssues = issues.ToArray ();
		}

		internal Dictionary<string, object> Describe ()
		{
			var parameters = Method.GetParameters ();
			return TraceJson.Object (
				"event", "method", "methodId", Id, "name", Method.Name,
				"declaringType", Method.DeclaringType.AssemblyQualifiedName,
				"assembly", Method.Module.Assembly.FullName, "moduleMvid", Method.Module.ModuleVersionId.ToString ("D"),
				"token", Method.MetadataToken, "signature", Method.ToString (),
				"isStatic", Method.IsStatic, "isConstructor", Method.IsConstructor, "isTypeInitializer", Method.Name == ".cctor", "isPublic", Method.IsPublic,
				"parameters", parameters.Select (p => TraceJson.Object ("name", p.Name, "type", p.ParameterType.AssemblyQualifiedName ?? p.ParameterType.ToString (), "byRef", p.ParameterType.IsByRef, "out", p.IsOut)).ToArray (),
				"returnType", Method is MethodInfo info ? info.ReturnType.AssemblyQualifiedName ?? info.ReturnType.ToString () : null,
				"genericArguments", Method.IsGenericMethod ? Method.GetGenericArguments ().Select (t => t.AssemblyQualifiedName ?? t.ToString ()).ToArray () : new string [0],
				"category", Category, "classification", "heuristic-v1");
		}

		static string Classify (MethodBase method)
		{
			var name = method.Name;
			var type = method.DeclaringType.FullName ?? "";
			if (method.IsConstructor || name == ".cctor" || name.StartsWith ("Create", StringComparison.Ordinal) || name == "GetPrototype")
				return "create";
			name = name.Substring (name.LastIndexOf ('.') + 1);
			if (name == "ReadMethodBody" || name == "ReadCode" || name == "ReadInstructions" || name == "ReadCustomAttributeSignature")
				return "materialize";
			if (name.StartsWith ("Resolve", StringComparison.Ordinal) || name.StartsWith ("Import", StringComparison.Ordinal))
				return "resolve";
			if (name == "Dispose" || name == "Close" || name == "Finalize")
				return "lifetime";
			if (name.StartsWith ("Write", StringComparison.Ordinal) && (type.Contains ("Writer") || type == "Mono.Cecil.ModuleDefinition" || type == "Mono.Cecil.AssemblyDefinition"))
				return "emit";
			if (name.StartsWith ("set_", StringComparison.Ordinal) || name.StartsWith ("add_", StringComparison.Ordinal) ||
				name.StartsWith ("remove_", StringComparison.Ordinal) || name.StartsWith ("Write", StringComparison.Ordinal) ||
				name == "Add" || name.StartsWith ("Remove", StringComparison.Ordinal) ||
				name == "Insert" || name == "Clear" ||
				name == "Append" || name == "Replace" || name == "Emit" || name == "InsertBefore" || name == "InsertAfter" ||
				name.StartsWith ("Set", StringComparison.Ordinal) || name.StartsWith ("OnAdd", StringComparison.Ordinal) ||
				name.StartsWith ("OnSet", StringComparison.Ordinal) || name.StartsWith ("OnRemove", StringComparison.Ordinal) ||
				name.StartsWith ("OnInsert", StringComparison.Ordinal) || name.StartsWith ("OnClear", StringComparison.Ordinal))
				return "write";
			if (name.StartsWith ("get_", StringComparison.Ordinal) ||
				name.StartsWith ("Get", StringComparison.Ordinal) || name.StartsWith ("Read", StringComparison.Ordinal) ||
				name.StartsWith ("Has", StringComparison.Ordinal) || name.StartsWith ("Is", StringComparison.Ordinal) ||
				name == "Contains" || name == "IndexOf" || name == "MoveNext" || name == "ToArray" ||
				name == "Equals" || name == "ToString" || name == "CopyTo")
				return "read";
			return "other";
		}
	}
}
