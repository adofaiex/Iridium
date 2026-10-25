using MelonLoader;
using MelonLoader.TinyJSON;
using System;
using System.IO;
using System.Runtime.CompilerServices;

[assembly: MelonInfo(typeof(Iridium.IridiumMelonMod), Iridium.BuildInfo.ModName, Iridium.BuildInfo.ModVersion, Iridium.BuildInfo.ModAuthor)]
[assembly: MelonGame("7th Beat Games", "A Dance of Fire and Ice")]

namespace Iridium
{
    public class IridiumMelonMod : MelonMod
    {
        private MelonHandler? _handler;
        private bool loaded;

        public override void OnInitializeMelon()
        {
            _handler = new MelonHandler(this);
            // The desktop MelonLoader target is Mono.
            Iridium.Main.Initialize(_handler, new Iridium.Runtime.MonoRuntimeHost());
            // Fire the "mod enabled" event like every other loader does. Without it
            // AsyncPatchManager never starts and *no* patch is applied — MelonLoader was
            // the only loader that silently skipped this.
            _handler.TriggerToggle(true);
        }

        public override void OnUpdate()
        {
            if (!loaded)
                AppDomain.CurrentDomain.Load(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(MelonAssembly.Location), "Iridium.dll")));
            loaded = true;

            UpdateInject();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UpdateInject()
        {
            _handler?.TriggerUpdate(UnityEngine.Time.deltaTime);
        }

        public override void OnGUI()
        {
            _handler?.TriggerGUI();
        }
    }
}
