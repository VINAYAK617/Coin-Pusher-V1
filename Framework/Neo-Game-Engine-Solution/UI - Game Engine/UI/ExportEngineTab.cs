using UIGameEngine.Handlers;

namespace UIGameEngine.UI
{
    internal static class ExportEngineTab
    {
        private static Form1 _form = Form1.Instance;

        public static void SaveExportEnginePath()
        {
            SettingsHandler.SaveSettingValue("ExportEnginePath", _form.ExportEnginePathValue);

        }

        public static void SaveExportEngineGameName()
        {
            SettingsHandler.SaveSettingValue("ExportEngineGameName", _form.ExportEngineGameNameValue);

        }

        public static void SaveExportProfilePath()
        {
            SettingsHandler.SaveSettingValue("ExportProfilePath", _form.ExportProfileGameNameValue);

        }
    }
}
