using UIGameEngine.Handlers;

namespace UIGameEngine.UI
{
    internal static class OfflineGenerationTab
    {
        public static Form1 _form = Form1.Instance;

        public static void SaveOfflineCombosExportFolder()
        {
            SettingsHandler.SaveSettingValue("OfflineCombosExportFolder", _form.OfflineCombosExportFolderValue);

        }

        public static void SaveOfflineCombosAutoImport()
        {
            SettingsHandler.SaveSettingValue("OfflineCombosAutoImport", _form.OfflineCombosAutoImportStatus.ToString());

        }

        public static void SaveOfflineGenerationTierMin()
        {
            SettingsHandler.SaveSettingValue("OfflineGenerationTierMin", _form.OfflineGenerationTierMinValue);
        }

        public static void SaveOfflineGenerationTierMax()
        {
            SettingsHandler.SaveSettingValue("OfflineGenerationTierMax", _form.OfflineGenerationTierMaxValue);
        }

        public static void SaveOfflineGenerationNbTickets()
        {
            SettingsHandler.SaveSettingValue("OfflineGenerationNbTickets", _form.OfflineGenerationNbTicketsValue);
        }

        public static void SaveOfflineGenerationCheckTickets()
        {
            SettingsHandler.SaveSettingValue("OfflineGenerationCheckTickets", _form.OfflineGenerationCheckTicketsStatus.ToString());

        }
    }
}
