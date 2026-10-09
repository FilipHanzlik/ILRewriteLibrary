using System;

namespace Mono.Cecil.Diagnostics {

	[AttributeUsage (AttributeTargets.Assembly)]
	public sealed class CecilTraceInstrumentedAttribute : Attribute {
		public CecilTraceInstrumentedAttribute ()
		{
		}
	}
}
