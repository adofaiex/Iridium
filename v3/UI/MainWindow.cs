namespace Iridium.UI
{
    /// <summary>
    /// Forwarding shell over <see cref="ImlWindow"/> so Main.cs call sites stay stable.
    /// </summary>
    public static class MainWindow
    {
        public static void ShowFirstRun() => ImlWindow.ShowFirstRun();

        public static void ShowUpgrade(string messageKey) => ImlWindow.ShowUpgrade(messageKey);
    }
}
