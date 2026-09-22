using System;
using System.Runtime.InteropServices;
using Iridium.Runtime;
using ModsTagLib.Unity;
using ModsTagLib.Unity.MiniModLoader;

namespace Iridium.Loader
{

    public sealed class Starter : ModEventSystem
    {
        public static bool __Bootstrap()
        {
            boot = ModLoader.GetCurrentModData();
            // UMM is a Mono-only loader. No runtime detection is needed here.
            return Main.Initialize(handler = new MMLHandler(instance = new()), new MonoRuntimeHost());
        }

        public static BootFile boot;
        public static Starter instance;
        public static MMLHandler handler;

        public Starter() : base(boot)
        {

        }

        protected override void Awake()
        {
            handler.EventOnToggleInvoke(true);
        }

        public static void OnGUI()
        {
            // wtf
            handler.EventOnGUIInvoke();
        }

        protected override void Update()
        {
            handler.EventOnUpdateInvoke();
        }

        private static void Save()
        {
            handler.EventOnSaveGUIInvoke();
        }

        protected override void Exit()
        {
            Save();
        }
        protected override unsafe Pointer CustomEvent(BootFile caller, ulong data)
        {
            if (caller.Id == "modstag.lib.adoconfig")
            {
                switch (data)
                {
                    case 0:
                        break;// return new Pointer((delegate* managed<void>)&OpenGUI);
                    case 1:
                        return new Pointer((delegate* managed<void>)&OnGUI);
                    case 2: // CloseGUI
                        return new Pointer((delegate* managed<void>)&Save);
                    case 0x0100: // event flag
                        return 0;
                    case 0x0200: // sub list, obsolate(iridium)
                        return 0;
                    default:
                        break;
                }    
            }
            throw new Starter.SkipEventException(data);
        }

        protected override void ExceptionReload(Exception e, MethodType e_in)
        {
            throw new NotImplementedException();
        }
    }
}
