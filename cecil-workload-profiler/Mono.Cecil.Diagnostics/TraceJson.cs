using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mono.Cecil.Diagnostics {

	internal static class TraceJson {

		internal static Dictionary<string, object> Object (params object [] pairs)
		{
			var result = new Dictionary<string, object> (StringComparer.Ordinal);
			for (int i = 0; i < pairs.Length; i += 2)
				result.Add ((string) pairs [i], pairs [i + 1]);
			return result;
		}

		internal static string Serialize (object value)
		{
			var builder = new StringBuilder ();
			Append (builder, value);
			return builder.ToString ();
		}

		static void Append (StringBuilder builder, object value)
		{
			if (value == null) {
				builder.Append ("null");
				return;
			}
			if (value is string text) {
				Quote (builder, text);
				return;
			}
			if (value is bool boolean) {
				builder.Append (boolean ? "true" : "false");
				return;
			}
			if (value is IDictionary<string, object> properties) {
				builder.Append ('{');
				bool first = true;
				foreach (var property in properties) {
					if (!first)
						builder.Append (',');
					first = false;
					Quote (builder, property.Key);
					builder.Append (':');
					Append (builder, property.Value);
				}
				builder.Append ('}');
				return;
			}
			if (value is IList list) {
				builder.Append ('[');
				for (int i = 0; i < list.Count; i++) {
					if (i != 0)
						builder.Append (',');
					Append (builder, list [i]);
				}
				builder.Append (']');
				return;
			}
			switch (Type.GetTypeCode (value.GetType ())) {
			case TypeCode.Byte:
			case TypeCode.SByte:
			case TypeCode.Int16:
			case TypeCode.UInt16:
			case TypeCode.Int32:
			case TypeCode.UInt32:
			case TypeCode.Int64:
			case TypeCode.UInt64:
				builder.Append (Convert.ToString (value, CultureInfo.InvariantCulture));
				return;
			default:
				throw new InvalidOperationException ("Unexpected trace JSON value type: " + value.GetType ().FullName);
			}
		}

		static void Quote (StringBuilder builder, string value)
		{
			builder.Append ('"');
			foreach (char character in value) {
				switch (character) {
				case '"': builder.Append ("\\\""); break;
				case '\\': builder.Append ("\\\\"); break;
				case '\r': builder.Append ("\\r"); break;
				case '\n': builder.Append ("\\n"); break;
				case '\t': builder.Append ("\\t"); break;
				default:
					if (character < 0x20 || character > 0x7e)
						builder.Append ("\\u").Append (((int) character).ToString ("x4", CultureInfo.InvariantCulture));
					else
						builder.Append (character);
					break;
				}
			}
			builder.Append ('"');
		}
	}
}
