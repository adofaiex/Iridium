using Iridium.Config;
using System;
using Iridium.Runtime;

namespace Iridium.Patches
{
	/// <summary>
	/// Gating contract for reflection-driven patch registration:
	/// <b>Path</b> is a hierarchy ("optimizer/texture"); a disabled parent disables every child.
	/// <b>Pre</b> (settings group type) + <b>Condition</b> (property names, comma = AND) resolve to
	/// "group.property" paths; <b>PreTypes</b> pairs each name with its own group for cross-class
	/// conditions. With no Pre/Condition the patch inherits its parent path's condition, otherwise
	/// its own condition is AND-ed with the parent. <b>AlwaysOn</b> ignores all of it.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
	public sealed class IriPatchAttribute : Attribute
	{
		/// <summary>Hierarchical path ("optimizer/texture"); parent segments gate children.</summary>
		public string Path { get; set; } = "";

		/// <summary>Settings group type; with <see cref="Condition"/> forms "group.property" paths.</summary>
		public Type? Pre { get; set; }

		/// <summary>Cross-class conditions: one settings group type per <see cref="Condition"/> entry.</summary>
		public Type[]? PreTypes { get; set; }

		/// <summary>Property name(s) in the settings group; comma-separated names are AND-ed.</summary>
		public string? Condition { get; set; }

		/// <summary>Active regardless of path, Pre/Condition and <see cref="RequireMethod"/>.</summary>
		public bool AlwaysOn { get; set; } = false;

		/// <summary>Reflection gate: active only while "TypeName.MethodName" exists.</summary>
		public string? RequireMethod { get; set; }

		public RuntimeKind[] SupportedRuntimes { get; set; } = new[] { RuntimeKind.Mono };
	}
}
