using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Mono.Cecil.Diagnostics {

	internal sealed class UnavailableTraceValue {
		internal readonly string Reason;

		internal UnavailableTraceValue (string reason)
		{
			Reason = reason;
		}
	}

	internal sealed class TraceValueWriter {

		sealed class ObjectIdentity {
			internal long Id;
			internal long SourceCallId;
			internal string SourcePath;
		}

		readonly ConditionalWeakTable<object, ObjectIdentity> identities = new ConditionalWeakTable<object, ObjectIdentity> ();
		readonly int max_depth = ReadLimit ("CECIL_TRACE_MAX_DEPTH", 6);
		readonly int max_items = ReadLimit ("CECIL_TRACE_MAX_ITEMS", 256);
		readonly int max_bytes = ReadLimit ("CECIL_TRACE_MAX_BYTES", 65536);
		readonly int max_string = ReadLimit ("CECIL_TRACE_MAX_STRING", 16384);
		long next_id;

		internal object Limits {
			get { return TraceJson.Object ("depth", max_depth, "items", max_items, "bytes", max_bytes, "stringCharacters", max_string); }
		}

		static int ReadLimit (string name, int fallback)
		{
			var text = Environment.GetEnvironmentVariable (name);
			if (string.IsNullOrEmpty (text))
				return fallback;
			if (!int.TryParse (text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
				throw new ArgumentException (name + " must be a positive integer.");
			return value;
		}

		ObjectIdentity Identity (object value)
		{
			return identities.GetValue (value, _ => new ObjectIdentity { Id = ++next_id });
		}

		internal long GetIdentityId (object value)
		{
			return Identity (value).Id;
		}

		internal void Bind (object value, long call_id, string path)
		{
			Bind (value, call_id, path, new HashSet<long> (), 0);
		}

		void Bind (object value, long call_id, string path, HashSet<long> visited, int depth)
		{
			if (value == null || value is string || value is UnavailableTraceValue)
				return;
			var type = value.GetType ();
			if (type.IsValueType) {
				if (depth >= max_depth || type.IsPrimitive || type.IsEnum || type == typeof (decimal) ||
					type == typeof (DateTime) || type == typeof (Guid) || type == typeof (TimeSpan))
					return;
				var fields = GetFields (type);
				for (int i = 0; i < Math.Min (fields.Length, max_items); i++) {
					var field = fields [i];
					if (!field.FieldType.IsPointer && !field.FieldType.IsByRef)
						Bind (field.GetValue (value), call_id, path + ".field(" + Uri.EscapeDataString (TypeName (field.DeclaringType)) +
							"," + Uri.EscapeDataString (field.Name) + ")", visited, depth + 1);
				}
				return;
			}
			var identity = Identity (value);
			if (identity.SourceCallId == 0) {
				identity.SourceCallId = call_id;
				identity.SourcePath = path;
			}
			if (depth >= max_depth || !visited.Add (identity.Id))
				return;
			if (value is Array array && array.Length <= max_items) {
				var indices = GetDimensions (array, true);
				foreach (var item in array) {
					Bind (item, call_id, path + "[" + string.Join (",", Array.ConvertAll (indices, index => index.ToString (CultureInfo.InvariantCulture))) + "]", visited, depth + 1);
					for (int dimension = indices.Length - 1; dimension >= 0; dimension--) {
						if (indices [dimension] < array.GetUpperBound (dimension)) {
							indices [dimension]++;
							break;
						}
						indices [dimension] = array.GetLowerBound (dimension);
					}
				}
			}
		}

		internal object Capture (object value)
		{
			return Capture (value, 0, new HashSet<long> ());
		}

		internal object [] CaptureArguments (object [] arguments)
		{
			var snapshots = new object [arguments.Length];
			for (int i = 0; i < arguments.Length; i++)
				snapshots [i] = Capture (arguments [i]);
			return snapshots;
		}

		static string TypeName (Type type)
		{
			return type.AssemblyQualifiedName ?? type.ToString ();
		}

		static Dictionary<string, object> Value (string kind, Type type, object value)
		{
			return TraceJson.Object ("kind", kind, "type", TypeName (type), "reconstruction", "value", "requiresIdentity", false, "value", value);
		}

		static Dictionary<string, object> Missing (Type type, string reason)
		{
			return TraceJson.Object ("kind", "unavailable", "type", type == null ? null : TypeName (type), "reconstruction", "unavailable", "reason", reason);
		}

		object Capture (object value, int depth, HashSet<long> visited)
		{
			if (value == null)
				return TraceJson.Object ("kind", "null", "reconstruction", "value", "requiresIdentity", false);
			if (value is UnavailableTraceValue unavailable)
				return Missing (null, unavailable.Reason);

			var type = value.GetType ();
			if (value is string text) {
				if (text.Length > max_string) {
					var missing = Missing (type, "snapshot-string-limit");
					missing.Add ("length", text.Length);
					return missing;
				}
				if (HasUnpairedSurrogate (text)) {
					var utf16_bytes = new byte [text.Length * 2];
					for (int i = 0; i < text.Length; i++) {
						utf16_bytes [i * 2] = (byte) text [i];
						utf16_bytes [i * 2 + 1] = (byte) (text [i] >> 8);
					}
					var encoded = Value ("string", type, Convert.ToBase64String (utf16_bytes));
					encoded.Add ("encoding", "utf16le-base64");
					return encoded;
				}
				return Value ("string", type, text);
			}
			if (type.IsEnum)
				return Value ("enum", type, Convert.ToString (Convert.ChangeType (value, Enum.GetUnderlyingType (type), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
			if (value is bool)
				return Value ("scalar", type, value);
			if (value is char character)
				return Value ("scalar", type, ((int) character).ToString (CultureInfo.InvariantCulture));
			if (value is float single)
				return Value ("scalar", type, Convert.ToBase64String (BitConverter.GetBytes (single)));
			if (value is double number)
				return Value ("scalar", type, Convert.ToBase64String (BitConverter.GetBytes (number)));
			if (value is decimal decimal_value)
				return Value ("scalar", type, decimal.GetBits (decimal_value));
			if (value is DateTime date)
				return Value ("scalar", type, date.ToBinary ().ToString (CultureInfo.InvariantCulture));
			if (value is Guid guid)
				return Value ("scalar", type, guid.ToString ("D"));
			if (value is TimeSpan duration)
				return Value ("scalar", type, duration.Ticks.ToString (CultureInfo.InvariantCulture));
			if (value is Version version)
				return Value ("scalar", type, version.ToString ());
			if (value is IntPtr || value is UIntPtr)
				return Missing (type, "native-pointer-needs-adapter");
			switch (Type.GetTypeCode (type)) {
			case TypeCode.Byte:
			case TypeCode.SByte:
			case TypeCode.Int16:
			case TypeCode.UInt16:
			case TypeCode.Int32:
			case TypeCode.UInt32:
			case TypeCode.Int64:
			case TypeCode.UInt64:
				return Value ("scalar", type, Convert.ToString (value, CultureInfo.InvariantCulture));
			}

			// Reflection metadata is not a Cecil object graph. Never invoke Cecil getters or ToString here.
			if (value is Type reflected_type && type.Assembly == typeof (Type).Assembly) {
				if ((type.FullName ?? "").StartsWith ("System.Reflection.Emit.", StringComparison.Ordinal))
					return Missing (type, "dynamic-reflection-type-needs-adapter");
				if (reflected_type.AssemblyQualifiedName == null)
					return Missing (type, "open-generic-reflection-type-needs-context");
				if (reflected_type.Assembly.IsDynamic)
					return Missing (type, "dynamic-reflection-type-needs-adapter");
				return Value ("type", typeof (Type), reflected_type.AssemblyQualifiedName);
			}
			if (value is MemberInfo member && type.Assembly == typeof (Type).Assembly) {
				if ((type.FullName ?? "").StartsWith ("System.Reflection.Emit.", StringComparison.Ordinal) || member.Module.Assembly.IsDynamic)
					return Missing (type, "dynamic-reflection-member-needs-adapter");
				var reflected = Value ("member", type, member.Name);
				reflected.Add ("memberType", member.MemberType.ToString ());
				reflected.Add ("declaringType", member.DeclaringType == null ? null : TypeName (member.DeclaringType));
				reflected.Add ("moduleMvid", member.Module.ModuleVersionId.ToString ("D"));
				reflected.Add ("token", member.MetadataToken);
				if (member is MethodInfo method && method.IsGenericMethod) {
					reflected.Add ("isGenericMethodDefinition", method.IsGenericMethodDefinition);
					reflected.Add ("genericArguments", Array.ConvertAll (method.GetGenericArguments (), TypeName));
				}
				return reflected;
			}

			if (type.IsValueType) {
				if (depth >= max_depth)
					return Missing (type, "snapshot-depth-limit");
				var result = TraceJson.Object ("kind", "value", "type", TypeName (type), "reconstruction", "snapshot", "requiresIdentity", false);
				result.Add ("fields", CaptureFields (value, type, depth, visited));
				return result;
			}

			var identity = Identity (value);
			var reference = TraceJson.Object ("kind", "reference", "type", TypeName (type), "id", identity.Id, "requiresIdentity", true,
				"reconstruction", identity.SourceCallId == 0 ? "unavailable" : "reference");
			if (identity.SourceCallId != 0) {
				reference.Add ("sourceCallId", identity.SourceCallId);
				reference.Add ("sourcePath", identity.SourcePath);
			}
			if (!visited.Add (identity.Id)) {
				reference ["reconstruction"] = "reference";
				reference.Add ("sameSnapshot", true);
				return reference;
			}
			if (value is Stream || value is Delegate) {
				reference ["reconstruction"] = "unavailable";
				reference.Add ("reason", value is Stream ? "stream-needs-adapter" : "delegate-needs-driver");
				return reference;
			}
			if (depth >= max_depth && (value is Array || IsOptions (type))) {
				reference ["reconstruction"] = "unavailable";
				reference.Add ("reason", "snapshot-depth-limit");
				return reference;
			}
			if (value is byte [] bytes) {
				reference ["kind"] = "bytes";
				reference.Add ("length", bytes.Length);
				if (bytes.Length > max_bytes) {
					reference ["reconstruction"] = "unavailable";
					reference.Add ("reason", "snapshot-byte-limit");
				} else {
					reference ["reconstruction"] = "snapshot";
					reference.Add ("base64", Convert.ToBase64String (bytes));
				}
				return reference;
			}
			if (value is Array array) {
				reference ["kind"] = "array";
				reference.Add ("lengths", GetDimensions (array, false));
				reference.Add ("lowerBounds", GetDimensions (array, true));
				if (array.Length > max_items) {
					reference ["reconstruction"] = "unavailable";
					reference.Add ("reason", "snapshot-item-limit");
				} else {
					reference ["reconstruction"] = "snapshot";
					var items = new List<object> (array.Length);
					foreach (var item in array)
						items.Add (Capture (item, depth + 1, visited));
					reference.Add ("items", items);
				}
				return reference;
			}
			if (IsOptions (type)) {
				reference ["reconstruction"] = "snapshot";
				reference.Add ("fields", CaptureFields (value, type, depth, visited));
				return reference;
			}
			if (identity.SourceCallId == 0) {
				reference.Add ("reason", "object-has-no-external-producer");
			}
			return reference;
		}

		static bool IsOptions (Type type)
		{
			return type == typeof (ReaderParameters) || type == typeof (WriterParameters) || type == typeof (ModuleParameters);
		}

		object [] CaptureFields (object value, Type type, int depth, HashSet<long> visited)
		{
			var fields = GetFields (type);
			var result = new object [Math.Min (fields.Length, max_items) + (fields.Length > max_items ? 1 : 0)];
			for (int i = 0; i < Math.Min (fields.Length, max_items); i++) {
				var field = fields [i];
				var field_value = field.FieldType.IsPointer || field.FieldType.IsByRef
					? Missing (field.FieldType, "pointer-field-needs-adapter")
					: Capture (field.GetValue (value), depth + 1, visited);
				result [i] = TraceJson.Object ("name", field.Name, "declaringType", TypeName (field.DeclaringType), "value", field_value);
			}
			if (fields.Length > max_items)
				result [result.Length - 1] = TraceJson.Object ("name", null, "declaringType", TypeName (type), "value", Missing (type, "snapshot-field-limit"));
			return result;
		}

		static FieldInfo [] GetFields (Type type)
		{
			var fields = type.GetFields (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			Array.Sort (fields, (left, right) => StringComparer.Ordinal.Compare (left.Name, right.Name));
			return fields;
		}

		static bool HasUnpairedSurrogate (string text)
		{
			for (int i = 0; i < text.Length; i++) {
				if (char.IsHighSurrogate (text [i])) {
					if (++i == text.Length || !char.IsLowSurrogate (text [i]))
						return true;
				} else if (char.IsLowSurrogate (text [i])) {
					return true;
				}
			}
			return false;
		}

		static int [] GetDimensions (Array array, bool lower_bounds)
		{
			var result = new int [array.Rank];
			for (int i = 0; i < result.Length; i++)
				result [i] = lower_bounds ? array.GetLowerBound (i) : array.GetLength (i);
			return result;
		}
	}
}
