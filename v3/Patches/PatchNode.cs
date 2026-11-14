using System;
using System.Collections.Generic;
using System.Reflection;

namespace Iridium.Patches
{
	/// <summary>Path tree only; gating lives on <see cref="IriPatchAttribute"/>.</summary>
	public sealed class PatchNode
	{
		public Dictionary<string, PatchNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

		public PatchNode() { }

		public PatchNode WithChild(string name, PatchNode child)
		{
			Children[name] = child;
			return this;
		}

		public static PatchNode? Find(string fullPath)
		{
			if (string.IsNullOrEmpty(fullPath)) return null;

			var segments = fullPath.Split('/');
			PatchNode? current = null;

			for (int i = 0; i < segments.Length; i++)
			{
				if (current == null)
				{
					current = FindRootChild(segments[i]);
				}
				else
				{
					if (!current.Children.TryGetValue(segments[i], out current))
						return null;
				}
			}

			return current;
		}

		/// <summary>Root-to-node path prefixes, used to resolve inherited conditions.</summary>
		public static List<string> GetAncestorPaths(string fullPath)
		{
			var result = new List<string>();
			if (string.IsNullOrEmpty(fullPath)) return result;

			var segments = fullPath.Split('/');
			string accumulated = "";
			foreach (var segment in segments)
			{
				accumulated = string.IsNullOrEmpty(accumulated) ? segment : accumulated + "/" + segment;
				result.Add(accumulated);
			}
			return result;
		}

		/// <summary>"group.property" → Func&lt;bool&gt;; a "!" prefix negates, empty/null is always true.</summary>
		public static Func<bool> ResolveSetting(string? settingPath)
		{
			if (string.IsNullOrEmpty(settingPath))
				return () => true;

			var segments = settingPath.Split('.');
			if (segments.Length != 2)
			{
				Main.Logger?.Error($"[PatchNode] Invalid setting path: '{settingPath}' (expected 'group.property')");
				return () => false;
			}

			string groupName = segments[0];
			string propertyName = segments[1];

			bool negate = propertyName.StartsWith("!");
			if (negate) propertyName = propertyName.Substring(1);

			var settingsField = typeof(Settings).GetField(groupName,
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

			if (settingsField == null)
			{
				Main.Logger?.Error($"[PatchNode] Unknown settings group: '{groupName}'");
				return () => false;
			}

			var field = settingsField.FieldType.GetField(propertyName,
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

			if (field == null)
			{
				Main.Logger?.Error($"[PatchNode] Unknown field: '{groupName}.{propertyName}'");
				return () => false;
			}

			// bool fields directly, enums via Convert.ToBoolean
			Type fieldType = field.FieldType;
			Func<object?, bool> converter;
			if (fieldType == typeof(bool))
				converter = obj => (bool)field.GetValue(obj);
			else if (fieldType.IsEnum)
				converter = obj => Convert.ToBoolean(Convert.ToInt32(field.GetValue(obj)));
			else
			{
				Main.Logger?.Error($"[PatchNode] Unsupported field type: '{groupName}.{propertyName}' is {fieldType.Name}");
				return () => false;
			}

			if (negate)
			{
				return () =>
				{
					var settings = Main.Settings;
					if (settings == null) return false;
					var group = settingsField.GetValue(settings);
					if (group == null) return false;
					return !converter(group);
				};
			}

			return () =>
			{
				var settings = Main.Settings;
				if (settings == null) return false;
				var group = settingsField.GetValue(settings);
				if (group == null) return false;
				return converter(group);
			};
		}

		private static PatchNode? FindRootChild(string name)
		{
			var field = typeof(PatchPaths).GetField(name,
				BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
			return field?.GetValue(null) as PatchNode;
		}
	}
}
