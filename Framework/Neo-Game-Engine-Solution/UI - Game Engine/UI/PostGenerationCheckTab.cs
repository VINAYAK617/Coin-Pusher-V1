using UIGameEngine.Handlers;

namespace UIGameEngine.UI
{
    internal static class PostGenerationCheckTab
    {
        public static Form1 _form = Form1.Instance;

        public static void SavePostGenCheckPath()
        {
            SettingsHandler.SaveSettingValue("PostGenCheckPath", _form.PostGenCheckPathValue);
        }

        public static void SavePostGenMoveNonValidTickets()
        {
            SettingsHandler.SaveSettingValue("PostGenMoveNonValidTickets", _form.PostGenMoveNonValidTicketsStatus.ToString());

        }

        public static void SavePostGenRecursiveFolder()
        {
            SettingsHandler.SaveSettingValue("PostGenRecursiveFolder", _form.PostGenRecursiveFolderStatus.ToString());

        }

    }
}
