using UIGameEngine.Handlers;

namespace UIGameEngine.UI
{
    internal static class ExportInfoTab
    {
        public static Form1 _form = Form1.Instance;

        public static void SaveCombosToExcelMultiplyPrizesByStake()
        {
            SettingsHandler.SaveSettingValue("CombosToExcelMultiplyPrizesByStake", _form.CombosToExcelMultiplyPrizesByStakeStatus.ToString());
        }

        public static void SaveCombosToExcelStakeCell()
        {
            SettingsHandler.SaveSettingValue("CombosToExcelStakeCell", _form.CombosToExcelStakeCellValue);
        }
    }
}
