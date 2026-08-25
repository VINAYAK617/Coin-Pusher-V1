using System.IO;
using System.Windows.Forms;
using UIGameEngine.Handlers;

namespace UIGameEngine.UI
{
    internal static class GenerateTab
    {
        public static Form1 _form => Form1.Instance;

        #region Export
        public static void SaveExportTicketCheckBox()
        {
            SettingsHandler.SaveSettingValue("ExportTicketCheckBox", _form.ExportTicketStatus.ToString());
        }

        public static void SaveExportPath()
        {
            SettingsHandler.SaveSettingValue("ExportPath", _form.ExportPathValue);
        }

        public static void SaveExportFileName()
        {
            SettingsHandler.SaveSettingValue("ExportFileName", _form.ExportFileNameValue);
        }

        public static void SaveAddTimestamp()
        {
            SettingsHandler.SaveSettingValue("ExportAddTimestamp", _form.AddTimestampStatus.ToString());
        }

        public static void SaveUseIncrementalNumbers()
        {
            SettingsHandler.SaveSettingValue("ExportUseIncrementalNumbers", _form.UseIncrementalNumbersStatus.ToString());
        }

        public static void SaveTicketInTierFolder()
        {
            SettingsHandler.SaveSettingValue("ExportTicketInTierFolder", _form.TicketInTierFolderStatus.ToString());
        }

        public static void SaveExportFolderPrefix()
        {
            SettingsHandler.SaveSettingValue("ExportFolderPrefix", _form.ExportFolderPrefixValue);
        }
        #endregion

        #region Tier
        public static void SaveSingleTierState()
        {
            SettingsHandler.SaveSettingValue("SingleTierState", _form.SingleTierRadioButtonStatus.ToString());
        }

        public static void SaveMultipleTiersState()
        {
            SettingsHandler.SaveSettingValue("MultipleTiersState", _form.MultipleTierRadioButtonStatus.ToString());
        }

        public static void SaveSingleTierNumber()
        {
            SettingsHandler.SaveSettingValue("SingleTierNumber", _form.SingleTierNumberValue);
        }

        public static void SaveMinTier()
        {
            SettingsHandler.SaveSettingValue("MinTier", _form.MinTierValue);
        }

        public static void SaveMaxTier()
        {
            SettingsHandler.SaveSettingValue("MaxTier", _form.MaxTierValue);
        }

        public static void SaveNumberOfTicketsPerTiers()
        {
            SettingsHandler.SaveSettingValue("NumberOfTicketsPerTiers", _form.NumberOfTicketsPerTiersValue);
        }

        public static void SaveForceSeedCheckbox()
        {
            SettingsHandler.SaveSettingValue("ForceSeed", _form.ForceSeedStatus.ToString());

        }

        public static void SaveUseExtraParametersCheckbox()
        {
            SettingsHandler.SaveSettingValue("UseExtraParameters", _form.UseExtraParametersStatus.ToString());

        }

        public static void SaveForceSeedTextbox()
        {
            SettingsHandler.SaveSettingValue("ForceSeedValue", _form.ForceSeedValue);

        }

        public static void SaveCheckTicketsOnTheFly()
        {
            SettingsHandler.SaveSettingValue("CheckTicketsOnTheFly", _form.CheckTicketsOnTheFlyStatus.ToString());

        }
        public static void SaveProfileSelected()
        {
            SettingsHandler.SaveSettingValue("ProfileComboBox", _form.ProfileValue);
        }

        public static void SaveStake()
        {
            if (_form is not null)
                SettingsHandler.SaveSettingValue("Stake", _form.StakeValue);
        }
        
        public static void SaveJackpot()
        {
            if (_form is not null)
            {
                SettingsHandler.SaveSettingValue("JackpotValue", _form.JackpotValue);
                SettingsHandler.SaveSettingValue("JackpotLevel", _form.JackpotLevel);
            }
        }

        public static void SaveUseJackpotCheckbox()
        {
            SettingsHandler.SaveSettingValue("UseJackpot", _form.UseJackpotStatus.ToString());

        }

        public static void SaveExportReportCheckbox()
        {
            SettingsHandler.SaveSettingValue("ExportReport", _form.ExportReportStatus.ToString());
        }

        public static void SaveExportReportPath()
        {
            SettingsHandler.SaveSettingValue("ExportReportPath", _form.ExportReportPathValue);
        }
        #endregion
    }
}
