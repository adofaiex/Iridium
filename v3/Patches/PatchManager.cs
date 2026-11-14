using HarmonyLib;
using Iridium.Config;
using Iridium.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Iridium.Patches
{
	public static class PatchManager
	{
		private static readonly Dictionary<Type, bool> _activePatches = new();

		private sealed class PatchDef
		{
			public Type Type;
			public object Definition;
			public Func<bool> Condition;
			public string Name;
			public RuntimeKind[] SupportedRuntimes;

			public PatchDef(Type type, object definition, Func<bool> condition)
			{
				Type = type;
				Definition = definition;
				Condition = condition;
				Name = type.FullName ?? type.Name;
				SupportedRuntimes = new[] { RuntimeKind.Mono };
			}
		}

		private static readonly List<PatchDef> _definitions = new();

		static PatchManager()
		{
			DiscoverPatches();
		}

		public static void RegisterPatch(string id, object definition, Func<bool> condition)
		{
			if (definition == null) throw new ArgumentNullException(nameof(definition));
			var def = new PatchDef(definition.GetType(), definition, condition) { Name = id };
			if (definition is IPatchDefinition patchDef)
				def.SupportedRuntimes = patchDef.SupportedRuntimes;
			_definitions.Add(def);
		}

		public static void UpdateAllPatches()
		{
			if (Main.RuntimeHost?.PatchBackend == null) return;

			int succeeded = 0;
			var failures = new List<FailureDetail>();
			var skipNames = new List<string>();

			foreach (var def in _definitions)
			{
				if (!IsRuntimeSupported(def)) { skipNames.Add(def.Name); continue; }
				var result = UpdateSinglePatch(def);
				if (result == null) succeeded++;
				else failures.Add(result);
			}

			int applied = succeeded + failures.Count;
			Main.Logger?.Log($"[PatchManager] {succeeded}/{applied} ok, {failures.Count} failed, {skipNames.Count} skipped");
			if (failures.Count > 0)
			{
				Main.Logger?.Log("[PatchManager] --- FAILURES ---");
				foreach (var f in failures)
					Main.Logger?.Log($"[PatchManager]   {f.Name}: {f.State} ({f.Message})");
			}
			if (skipNames.Count > 0)
			{
				Main.Logger?.Log("[PatchManager] --- SKIPPED (unsupported runtime) ---");
				foreach (var name in skipNames)
					Main.Logger?.Log($"[PatchManager]   {name}");
			}
		}

		public static void ReapplyAllPatches()
		{
			if (Main.RuntimeHost?.PatchBackend == null) return;
			Main.RuntimeHost.PatchBackend.SetPerformanceMode(Main.Settings.patchMode.useILPatch);
			Main.RuntimeHost.PatchBackend.RemoveAll();
			_activePatches.Clear();
			UpdateAllPatches();
		}

		public static void UpdatePatchByType(Type patchType)
		{
			if (Main.RuntimeHost?.PatchBackend == null) return;
			var def = _definitions.Find(d => d.Type == patchType);
			if (def == null)
			{
				Main.Logger?.Warning($"[PatchManager] UpdatePatchByType: no definition for {patchType.Name}");
				return;
			}
			var failure = UpdateSinglePatch(def);
			if (failure != null)
				Main.Logger?.Error(
					$"[PatchManager] {def.Name} update failed: {failure.State} — {failure.Message}");
		}

		public static void UpdateOptimizerPatches()
		{
			if (Main.RuntimeHost?.PatchBackend == null) return;
			foreach (var def in _definitions)
			{
				if (def.Type.Namespace == "Iridium.Modules.AsyncInputOptimize") continue;
				string path = GetIriPatchPath(def.Type);
				if (path != null && path.StartsWith("optimizer"))
					UpdateSinglePatch(def);
			}
		}

		public static void UpdatePatchesByCondition(Func<Type, bool> predicate)
		{
			if (Main.RuntimeHost?.PatchBackend == null) return;
			foreach (var def in _definitions)
				if (predicate(def.Type)) UpdateSinglePatch(def);
		}

		public static void UnpatchAll()
		{
			Main.RuntimeHost?.PatchBackend.RemoveAll();
			_activePatches.Clear();
			Main.Logger?.Log(Localization.Get("PatchManagerUnpatchedAll"));
		}

		private sealed class PatchInfo
		{
			public Type Type;
			public IriPatchAttribute Attr;
			public PatchInfo(Type type, IriPatchAttribute attr) { Type = type; Attr = attr; }
		}

		private static void DiscoverPatches()
		{
			_definitions.Clear();
			var assembly = typeof(PatchManager).Assembly;

			// Pass 1: collect all [IriPatch] types
			var allInfos = assembly.GetTypes()
				.Select(t => new { Type = t, Attr = t.GetCustomAttribute<IriPatchAttribute>(false) })
				.Where(x => x.Attr != null)
				.Select(x => new PatchInfo(x.Type, x.Attr!))
				.ToList();

			// Build lookup: path → list of infos at that path
			var pathLookup = new Dictionary<string, List<PatchInfo>>(StringComparer.OrdinalIgnoreCase);
			foreach (var info in allInfos)
			{
				string path = info.Attr.Path ?? "";
				if (!pathLookup.TryGetValue(path, out var list))
				{
					list = new List<PatchInfo>();
					pathLookup[path] = list;
				}
				list.Add(info);
			}

			// Pass 2: build conditions using ancestor inheritance
			foreach (var info in allInfos)
			{
				var condition = BuildCondition(info.Attr, pathLookup);

				if (typeof(IPatchDefinition).IsAssignableFrom(info.Type))
				{
					object? instance = null;
					try { instance = Activator.CreateInstance(info.Type); }
					catch { Main.Logger?.Error($"[PatchManager] Failed to create: {info.Type.FullName}"); continue; }

					var def = new PatchDef(info.Type, instance!, condition);
					if (instance is IPatchDefinition pd)
						def.SupportedRuntimes = pd.SupportedRuntimes;
					_definitions.Add(def);
				}
				else
				{
					_definitions.Add(new PatchDef(info.Type, info.Type, condition));
				}
			}
		}

		/// <summary>Ancestor path conditions AND the patch's own Pre/Condition; nothing collected means always on.</summary>
		private static Func<bool> BuildCondition(IriPatchAttribute attr, Dictionary<string, List<PatchInfo>> pathLookup)
		{
			if (attr.AlwaysOn) return () => true;

			if (!string.IsNullOrEmpty(attr.RequireMethod))
			{
				var parts = attr.RequireMethod.Split('.');
				if (parts.Length == 2)
				{
					var type = AccessTools.TypeByName(parts[0]);
					if (type == null || AccessTools.Method(type, parts[1]) == null)
						return () => false;
				}
			}

			var conditions = new List<Func<bool>>();

			// Ancestors: first patch with Pre+Condition at each parent level, others skipped.
			string path = attr.Path ?? "";
			if (!string.IsNullOrEmpty(path))
			{
				var ancestorPaths = PatchNode.GetAncestorPaths(path);
				ancestorPaths.RemoveAt(ancestorPaths.Count - 1); // remove current

				foreach (var ancestorPath in ancestorPaths)
				{
					if (!pathLookup.TryGetValue(ancestorPath, out var ancestorInfos)) continue;

					foreach (var ancestor in ancestorInfos)
					{
						if (ancestor.Attr.AlwaysOn) continue;
						string? resolved = ResolveOwnCondition(ancestor.Attr);
						if (resolved != null)
						{
							conditions.Add(PatchNode.ResolveSetting(resolved));
							break;
						}
					}
				}
			}

			// Own condition from Pre + Condition
			string? ownCondition = ResolveOwnCondition(attr);
			if (!string.IsNullOrEmpty(ownCondition))
			{
				foreach (var expr in ownCondition.Split(','))
					conditions.Add(PatchNode.ResolveSetting(expr.Trim()));
			}

			if (conditions.Count == 0) return () => true;
			if (conditions.Count == 1) return conditions[0];

			return () =>
			{
				for (int i = 0; i < conditions.Count; i++)
					if (!conditions[i]()) return false;
				return true;
			};
		}

		/// <summary>Pre + Condition → "group.property" paths; PreTypes pairs each name with its own group.</summary>
		private static string? ResolveOwnCondition(IriPatchAttribute attr)
		{
			if (attr.Pre == null && attr.PreTypes == null) return null;
			if (string.IsNullOrEmpty(attr.Condition)) return null;

			var conditions = attr.Condition.Split(',').Select(c => c.Trim()).ToArray();
			var preTypes = attr.PreTypes;

			// Single Pre type (common case) — all conditions use same group
			if (preTypes == null || preTypes.Length == 0)
			{
				string groupPath = ResolveGroupPath(attr.Pre!) ?? attr.Pre!.Name.ToLower();
				return string.Join(",",
					conditions.Select(c => groupPath + "." + c));
			}

			// Multiple Pre types — pair each condition with its type
			var parts = new List<string>();
			for (int i = 0; i < conditions.Length; i++)
			{
				Type preType = i < preTypes.Length ? preTypes[i] : attr.Pre!;
				string groupPath = ResolveGroupPath(preType) ?? preType.Name.ToLower();
				parts.Add(groupPath + "." + conditions[i]);
			}
			return string.Join(",", parts);
		}

		private static string? ResolveGroupPath(Type preType)
		{
			var field = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)
				.FirstOrDefault(f => f.FieldType == preType);
			return field?.Name;
		}

		private static string? GetIriPatchPath(Type type)
		{
			var attr = type.GetCustomAttribute<IriPatchAttribute>(false);
			return attr?.Path;
		}

		private sealed class FailureDetail
		{
			public string Name = null!;
			public string State = null!;
			public string Message = null!;
		}

		private static bool IsRuntimeSupported(PatchDef def)
		{
			var runtime = Main.RuntimeHost?.Runtime;
			if (runtime == null) return true;
			foreach (var kind in def.SupportedRuntimes)
				if (kind == runtime.Value) return true;
			return false;
		}

		private static FailureDetail? UpdateSinglePatch(PatchDef def)
		{
			bool shouldBeActive = def.Condition();
			bool trackedActive = _activePatches.TryGetValue(def.Type, out bool currentActive) && currentActive;

			if (trackedActive == shouldBeActive) return null;

			if (shouldBeActive)
			{
				var result = ApplyPatch(def);
				if (result == null) _activePatches[def.Type] = true;
				return result;
			}
			else
			{
				var result = RemovePatch(def);
				if (result == null) _activePatches.Remove(def.Type);
				return result;
			}
		}

		private static FailureDetail? ApplyPatch(PatchDef def)
		{
			var backend = Main.RuntimeHost?.PatchBackend;
			if (backend == null)
				return new FailureDetail { Name = def.Name, State = "NoBackend", Message = "Patch backend is null" };
			var result = backend.Apply(def.Name, def.Definition);
			if (!result.Succeeded)
				return new FailureDetail { Name = def.Name, State = result.State.ToString(), Message = result.Message };
			return null;
		}

		private static FailureDetail? RemovePatch(PatchDef def)
		{
			var backend = Main.RuntimeHost?.PatchBackend;
			if (backend == null)
				return new FailureDetail { Name = def.Name, State = "NoBackend", Message = "Patch backend is null" };
			var result = backend.Remove(def.Name, def.Definition);
			if (!result.Succeeded)
				return new FailureDetail { Name = def.Name, State = result.State.ToString(), Message = result.Message };
			return null;
		}
	}
}
