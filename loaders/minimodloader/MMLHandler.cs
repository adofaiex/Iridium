using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using ModsTagLib;
using ModsTagLib.Unity;

namespace Iridium.Loader
{
    public class MMLHandler : IHandler
    {
        private readonly Starter _entry;

        public MMLHandler(Starter entry)
        {
            _entry = entry;
        }

        public string ModId => _entry.Info.Id;
        public string ModVersion => _entry.Info.Version;
        public string ModPath => _entry.Path;

        public void Log(string message) => Starter.boot.Log.INFO(message);
        public void Warning(string message) => Starter.boot.Log.WARN(message);
        public void Error(string message) => Starter.boot.Log.ERROR(message);

        internal void EventOnToggleInvoke(bool value)
        {
            OnToggle?.Invoke(value);
        }

        internal void EventOnUpdateInvoke()
        {
            OnUpdate?.Invoke(UnityEngine.Time.deltaTime);
        }

        internal void EventOnGUIInvoke()
        {
            OnGUI?.Invoke();
        }

        internal void EventOnSaveGUIInvoke()
        {
            OnSaveGUI?.Invoke();
        }

        private static string SettingsDirectory(string name)
        {
            return Starter.instance.GetPath(name);
        }

        public T LoadSettings<T>() where T : class, new()
        {
            string settingsPath = SettingsDirectory("Settings.xml");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var serializer = new XmlSerializer(typeof(T));
                    using var reader = new StreamReader(settingsPath);
                    return (T)(serializer.Deserialize(reader) ?? new T());
                }
                catch
                {
                    return new T();
                }
            }
            return new T();
        }

        public void SaveSettings<T>(T settings) where T : class
        {
            string settingsPath = SettingsDirectory("Settings.xml");
            try
            {
                var serializer = new XmlSerializer(typeof(T));
                using var writer = new StreamWriter(settingsPath);
                serializer.Serialize(writer, settings);
            }
            catch (Exception ex)
            {
                Log($"Failed to save settings: {ex.Message}");
            }
        }

        public float UIScale => 1048576;

        public event Action<float>? OnUpdate;
        public event Action<bool>? OnToggle;
        public event Action? OnGUI;
        public event Action? OnSaveGUI;
    }
}
