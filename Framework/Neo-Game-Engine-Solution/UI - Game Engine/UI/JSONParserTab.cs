using UIGameEngine.Handlers;

namespace UIGameEngine.UI
{
    internal static class JSONParserTab
    {
        public static Form1 _form = Form1.Instance;

        public static void SaveJSONParserFileName()
        {
            SettingsHandler.SaveSettingValue("JSONParserFileName", _form.JSONParserFileNameValue);

        }

        public static void SaveJSONParserSourceFolder()
        {
            SettingsHandler.SaveSettingValue("JSONParserSourceFolder", _form.JSONParserSourceFolderValue);

        }

        public static void SaveJSONParserExportInSameFolder()
        {
            SettingsHandler.SaveSettingValue("JSONParserExportInSameFolder", _form.JSONParserExportInSameFolderStatus.ToString());

        }

        public static void SaveJSONParserRemoveOriginalFiles()
        {
            SettingsHandler.SaveSettingValue("JSONParserRemoveOriginalFiles", _form.JSONParserRemoveOriginalFilesStatus.ToString());

        }

        public static void SaveJSONParserMergeSubFolders()
        {
            SettingsHandler.SaveSettingValue("JSONParserMergeSubFolders", _form.JSONParserMergeSubFoldersStatus.ToString());

        }
    }
}
