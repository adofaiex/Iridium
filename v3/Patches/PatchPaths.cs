namespace Iridium.Patches
{
	/// <summary>
	/// Hierarchical path tree — pure structure, no conditions.
	/// Conditions are defined on each patch via <c>[IriPatch(Pre=..., Condition=...)]</c>.
	/// </summary>
	public static class PatchPaths
	{
		// ── Optimizer ──
		public static readonly PatchNode Optimizer = new PatchNode()
			.WithChild("texture", new PatchNode())
			.WithChild("decor", new PatchNode())
			.WithChild("track", new PatchNode())
			.WithChild("scene", new PatchNode())
			.WithChild("loading", new PatchNode())
			.WithChild("ffx", new PatchNode())
			.WithChild("multithread", new PatchNode())
			.WithChild("vram", new PatchNode())
			.WithChild("particle", new PatchNode())
			.WithChild("dotween", new PatchNode())
			.WithChild("eventTween", new PatchNode())
			.WithChild("extreme", new PatchNode())
			.WithChild("playerInput", new PatchNode())
			.WithChild("rdInput", new PatchNode())
			.WithChild("json", new PatchNode())
			.WithChild("tweenSafety", new PatchNode())
			.WithChild("customEasing", new PatchNode())
			.WithChild("editorPerf", new PatchNode())
			.WithChild("deathReset", new PatchNode())
			.WithChild("decorReset", new PatchNode())
			.WithChild("hold", new PatchNode())
			.WithChild("onBeat", new PatchNode())
			.WithChild("spectrum", new PatchNode())
			.WithChild("hitText", new PatchNode())
			.WithChild("editorFloor", new PatchNode()
				.WithChild("insert", new PatchNode()));

		// ── Bugfix ──
		public static readonly PatchNode Bugfix = new PatchNode()
			.WithChild("portal", new PatchNode())
			.WithChild("coopPause", new PatchNode())
			.WithChild("editorPlayReset", new PatchNode())
			.WithChild("turnaround", new PatchNode());

		// ── Compatibility ──
		public static readonly PatchNode Compatibility = new PatchNode()
			.WithChild("legacyPause", new PatchNode())
			.WithChild("noFail", new PatchNode())
			.WithChild("scaleFilter", new PatchNode())
			.WithChild("cameraDrag", new PatchNode())
			.WithChild("forceAngle", new PatchNode())
			.WithChild("legacyBehavior", new PatchNode())
			.WithChild("requiredMods", new PatchNode())
			.WithChild("customEvents", new PatchNode());

		// ── UI ──
		public static readonly PatchNode UI = new PatchNode()
			.WithChild("news", new PatchNode())
			.WithChild("watermark", new PatchNode())
			.WithChild("difficulty", new PatchNode())
			.WithChild("circleArc", new PatchNode())
			.WithChild("autoplayText", new PatchNode())
			.WithChild("countdown", new PatchNode())
			.WithChild("autoplayHint", new PatchNode())
			.WithChild("pauseTrail", new PatchNode())
			.WithChild("lobbyMusic", new PatchNode());

		// ── Sound ──
		public static readonly PatchNode Sound = new PatchNode()
			.WithChild("hitSound", new PatchNode())
			.WithChild("judgeText", new PatchNode())
			.WithChild("judgeTextRotation", new PatchNode());

		// ── Editor ──
		public static readonly PatchNode Editor = new PatchNode()
			.WithChild("shortcuts", new PatchNode())
			.WithChild("pause", new PatchNode());

		// ── AsyncInput ──
		public static readonly PatchNode AsyncInput = new PatchNode();
	}
}
