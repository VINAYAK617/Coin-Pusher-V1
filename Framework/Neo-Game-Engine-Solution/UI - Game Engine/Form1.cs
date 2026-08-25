using GameEngine;
using Neo.ComboGenerator;
using Neo.ComboGenerator.Offline;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TicketChecker;
using UIGameEngine.Enums;
using UIGameEngine.Handlers;
using UIGameEngine.Helpers;
using UIGameEngine.Models;

namespace UIGameEngine
{
    public partial class Form1 : Form
    {
        public static Form1 Instance { get; private set; }

        public CancellationTokenSource GenerationCTS = new();

        private bool _isFormClosing = false;

        public Form1()
        {
            InitializeComponent();

            Instance = this;

            Text = Assembly.GetExecutingAssembly().GetName().Name;

            PopulateProfileComboBoxes();

            LoadSettings();

            DisableButton(StopGeneratingButton);

            CombosToExcelSetProgressBarValue(0);
            SetExportReportPathState();

            Checker.ShowErrorCallback += ShowErrorFromTicketChecker;
        }

        #region Getters
        #region Generate Tab
        #region Export
        internal bool ExportTicketStatus => ExportTicketCheckBox.Checked;
        internal string ExportPathValue => ExportPathTextBox.Text;
        internal string ExportFileNameValue => ExportFileNameTextBox.Text;
        internal bool AddTimestampStatus => AddTimestampCheckBox.Checked;
        internal bool UseIncrementalNumbersStatus => UseIncrementalNumbersCheckBox.Checked;
        internal bool TicketInTierFolderStatus => TicketInTierFolderCheckBox.Checked;
        internal string ExportFolderPrefixValue => ExportFolderPrefixTextBox.Text;
        #endregion

        #region Profile
        internal string ProfileValue => GetSelectedProfile(ProfileComboBox).Name;
        internal string StakeValue => StakeTextBox.Text;
        internal string JackpotValue => JackpotTextBox.Text;
        internal string JackpotLevel => JackpotLevelBox.Text;
        internal bool UseJackpotStatus => UseJackpotCheckBox.Checked;
        internal int JackpotLevelValue => (int)decimal.Round(JackpotLevelBox.Value);
        internal bool SingleTierRadioButtonStatus => SingleTierRadioButton.Checked;
        internal bool MultipleTierRadioButtonStatus => MultipleTierRadioButton.Checked;
        internal string SingleTierNumberValue => SingleTierNumberTextBox.Text;
        internal string MinTierValue => MinTierTextBox.Text;
        internal string MaxTierValue => MaxTierTextBox.Text;
        internal string NumberOfTicketsPerTiersValue => NumberOfTicketsPerTiersTextBox.Text;
        internal bool ForceSeedStatus => ForceSeedCheckBox.Checked;
        internal bool UseExtraParametersStatus => UseExtraParametersCheckBox.Checked;
        internal string ForceSeedValue => ForceSeedTextBox.Text;
        internal bool CheckTicketsOnTheFlyStatus => CheckTicketsOnTheFlyCheckBox.Checked;
        internal bool ExportReportStatus => ExportReportCheckBox.Checked;
        internal string ExportReportPathValue => ExportReportPathTextBox.Text;
        #endregion
        #endregion

        #region Post generation check
        internal string PostGenCheckPathValue => PostGenCheckPathTextBox.Text;
        internal bool PostGenMoveNonValidTicketsStatus => PostGenMoveNonValidTicketsCheckBox.Checked;
        internal bool PostGenRecursiveFolderStatus => PostGenRecursiveFolderCheckBox.Checked;
        #endregion

        #region Export engine
        internal string ExportEnginePathValue => ExportEnginePathTextBox.Text;
        internal string ExportEngineGameNameValue => ExportEngineGameNameTextBox.Text;
        internal string ExportProfileGameNameValue => ExportProfilePathTextBox.Text;
        #endregion

        #region JSON Parser
        internal string JSONParserFileNameValue => JSONParserFileNameTextBox.Text;
        internal string JSONParserSourceFolderValue => JSONParserSourceFolderTextBox.Text;
        internal bool JSONParserExportInSameFolderStatus => JSONParserExportInSameFolderCheckBox.Checked;
        internal bool JSONParserRemoveOriginalFilesStatus => JSONParserRemoveOriginalFilesCheckBox.Checked;
        internal bool JSONParserMergeSubFoldersStatus => JSONParserMergeSubFoldersCheckBox.Checked;
        #endregion

        #region Offline generation
        internal string OfflineCombosExportFolderValue => OfflineCombosExportFolderTextBox.Text;
        internal bool OfflineCombosAutoImportStatus => OfflineCombosAutoImportCheckBox.Checked;
        internal string OfflineGenerationTierMinValue => OfflineGenerationTierMinTextBox.Text;
        internal string OfflineGenerationTierMaxValue => OfflineGenerationTierMaxTextBox.Text;
        internal string OfflineGenerationNbTicketsValue => OfflineGenerationNbTicketsTextBox.Text;
        internal bool OfflineGenerationCheckTicketsStatus => OfflineGenerationCheckTicketsCheckBox.Checked;
        #endregion

        #region Export Info
        internal bool CombosToExcelMultiplyPrizesByStakeStatus => CombosToExcelMultiplyPrizesByStakeCheckBox.Checked;
        internal string CombosToExcelStakeCellValue => CombosToExcelStakeCellTextBox.Text;
        #endregion
        #endregion

        #region Settings
        private void LoadSettings()
        {
            void InitialiseProfileComboBox(ComboBox comboBox, string profileName)
            {
                foreach (ProfileInfo item in comboBox.Items)
                {
                    if (item.Name == profileName)
                    {
                        comboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            /*
             * Generate tab
             */
            // Export
            ExportTicketCheckBox.Checked = SettingsHandler.ReadSettingValue("ExportTicketCheckBox") == "True";
            SetExportElementsState(ExportTicketCheckBox.Checked);
            ExportPathTextBox.Text = SettingsHandler.ReadSettingValue("ExportPath");
            ExportFileNameTextBox.Text = SettingsHandler.ReadSettingValue("ExportFileName");
            AddTimestampCheckBox.Checked = SettingsHandler.ReadSettingValue("ExportAddTimestamp") == "True";
            UseIncrementalNumbersCheckBox.Checked = SettingsHandler.ReadSettingValue("ExportUseIncrementalNumbers") == "True";
            ExportFileNameTextBox.Enabled = SettingsHandler.ReadSettingValue("ExportUseIncrementalNumbers") != "True";
            TicketInTierFolderCheckBox.Checked = SettingsHandler.ReadSettingValue("ExportTicketInTierFolder") == "True";
            ExportFolderPrefixTextBox.Text = SettingsHandler.ReadSettingValue("ExportFolderPrefix");
            ExportFolderPrefixTextBox.Enabled = TicketInTierFolderCheckBox.Checked;

            /*
             * Profile
             */
            InitialiseProfileComboBox(ProfileComboBox, SettingsHandler.ReadSettingValue("ProfileComboBox"));
            StakeTextBox.Text = SettingsHandler.ReadSettingValue("Stake", "1");
            UseJackpotCheckBox.Checked = SettingsHandler.ReadSettingValue("UseJackpot") == "True";
            JackpotTextBox.Text = SettingsHandler.ReadSettingValue("JackpotValue");
            JackpotLevelBox.Text = SettingsHandler.ReadSettingValue("JackpotLevel", "1");
            SetJackpotElementStates();
            SingleTierRadioButton.Checked = SettingsHandler.ReadSettingValue("SingleTierState") == "True";
            MultipleTierRadioButton.Checked = SettingsHandler.ReadSettingValue("MultipleTiersState") == "True";
            SetTierElementStates();
            SingleTierNumberTextBox.Text = SettingsHandler.ReadSettingValue("SingleTierNumber");
            MinTierTextBox.Text = SettingsHandler.ReadSettingValue("MinTier");
            MaxTierTextBox.Text = SettingsHandler.ReadSettingValue("MaxTier");
            NumberOfTicketsPerTiersTextBox.Text = SettingsHandler.ReadSettingValue("NumberOfTicketsPerTiers");
            UseExtraParametersCheckBox.Checked = SettingsHandler.ReadSettingValue("UseExtraParameters") == "True";
            ExtraParametersButton.Enabled = UseExtraParametersCheckBox.Checked;
            ForceSeedCheckBox.Checked = SettingsHandler.ReadSettingValue("ForceSeed") == "True";
            ForceSeedTextBox.Enabled = ForceSeedCheckBox.Checked;
            ForceSeedTextBox.Text = SettingsHandler.ReadSettingValue("ForceSeedValue");
            CheckTicketsOnTheFlyCheckBox.Checked = SettingsHandler.ReadSettingValue("CheckTicketsOnTheFly") == "True";
            ExportReportCheckBox.Checked = SettingsHandler.ReadSettingValue("ExportReport") == "True";
            ExportReportPathTextBox.Text = SettingsHandler.ReadSettingValue("ExportReportPath");

            /*
             * Post generation check
             */
            PostGenCheckPathTextBox.Text = SettingsHandler.ReadSettingValue("PostGenCheckPath");
            PostGenMoveNonValidTicketsCheckBox.Checked = SettingsHandler.ReadSettingValue("PostGenMoveNonValidTickets") == "True";
            PostGenRecursiveFolderCheckBox.Checked = SettingsHandler.ReadSettingValue("PostGenRecursiveFolder") == "True";

            /*
             * Export engine
             */
            ExportEnginePathTextBox.Text = SettingsHandler.ReadSettingValue("ExportEnginePath");
            ExportEngineGameNameTextBox.Text = SettingsHandler.ReadSettingValue("ExportEngineGameName");
            ExportProfilePathTextBox.Text = SettingsHandler.ReadSettingValue("ExportProfilePath");

            /*
             * JSON Parser
             */
            JSONParserFileNameTextBox.Text = SettingsHandler.ReadSettingValue("JSONParserFileName");
            JSONParserSourceFolderTextBox.Text = SettingsHandler.ReadSettingValue("JSONParserSourceFolder");
            JSONParserExportInSameFolderCheckBox.Checked = SettingsHandler.ReadSettingValue("JSONParserExportInSameFolder") == "True";
            JSONParserRemoveOriginalFilesCheckBox.Checked = SettingsHandler.ReadSettingValue("JSONParserRemoveOriginalFiles") == "True";
            JSONParserMergeSubFoldersCheckBox.Checked = SettingsHandler.ReadSettingValue("JSONParserMergeSubFolders") == "True";

            /*
             * Offline generation
             */
            OfflineCombosExportFolderTextBox.Text = SettingsHandler.ReadSettingValue("OfflineCombosExportFolder");
            OfflineCombosAutoImportCheckBox.Checked = SettingsHandler.ReadSettingValue("OfflineCombosAutoImport") == "True";
            SetOfflineGenerationElementsState(!OfflineCombosAutoImportCheckBox.Checked);
            StopOfflineGenerationProgressBar();
            OfflineGenerationTierMinTextBox.Text = SettingsHandler.ReadSettingValue("OfflineGenerationTierMin");
            OfflineGenerationTierMaxTextBox.Text = SettingsHandler.ReadSettingValue("OfflineGenerationTierMax");
            OfflineGenerationNbTicketsTextBox.Text = SettingsHandler.ReadSettingValue("OfflineGenerationNbTickets");
            OfflineGenerationCheckTicketsCheckBox.Checked = SettingsHandler.ReadSettingValue("OfflineGenerationCheckTickets") == "True";

            /*
             * Export Info
             */
            CombosToExcelMultiplyPrizesByStakeCheckBox.Checked = SettingsHandler.ReadSettingValue("CombosToExcelMultiplyPrizesByStake") == "True";
            SetCombosToExcelStakeCellState(CombosToExcelMultiplyPrizesByStakeCheckBox.Checked);
            CombosToExcelStakeCellTextBox.Text = SettingsHandler.ReadSettingValue("CombosToExcelStakeCell");
            StopExportInfoProgressBar();
        }
        #endregion

        #region UI Callbacks

        #region Export
        private void ExportTicketCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveExportTicketCheckBox();

            SetExportElementsState(ExportTicketCheckBox.Checked);
        }

        private void SetExportElementsState(bool state)
        {
            ExportPathTextBox.Enabled = state;
            ExportFolderButton.Enabled = state;
            ExportFileNameTextBox.Enabled = state;
            AddTimestampCheckBox.Enabled = state;
            UseIncrementalNumbersCheckBox.Enabled = state;
            TicketInTierFolderCheckBox.Enabled = state;
            ExportFolderPrefixTextBox.Enabled = state;
        }

        private void SetJackpotElementStates()
        {
            JackpotTextBox.Enabled = UseJackpotCheckBox.Checked;
            JackpotLevelBox.Enabled = UseJackpotCheckBox.Checked;
        }

        private void SetTierElementStates()
        {
            SingleTierNumberTextBox.Enabled = SingleTierRadioButton.Checked;

            MinTierTextBox.Enabled = MultipleTierRadioButton.Checked;
            MaxTierTextBox.Enabled = MultipleTierRadioButton.Checked;
        }

        private void ExportPathTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveExportPath();
        }

        private void ExportFolderButton_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            bool exists = Directory.Exists(ExportPathTextBox.Text);

            if (exists)
                fbd.SelectedPath = ExportPathTextBox.Text;

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                ExportPathTextBox.Text = fbd.SelectedPath;
                UI.GenerateTab.SaveExportPath();
            }
        }

        private void ExportFileNameTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveExportFileName();
        }

        private void AddTimestampCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveAddTimestamp();
        }

        private void UseIncrementalNumbersCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            ExportFileNameTextBox.Enabled = !UseIncrementalNumbersCheckBox.Checked;

            UI.GenerateTab.SaveUseIncrementalNumbers();
        }

        private void TicketInTierFolderCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            ExportFolderPrefixTextBox.Enabled = TicketInTierFolderCheckBox.Checked;

            UI.GenerateTab.SaveTicketInTierFolder();
        }

        private void ExportFolderPrefixTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveExportFolderPrefix();
        }
        #endregion

        #region Profile
        private void ProfileComboBox_SelectionChangeCommitted(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveProfileSelected();
        }

        private void StakeTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveStake();
        }

        private void SingleTierRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            SingleTierNumberTextBox.Enabled = ((RadioButton)sender).Checked;

            UI.GenerateTab.SaveSingleTierState();
            UI.GenerateTab.SaveMultipleTiersState();
        }

        private void MultipleTierRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            MinTierTextBox.Enabled = ((RadioButton)sender).Checked;
            MaxTierTextBox.Enabled = ((RadioButton)sender).Checked;

            UI.GenerateTab.SaveSingleTierState();
            UI.GenerateTab.SaveMultipleTiersState();
        }

        private void SingleTierNumberTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveSingleTierNumber();
        }

        private void MinTierTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveMinTier();
        }

        private void MaxTierTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveMaxTier();
        }

        private void NumberOfTicketsPerTiersTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveNumberOfTicketsPerTiers();
        }

        private void ForceSeedCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            ForceSeedTextBox.Enabled = ((CheckBox)sender).Checked;

            UI.GenerateTab.SaveForceSeedCheckbox();
        }

        private void UseJackpotCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            JackpotTextBox.Enabled = ((CheckBox)sender).Checked;
            JackpotLevelBox.Enabled = ((CheckBox)sender).Checked;

            UI.GenerateTab.SaveUseJackpotCheckbox();
        }

        private void UseExtraParametersCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            ExtraParametersButton.Enabled = ((CheckBox)sender).Checked;

            UI.GenerateTab.SaveUseExtraParametersCheckbox();
        }

        private void ForceSeedTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveForceSeedTextbox();
        }

        private void JackpotTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveJackpot();
        }

        private void JackpotLevelBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveJackpot();
        }

        private void CheckTicketsOnTheFlyCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            SetExportReportPathState();
            UI.GenerateTab.SaveCheckTicketsOnTheFly();
        }

        private void ExportReportPathTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.GenerateTab.SaveExportReportPath();
        }

        private void ExportReportCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            SetExportReportPathState();

            UI.GenerateTab.SaveExportReportCheckbox();
        }

        private void SetExportReportPathState()
        {
            bool state = ExportReportCheckBox.Checked && CheckTicketsOnTheFlyCheckBox.Checked;
            ExportReportPathTextBox.Enabled = state;
            ExportReportFolderButton.Enabled = state;
        }

        private void ExportReportFolderButton_Click(object sender, System.EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            bool exists = Directory.Exists(ExportReportPathTextBox.Text);

            if (exists)
                fbd.SelectedPath = ExportReportPathTextBox.Text;
            else
                fbd.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                ExportReportPathTextBox.Text = fbd.SelectedPath;
                UI.GenerateTab.SaveExportReportPath();
            }
        }

        private void StakeTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            // Allow backspace
            if (e.KeyChar == ((int)Keys.Back))
            {
                e.Handled = false;
                return;
            }

            string currentStr = StakeTextBox.Text + e.KeyChar;

            if (Helpers.TextField.IsStringDecimalValue(currentStr))
            {
                e.Handled = false;
                return;
            }

            e.Handled = true;
        }

        private void SingleTierNumberTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void MinTierTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void MaxTierTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void NumberOfTicketsPerTiersTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void ForceSeedTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void JackpotTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }
        #endregion

        #region Post generation check
        private void PostGenCheckPathButton_Click(object sender, EventArgs e)
        {
            using FolderBrowserDialog fbd = new();
            bool exists = Directory.Exists(PostGenCheckPathTextBox.Text);

            if (exists)
                fbd.SelectedPath = ExportPathTextBox.Text;

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                PostGenCheckPathTextBox.Text = fbd.SelectedPath;
                UI.PostGenerationCheckTab.SavePostGenCheckPath();
            }
        }

        private void PostGenCheckPathTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.PostGenerationCheckTab.SavePostGenCheckPath();
        }

        private void PostGenMoveNonValidTicketsCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.PostGenerationCheckTab.SavePostGenMoveNonValidTickets();
        }

        private void PostGenRecursiveFolderCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.PostGenerationCheckTab.SavePostGenRecursiveFolder();
        }
        #endregion

        #region Export engine
        private void ExportEnginePathTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.ExportEngineTab.SaveExportEnginePath();
        }
        private void ExportEngineGameNameTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.ExportEngineTab.SaveExportEngineGameName();
        }

        private void ExportProfileFolderButton_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            bool exists = Directory.Exists(ExportProfilePathTextBox.Text);

            if (exists)
                fbd.SelectedPath = ExportProfilePathTextBox.Text;

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                ExportProfilePathTextBox.Text = fbd.SelectedPath;
                UI.ExportEngineTab.SaveExportProfilePath();
            }
        }

        private void ExportEngineFolderButton_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            bool exists = Directory.Exists(ExportEnginePathTextBox.Text);

            if (exists)
                fbd.SelectedPath = ExportEnginePathTextBox.Text;

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                ExportEnginePathTextBox.Text = fbd.SelectedPath;
                UI.ExportEngineTab.SaveExportEnginePath();
            }
        }

        private void ExportEngineButton_Click(object sender, EventArgs e)
        {
            if (!CheckFieldsForEngineExport())
                return;

#if DEBUG
            MessageBox.Show("You can't export a DEBUG version. Please switch to RELEASE to export the engine.", "Error");
            return;
#endif

            string currentLocation = Directory.GetCurrentDirectory();
            string engineLocation = Path.GetFullPath(Path.Combine(currentLocation, @"..\..\..\..", @"GameEngine\bin\Release\net5.0"));

            Startup startup = new Startup();

            string zipName = $"{ExportEngineGameNameTextBox.Text} - Game engine {startup.EngineVersion}";

            string exportFilePath = Helpers.LocalFile.GetFullFilePath(ExportEnginePathTextBox.Text, zipName, false, ".zip");

            Helpers.Zip.ZipEngineFolderContent(engineLocation, exportFilePath);

        }

        private void ExportProfileButton_Click(object sender, EventArgs e)
        {
            if (!CheckFieldsForProfileExport())
                return;

            //#if DEBUG
            //            MessageBox.Show("You can't export a DEBUG version. Please switch to RELEASE to export the profile.", "Error");
            //            return;
            //#endif

            ProfileInfo profile = GetSelectedProfile(ExportProfileComboBox);


            string currentLocation = Directory.GetCurrentDirectory();
            string engineLocation = Path.GetFullPath(Path.Combine(currentLocation, @"..\..\..\..", @"GameEngine\bin\Release\net5.0"));

            string zipName = $"Profile - {profile.Name} - {profile.Version}";

            string exportFilePath = Helpers.LocalFile.GetFullFilePath(ExportProfilePathTextBox.Text, zipName, false, ".zip");

            Helpers.Zip.ZipProfile(profile, exportFilePath);
        }
        #endregion

        #region JSON parser
        private void JSONParserSourceFolderTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.JSONParserTab.SaveJSONParserSourceFolder();
        }

        private void JSONParserFileNameTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.JSONParserTab.SaveJSONParserFileName();
        }

        private void JSONParserSourceFolderButton_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            bool exists = Directory.Exists(JSONParserSourceFolderTextBox.Text);

            if (exists)
                fbd.SelectedPath = JSONParserSourceFolderTextBox.Text;

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                JSONParserSourceFolderTextBox.Text = fbd.SelectedPath;
                UI.JSONParserTab.SaveJSONParserSourceFolder();
            }
        }

        private void JSONParserExportInSameFolderCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.JSONParserTab.SaveJSONParserExportInSameFolder();
        }

        private void JSONParserRemoveOriginalFilesCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.JSONParserTab.SaveJSONParserRemoveOriginalFiles();
        }

        private void JSONParserMergeSubFoldersCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.JSONParserTab.SaveJSONParserMergeSubFolders();
        }
        #endregion

        #region Offline combos generation
        private void OfflineCombosExportFolderTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.OfflineGenerationTab.SaveOfflineCombosExportFolder();
        }

        private void OfflineCombosExportFolderButton_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            bool exists = Directory.Exists(OfflineCombosExportFolderTextBox.Text);

            if (exists)
                fbd.SelectedPath = OfflineCombosExportFolderTextBox.Text;

            DialogResult result = fbd.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                OfflineCombosExportFolderTextBox.Text = fbd.SelectedPath;
                UI.OfflineGenerationTab.SaveOfflineCombosExportFolder();
            }
        }

        private void OfflineCombosAutoImportCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.OfflineGenerationTab.SaveOfflineCombosAutoImport();

            SetOfflineGenerationElementsState(!OfflineCombosAutoImportCheckBox.Checked);
        }

        private void SetOfflineGenerationElementsState(bool state)
        {
            OfflineCombosExportFolderTextBox.Enabled = state;
        }

        private void OfflineGenerationTierMinTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.OfflineGenerationTab.SaveOfflineGenerationTierMin();
        }

        private void OfflineGenerationTierMaxTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.OfflineGenerationTab.SaveOfflineGenerationTierMax();
        }

        private void OfflineGenerationNbTicketsTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.OfflineGenerationTab.SaveOfflineGenerationNbTickets();
        }

        private void OfflineGenerationTierMinTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void OfflineGenerationTierMaxTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void OfflineGenerationNbTicketsTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = Helpers.TextField.IsNumeric(e);
        }

        private void OfflineGenerationCheckTicketsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            UI.OfflineGenerationTab.SaveOfflineGenerationCheckTickets();
        }
        #endregion

        #region Export Info
        private void CombosToExcelSelectButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                openFileDialog.Filter = "Excel Files|*.xlsx";
                openFileDialog.FilterIndex = 1;
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    // Get the path of specified file
                    string filePath = openFileDialog.FileName;
                    CombosToExcelFilePathTextBox.Text = filePath;
                }
            }
        }

        private void CombosToExcelMultiplyPrizesByStakeCheckBox_CheckStateChanged(object sender, EventArgs e)
        {
            UI.ExportInfoTab.SaveCombosToExcelMultiplyPrizesByStake();
            SetCombosToExcelStakeCellState(CombosToExcelMultiplyPrizesByStakeCheckBox.Checked);
        }

        private void SetCombosToExcelStakeCellState(bool state)
        {
            CombosToExcelStakeCellTextBox.Enabled = state;
        }

        private void CombosToExcelStakeCellTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            // Allow backspace
            if (e.KeyChar == ((int)Keys.Back))
            {
                e.Handled = false;
                return;
            }

            string toCheck = CombosToExcelStakeCellTextBox.Text + e.KeyChar.ToString();

            e.Handled = !TextField.IsExcelCellBeingBuilt(toCheck);
        }

        private void CombosToExcelStakeCellTextBox_TextChanged(object sender, EventArgs e)
        {
            UI.ExportInfoTab.SaveCombosToExcelStakeCell();
        }
        #endregion

        #endregion

        #region Setup
        private void PopulateProfileComboBoxes()
        {
            List<ProfileInfo> allProfiles = GetProfilesComboBoxesDataSource();

            Action<ComboBox> SetupComboBox = (profileComboBox) =>
            {
                //Setup data binding
                profileComboBox.DataSource = allProfiles;
                profileComboBox.DisplayMember = "DisplayName";

                // make it readonly
                profileComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            };

            SetupComboBox(ProfileComboBox);
            SetupComboBox(OfflineCombosExportProfileComboBox);
            SetupComboBox(OfflineTicketsExportProfileComboBox);
            SetupComboBox(ExportProfileComboBox);
            SetupComboBox(PostGenCheckProfileComboBox);
            SetupComboBox(CombosToExcelProfileComboBox);
        }

        /// <summary>
        /// Get data source for profiles combo box
        /// </summary>
        /// <returns></returns>
        private List<ProfileInfo> GetProfilesComboBoxesDataSource()
        {
            Dictionary<string, ProfileInfo> profiles = ProfileHandler.GetProfiles();

            //Build a list
            List<ProfileInfo> dataSource = new List<ProfileInfo>();

            foreach (KeyValuePair<string, ProfileInfo> profile in profiles)
            {
                dataSource.Add(profile.Value);
            }

            return dataSource;
        }
        #endregion

        #region Generate ticket
        private async void GenerateButton_Click(object sender, EventArgs e)
        {
            ProfileInfo profile = GetSelectedProfile(ProfileComboBox);

            if (GenerationCTS.IsCancellationRequested)
                GenerationCTS = new CancellationTokenSource();

            if (!CheckFieldsForGeneration())
                return;

            bool shouldExport = ExportTicketCheckBox.Checked;
            string exportPath = "";
            string fileName = "";
            bool addTimeStamp = false;
            bool incrementalNaming = false;
            bool ticketInFolder = false;
            string folderPrefix = "";

            if (shouldExport)
            {
                exportPath = ExportPathTextBox.Text;
                fileName = ExportFileNameTextBox.Text;

                addTimeStamp = AddTimestampCheckBox.Checked;
                incrementalNaming = UseIncrementalNumbersCheckBox.Checked;
                ticketInFolder = TicketInTierFolderCheckBox.Checked;
                folderPrefix = ExportFolderPrefixTextBox.Text;
            }

            int nbTickets = int.Parse(NumberOfTicketsPerTiersTextBox.Text);

            DisableGenerateButton();

            if (SingleTierRadioButton.Checked)
            {
                int tierNumber = int.Parse(SingleTierNumberTextBox.Text);

                await TicketGenerator.GenerateSingleTierTickets(shouldExport, exportPath, fileName, tierNumber, nbTickets, incrementalNaming, addTimeStamp, ticketInFolder, folderPrefix, profile);
            }
            else if (MultipleTierRadioButton.Checked)
            {
                int minTier = int.Parse(MinTierTextBox.Text);
                int maxTier = int.Parse(MaxTierTextBox.Text);

                await TicketGenerator.GenerateMultipleTiersTickets(minTier, maxTier, shouldExport, exportPath, fileName, nbTickets, incrementalNaming, addTimeStamp, ticketInFolder, folderPrefix, profile);
            }

            EnableGenerateButton();
        }

        private void StopGeneratingButton_Click(object sender, EventArgs e)
        {
            AddLog("Generation stopped.");
            GenerationCTS.Cancel();

            EnableGenerateButton();
        }
        #endregion

        #region Check fields
        private bool CheckFieldsForEngineExport()
        {
            if (!CheckIfTextFieldIsEmpty(ExportEnginePathTextBox.Text, "Export engine path")) return false;
            CheckPath(ExportEnginePathTextBox.Text);

            if (!CheckIfTextFieldIsEmpty(ExportEngineGameNameTextBox.Text, "Export engine file name")) return false;

            return true;
        }

        private bool CheckFieldsForProfileExport()
        {
            if (!CheckIfTextFieldIsEmpty(ExportProfilePathTextBox.Text, "Export profile path")) return false;
            CheckPath(ExportProfilePathTextBox.Text);

            return true;
        }

        private bool CheckFieldsForGeneration()
        {
            if (ExportTicketCheckBox.Checked)
            {
                if (!CheckIfTextFieldIsEmpty(ExportPathTextBox.Text, "Export path")) return false;
                CheckPath(ExportPathTextBox.Text);

                if (!UseIncrementalNumbersCheckBox.Checked)
                {
                    if (!CheckIfTextFieldIsEmpty(ExportFileNameTextBox.Text, "Export file name")) return false;
                }

            }

            if (!SingleTierRadioButton.Checked && !MultipleTierRadioButton.Checked)
            {
                MessageBox.Show("Please select \"Single\" or \"Multiple\" tiers to generate.", "Field Error");
                return false;
            }

            ProfileInfo profile = GetSelectedProfile(ProfileComboBox);

            if (SingleTierRadioButton.Checked)
            {
                if (!CheckIfTextFieldIsEmpty(SingleTierNumberTextBox.Text, "Single tier number")) return false;
                if (!IsInt(SingleTierNumberTextBox.Text, "Single tier number")) return false;

                int tier = int.Parse(SingleTierNumberTextBox.Text);

                if (profile.Settings.Tiers.Find(t => t.TierNumber == tier) == null)
                {
                    MessageBox.Show($"Tier {tier} doesnt exist. Please check settings", "Field Error");
                    return false;
                }
            }


            if (MultipleTierRadioButton.Checked)
            {
                if (!CheckIfTextFieldIsEmpty(MinTierTextBox.Text, "Min tier number")) return false;
                if (!CheckIfTextFieldIsEmpty(MaxTierTextBox.Text, "Max tier number")) return false;
                if (!IsInt(MinTierTextBox.Text, "Min tier number")) return false;
                if (!IsInt(MaxTierTextBox.Text, "Max tier number")) return false;

                int minTier = int.Parse(MinTierTextBox.Text);
                int maxTier = int.Parse(MaxTierTextBox.Text);

                if (minTier > maxTier)
                {
                    MessageBox.Show("Min tier number can not be higher than max tier number", "Field Error");
                    return false;
                }

                for (int i = minTier; i < maxTier; i++)
                {
                    if (profile.Settings.Tiers.Find(t => t.TierNumber == i) == null)
                    {
                        MessageBox.Show($"Tier {i} doesnt exist. Please check settings", "Field Error");
                        return false;
                    }
                }
            }

            if (CheckTicketsOnTheFlyStatus && ExportReportStatus)
            {
                if (!CheckIfTextFieldIsEmpty(ExportReportPathTextBox.Text, "Export report path")) return false;
            }

            if (!CheckIfTextFieldIsEmpty(NumberOfTicketsPerTiersTextBox.Text, "Number of tickets per tier")) return false;
            if (!IsInt(NumberOfTicketsPerTiersTextBox.Text, "Number of tickets per tier")) return false;

            int numbertickets = int.Parse(NumberOfTicketsPerTiersTextBox.Text);

            if (numbertickets < 0)
            {
                MessageBox.Show("Number of tickets can not be negative", "Error");
                return false;
            }

            if (ForceSeedCheckBox.Checked)
            {
                if (!CheckIfTextFieldIsEmpty(ForceSeedTextBox.Text, "Force seed value")) return false;
                if (!IsInt(ForceSeedTextBox.Text, "Force seed value")) return false;
            }

            if (UseJackpotCheckBox.Checked)
            {
                if (!CheckIfTextFieldIsEmpty(JackpotTextBox.Text, "Jackpot value")) return false;
                if (!IsInt(JackpotTextBox.Text, "Jackpot value")) return false;
            }

            string stakeStr = SettingsHandler.ReadSettingValue("Stake");

            if (!CheckIfTextFieldIsEmpty(stakeStr, "Stake", true)) return false;
            if (!IsDecimal(stakeStr, "Stake", true)) return false;

            return true;
        }

        private bool CheckFieldsForJSONParser()
        {
            if (!CheckIfTextFieldIsEmpty(JSONParserSourceFolderTextBox.Text, "Source folder")) return false;
            CheckPath(JSONParserSourceFolderTextBox.Text);

            if (!CheckIfTextFieldIsEmpty(JSONParserFileNameTextBox.Text, "Tickets file name")) return false;

            return true;
        }

        private bool CheckFieldsForOfflineGenerationCombos()
        {
            if (!OfflineCombosAutoImportCheckBox.Checked)
            {
                if (!CheckIfTextFieldIsEmpty(OfflineCombosExportFolderTextBox.Text, "Export folder")) return false;
                CheckPath(OfflineCombosExportFolderTextBox.Text);
            }

            return true;
        }

        private bool CheckFieldsForOfflineGenerationTickets()
        {
            if (!CheckIfTextFieldIsEmpty(OfflineGenerationTierMinTextBox.Text, "Min tier number")) return false;
            if (!CheckIfTextFieldIsEmpty(OfflineGenerationTierMaxTextBox.Text, "Max tier number")) return false;
            if (!IsInt(OfflineGenerationTierMinTextBox.Text, "Min tier number")) return false;
            if (!IsInt(OfflineGenerationTierMaxTextBox.Text, "Max tier number")) return false;

            int minTier = int.Parse(OfflineGenerationTierMinTextBox.Text);
            int maxTier = int.Parse(OfflineGenerationTierMaxTextBox.Text);

            if (minTier > maxTier)
            {
                MessageBox.Show("Min tier number can not be higher than max tier number", "Field Error");
                return false;
            }

            ProfileInfo profile = GetSelectedProfile(OfflineTicketsExportProfileComboBox);

            for (int i = minTier; i < maxTier; i++)
            {
                if (profile.Settings.Tiers.Find(t => t.TierNumber == i) == null)
                {
                    MessageBox.Show($"Tier {i} doesnt exist. Please check settings", "Field Error");
                    return false;
                }
            }

            if (!CheckIfTextFieldIsEmpty(OfflineGenerationNbTicketsTextBox.Text, "Number of tickets per tier")) return false;
            if (!IsInt(OfflineGenerationNbTicketsTextBox.Text, "Number of tickets per tier")) return false;

            int numbertickets = int.Parse(OfflineGenerationNbTicketsTextBox.Text);

            if (numbertickets < 0)
            {
                MessageBox.Show("Number of tickets can not be negative", "Error");
                return false;
            }

            return true;
        }

        private static bool CheckIfTextFieldIsEmpty(string content, string fieldName, bool cheksettings = false)
        {
            if (Helpers.TextField.IsStringEmpty(content))
            {
                MessageBox.Show($"{fieldName} can not be empty." + ((cheksettings) ? "Check settings." : ""), "Error");
                return false;
            }

            return true;
        }

        private static bool IsDecimal(string value, string fieldName, bool cheksettings = false)
        {
            if (!Helpers.TextField.IsStringDecimalValue(value))
            {
                MessageBox.Show($"{fieldName} is not a number." + ((cheksettings) ? "Check settings." : ""), "Error");
                return false;
            }

            return true;
        }

        private static bool IsInt(string value, string fieldName, bool cheksettings = false)
        {
            if (!Helpers.TextField.IsInt(value))
            {
                MessageBox.Show($"{fieldName} is not a number." + ((cheksettings) ? "Check settings." : ""), "Error");
                return false;
            }

            return true;
        }

        private static void CheckPath(string path)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        #endregion

        #region Update UI
        internal void UpdateGenerationCurrentTierNumber(int tierNumber)
        {
            ThreadHelper.SetText(this, CurrentTierNumberLabel, tierNumber + "");
        }

        internal void UpdateGenerationCurrentTierGenerated(int value)
        {
            ThreadHelper.SetText(this, NumberTicketsGeneratedCurrentTierLabel, value.ToString());
        }

        internal void UpdateGenerationCurrentTierGenerated(Object stateInfo)
        {
            var value = (Tuple<int, int>)stateInfo;

            ThreadHelper.SetText(this, NumberTicketsGeneratedCurrentTierLabel, value.Item2.ToString());
        }

        internal void UpdateGenerationTotalTierGenerated(int value)
        {
            ThreadHelper.SetText(this, TotalTicketsGeneratedLabel, value.ToString());
        }

        void UpdatePostGenTotalTicketsChecked(int value)
        {
            ThreadHelper.SetText(this, PostGenNumberOfTicketCheckedValueLabel, value.ToString());
        }

        void UpdatePostGenCurrentTier(int value)
        {
            ThreadHelper.SetText(this, PostGenCurrentTierValueLabel, value.ToString());
        }

        void UpdatePostGenCurrentTierNumberOfTicketsChecked(int value)
        {
            ThreadHelper.SetText(this, PostGenCurrentTierNumberOfTicketsCheckedValueLabel, value.ToString());
        }

        private void EnableButton(Control button)
        {
            ThreadHelper.SetEnable(this, button, true);
        }

        private void DisableButton(Control button)
        {
            ThreadHelper.SetEnable(this, button, false);
        }

        private void EnableGenerateButton()
        {
            EnableButton(GenerateButton);
            DisableButton(StopGeneratingButton);
        }

        private void DisableGenerateButton()
        {
            DisableButton(GenerateButton);
            EnableButton(StopGeneratingButton);
        }

        internal void UpdateOfflineGenerationCurrentTierNumber(int tierNumber)
        {
            ThreadHelper.SetText(this, OfflineGenerationCurrentTierNumberLabel, tierNumber + "");
        }

        internal void UpdateOfflineGenerationCurrentTierGenerated(int value)
        {
            ThreadHelper.SetText(this, OfflineGenerationCurrentTierNumberLabel, value.ToString());
        }

        internal void UpdateOfflineGenerationTotalTierGenerated(int value)
        {
            ThreadHelper.SetText(this, OfflineGenerationTotalTicketsLabel, value.ToString());
        }

        internal void UpdateOfflineGenerationProgressBar(int value)
        {
            ThreadHelper.SetProgressBarValue(this, OfflineGenerationProgressBar, value);
        }
        #endregion

        #region Logs
        public void EmptyLog()
        {
            ThreadHelper.SetText(this, LogTextBox, "");
        }

        public void AddLog(string log)
        {
            ThreadHelper.AddText(this, LogTextBox, log + Environment.NewLine);
        }

        private void LogTextBox_TextChanged(object sender, EventArgs e)
        {
            LogTextBox.SelectionStart = LogTextBox.Text.Length;
            LogTextBox.ScrollToCaret();
        }

        private void ClearLogButton_Click(object sender, EventArgs e)
        {
            EmptyLog();
        }
        #endregion

        #region Ticket checker

        private void ShowErrorFromTicketChecker(string error)
        {
            AddLog(error);
        }

        private async void PostGenCheckButton_Click(object sender, EventArgs e)
        {
            if (CheckIfTextFieldIsEmpty(PostGenCheckPathTextBox.Text, "Folder to check"))
            {
                if (!Directory.Exists(PostGenCheckPathTextBox.Text))
                {
                    MessageBox.Show("The folder to check does not exist", "Error");
                    return;
                }
            }

            bool moveNonValidTickets = PostGenMoveNonValidTicketsCheckBox.Checked;
            bool recursiveFolder = PostGenRecursiveFolderCheckBox.Checked;

            ProfileInfo profile = GetSelectedProfile(PostGenCheckProfileComboBox);

            List<string> folders = new();

            folders.Add(PostGenCheckPathTextBox.Text);

            if (recursiveFolder)
            {
                folders.AddRange(Directory.GetDirectories(PostGenCheckPathTextBox.Text, "*", System.IO.SearchOption.AllDirectories).ToList());
            }

            UpdatePostGenTotalTicketsChecked(0);
            UpdatePostGenCurrentTier(0);
            UpdatePostGenCurrentTierNumberOfTicketsChecked(0);

            int numberOfTicketsChecked = 0;
            int currentTier = 0;
            int currentTierNumberOfTicketsChecked = 0;

            try
            {
                await Task.Run(() =>
                {

                    int errorsCount = 0;

                    for (int f = 0; f < folders.Count; ++f)
                    {
                        string[] files = Directory.GetFiles(folders[f]);

                        for (int i = 0; i < files.Length; ++i)
                        {
                            if (File.Exists(files[i]))
                            {
                                string jsonTicket = "";

                                string[] lines = File.ReadAllLines(files[i]);

                                foreach (string line in lines)
                                    jsonTicket += line;

                                string ticketStatus = Ticket.GetTicketStatusFromJsonString(jsonTicket);

                                if (ticketStatus != "success")
                                {
                                    ++errorsCount;

                                    AddLog($"Errors found in file: {files[i]}");
                                    AddLog($"{ticketStatus}{Environment.NewLine}");

                                    if (moveNonValidTickets)
                                    {
                                        MoveFile(files[i]);
                                    }
                                    continue;
                                }

                                Ticket ticket = Ticket.Load(jsonTicket);

                                if (ticket.Parameters.TierNumber != currentTier)
                                {
                                    currentTier = ticket.Parameters.TierNumber;
                                    UpdatePostGenCurrentTier(currentTier);
                                    currentTierNumberOfTicketsChecked = 0;
                                }


                                if (ticket != null)
                                {
                                    bool hasError = Checker.CheckTicket(jsonTicket, profile.Settings);

                                    if (hasError)
                                    {
                                        ++errorsCount;
                                        string message = "Errors found in file: " + files[i] + Environment.NewLine;
                                        AddLog(message);

                                        if (moveNonValidTickets)
                                        {
                                            MoveFile(files[i]);
                                        }
                                    }

                                    ++numberOfTicketsChecked;
                                    ++currentTierNumberOfTicketsChecked;

                                    UpdatePostGenTotalTicketsChecked(numberOfTicketsChecked);
                                    UpdatePostGenCurrentTierNumberOfTicketsChecked(currentTierNumberOfTicketsChecked);

                                }
                                else
                                {
                                    AddLog("Error - ticket couldn't be loaded.");
                                }
                            }

                        }
                    }

                    if (errorsCount > 0)
                    {
                        AddLog(errorsCount + " defective tickets found");
                    }
                });

                return;
            }
            catch (Exception ex)
            {
                AddLog("An error occuredd. " + ex.Message);
            }
        }

        private static void MoveFile(string sourceFile)
        {
            FileInfo file_info = new(sourceFile);
            string parent_directory_path = file_info.DirectoryName;

            string oldFileName = sourceFile;

            string path = Path.Combine(parent_directory_path, "Non valid"); ;
            string baseName = Path.GetFileNameWithoutExtension(sourceFile);
            string extension = Path.GetExtension(sourceFile);

            string newName = Path.Combine(path, baseName + extension);

            bool exists = Directory.Exists(path);

            if (!exists)
                Directory.CreateDirectory(path);

            File.Move(oldFileName, newName);
        }

        #endregion

        #region JSON Parser
        private async void JSONParserMergeButton_Click(object sender, EventArgs e)
        {
            if (!CheckFieldsForJSONParser())
                return;

            JSONParserMergeButton.Enabled = false;

            if (Path.GetExtension(JSONParserFileNameTextBox.Text) != ".json")
            {
                JSONParserFileNameTextBox.Text += ".json";
            }

            List<Task> allTasks = new();

            if (!JSONParserMergeSubFoldersCheckBox.Checked)
                allTasks.Add(MergeFolderContent(JSONParserSourceFolderTextBox.Text));
            else
            {
                string[] folders = Directory.GetDirectories(JSONParserSourceFolderTextBox.Text);

                foreach (string folder in folders)
                {
                    allTasks.Add(MergeFolderContent(folder));
                }
            }

            await Task.WhenAll(allTasks);

            JSONParserMergeButton.Enabled = true;
        }

        private async Task MergeFolderContent(string folderPath)
        {
            string[] files = Directory.GetFiles(folderPath, "*.json");

            try
            {
                await Task.Run(() =>
                {
                    string finalJSON = "{\"tickets\":[" + Environment.NewLine;

                    for (int i = 0; i < files.Length; ++i)
                    {
                        if (File.Exists(files[i]))
                        {
                            string content = "";

                            string[] lines = File.ReadAllLines(files[i]);

                            foreach (string line in lines)
                                content += line;

                            finalJSON += content;

                            if (i < files.Length - 1)
                            {
                                finalJSON += ",";
                            }

                            finalJSON += Environment.NewLine;
                        }

                    }

                    finalJSON += "]}";

                    if (JSONParserRemoveOriginalFilesCheckBox.Checked)
                    {
                        for (int i = 0; i < files.Length; ++i)
                        {
                            File.Delete(files[i]);
                        }
                    }

                    string fullPath = folderPath;

                    if (!JSONParserExportInSameFolderCheckBox.Checked)
                        fullPath = Path.Combine(folderPath, "Export");

                    if (!Directory.Exists(fullPath))
                    {
                        Directory.CreateDirectory(fullPath);
                    }

                    fullPath = Path.Combine(fullPath, JSONParserFileNameTextBox.Text);

                    File.WriteAllText(fullPath, finalJSON);

                    string result = fullPath + " : " + files.Length + " files merged.";

                    AddLog(result);
                });

            }
            catch (Exception e)
            {
                AddLog("Failed to parse content of \"" + folderPath + "\" - " + e.ToString());
            }
        }
        #endregion

        #region Offline generation
        private async void OfflineCombosGenerateButton_Click(object sender, EventArgs e)
        {
            if (!CheckFieldsForOfflineGenerationCombos())
                return;

            OfflineCombosGenerateButton.Enabled = false;

            StartOfflineGenerationProgressBar(ProgressBarType.MARQUEE);

            ProfileInfo profile = GetSelectedProfile(OfflineCombosExportProfileComboBox);

            List<OfflineTier> allTiers = await Generator.GenerateOfflineCombos(profile.Instance);

            if (OfflineCombosAutoImportCheckBox.Checked)
            {
                AddOfflineCombosToProject(allTiers);
            }
            else
            {
                foreach (OfflineTier tier in allTiers)
                {
                    SaveOfflineTierToLocation(tier, OfflineCombosExportFolderTextBox.Text, tier.TierNumber + ".json");
                }
            }

            StopOfflineGenerationProgressBar();

            OfflineCombosGenerateButton.Enabled = true;
        }

        private void StartOfflineGenerationProgressBar(ProgressBarType type)
        {
            switch (type)
            {
                case ProgressBarType.MARQUEE:
                    OfflineGenerationProgressBar.Style = ProgressBarStyle.Marquee;
                    OfflineGenerationProgressBar.MarqueeAnimationSpeed = 10;
                    break;
                case ProgressBarType.CONTINOUS:
                    OfflineGenerationProgressBar.Style = ProgressBarStyle.Continuous;
                    OfflineGenerationProgressBar.MarqueeAnimationSpeed = 10;
                    break;
                default:
                    break;
            }
        }

        private void StopOfflineGenerationProgressBar()
        {
            OfflineGenerationProgressBar.Style = ProgressBarStyle.Continuous;
            OfflineGenerationProgressBar.MarqueeAnimationSpeed = 0;
        }

        private void SaveOfflineTierToLocation(OfflineTier offlineTier, string path, string fileName)
        {
            string jsonTier = JsonConvert.SerializeObject(offlineTier, Formatting.None, new JsonConverter[] { new StringEnumConverter() });

            Helpers.LocalFile.SaveTicket(jsonTier, path, fileName, false, true);
        }

        private void AddOfflineCombosToProject(List<OfflineTier> allTiers)
        {

            string itemGroupOpenTag = @"  <ItemGroup>";
            string itemGroupCloseTag = @"  </ItemGroup>";

            ProfileInfo profile = GetSelectedProfile(OfflineCombosExportProfileComboBox);

            string currentLocation = Directory.GetCurrentDirectory();
            //string projectLocation = Path.GetFullPath(Path.Combine(currentLocation, @"..\..\..\..", @"ComboGenerator"));
            string csprojFileLocation = Path.Combine(profile.ProjectPath, @"" + profile.Name + ".csproj");
            string resourceLocation = Path.Combine(profile.ProjectPath, @"Resources", @"Combos", @"Tiers");

            if (!Directory.Exists(resourceLocation))
                Directory.CreateDirectory(resourceLocation);

            // Read csproj
            List<string> csprojLines = File.ReadAllLines(csprojFileLocation).ToList();

            List<string> tiersToAdd = new();


            foreach (OfflineTier tier in allTiers)
            {
                string itemGroupTier = $"    <EmbeddedResource Include=\"Resources\\Combos\\Tiers\\{tier.TierNumber}.json\" />";

                if (!csprojLines.Contains(itemGroupTier))
                {
                    tiersToAdd.Add(itemGroupTier);
                }

                SaveOfflineTierToLocation(tier, resourceLocation, tier.TierNumber + ".json");
                AddLog($"Tier {tier.TierNumber} exported to Resources folder: {resourceLocation}\\{tier.TierNumber}.json");
            }

            int endOfProjectIndex = -1;

            for (int i = 0; i < csprojLines.Count; i++)
            {
                string csprojLine = csprojLines[i];

                if (csprojLine.Contains($"    <EmbeddedResource Include=\"Resources\\Combos\\Tiers\\"))
                {
                    int tierNumStartIndex = csprojLine.LastIndexOf('\\') + 1;
                    int tierNumEndIndex = csprojLine.LastIndexOf('.');
                    int length = tierNumEndIndex - tierNumStartIndex;

                    int tierNumber = int.Parse(csprojLine.Substring(tierNumStartIndex, length));

                    if (allTiers.Find(x => x.TierNumber == tierNumber) == null)
                    {
                        csprojLines.RemoveAt(i + 2);
                        csprojLines.RemoveAt(i + 1);
                        csprojLines.RemoveAt(i);
                        csprojLines.RemoveAt(i - 1);

                        --i;

                        string fileToDeletePath = Path.Combine(resourceLocation, $"{tierNumber}.json");

                        try
                        {
                            File.Delete(fileToDeletePath);
                            AddLog($"Tier {tierNumber} deleted from Resources: {fileToDeletePath}");
                        }
                        catch (Exception)
                        {
                            AddLog($"Couldn't delete tier file: {fileToDeletePath}");
                        }
                    }
                }
                else if (csprojLine == "</Project>")
                {
                    endOfProjectIndex = i;
                }
            }

            foreach (string tierToAdd in tiersToAdd)
            {
                csprojLines.Insert(endOfProjectIndex++, itemGroupOpenTag);
                csprojLines.Insert(endOfProjectIndex++, tierToAdd);
                csprojLines.Insert(endOfProjectIndex++, itemGroupCloseTag);
                csprojLines.Insert(endOfProjectIndex++, "");
            }

            // Write the data
            File.WriteAllLines(csprojFileLocation, csprojLines);

            AddLog($"Csproj file updated. {csprojFileLocation}");
        }

        #endregion

        #region Export Info
        private void StartExportInfoProgressBar(ProgressBarType type)
        {
            switch (type)
            {
                case ProgressBarType.MARQUEE:
                    ExportInfoProgressBar.Style = ProgressBarStyle.Marquee;
                    ExportInfoProgressBar.MarqueeAnimationSpeed = 10;
                    break;
                case ProgressBarType.CONTINOUS:
                    ExportInfoProgressBar.Style = ProgressBarStyle.Continuous;
                    //ExportInfoProgressBar.MarqueeAnimationSpeed = 10;
                    break;
                default:
                    break;
            }
        }

        private void StopExportInfoProgressBar()
        {
            ExportInfoProgressBar.Style = ProgressBarStyle.Continuous;
            ExportInfoProgressBar.MarqueeAnimationSpeed = 0;
        }

        /// <summary>
        /// Set progress on progress bar and percentage
        /// </summary>
        /// <param name="value"></param>
        private void CombosToExcelSetProgressBarValue(float value)
        {
            float cappedValue = MathF.Round(value, 2) * 100;

            if (!_isFormClosing)
            {
                ThreadHelper.SetText(this, ExportToExcelPercentProgressionLabel, $"{cappedValue}%");
                ThreadHelper.SetProgressBarValue(this, ExportInfoProgressBar, (int)cappedValue);
            }
        }

        private async void CombosToExcelAddCombosButton_Click(object sender, EventArgs e)
        {
            if (!CheckIfTextFieldIsEmpty(CombosToExcelFilePathTextBox.Text, "Excel file")) return;

            if (!File.Exists(CombosToExcelFilePathTextBox.Text))
            {
                MessageBox.Show($"The file \"{CombosToExcelFilePathTextBox.Text}\" does not exist!", "Error");
            }

            if (CombosToExcelMultiplyPrizesByStakeCheckBox.Checked && !CheckIfTextFieldIsEmpty(CombosToExcelStakeCellTextBox.Text, "Stake cell")) return;

            if (!TextField.IsExcelCell(CombosToExcelStakeCellTextBox.Text))
            {
                MessageBox.Show("Stake cell doesn't have a correct cell format.\nExample: A10 - AD5...", "Error");
                return;
            }

            CombosToExcelAddCombosButton.Enabled = false;

            StartExportInfoProgressBar(ProgressBarType.CONTINOUS);

            ProfileInfo profile = GetSelectedProfile(CombosToExcelProfileComboBox);

            List<OfflineTier> allTiers = await Generator.GenerateOfflineCombos(profile.Instance);

            bool multiplyByStake = CombosToExcelMultiplyPrizesByStakeCheckBox.Checked;
            string stakeCell = CombosToExcelStakeCellTextBox.Text;

            await Task.Run(() =>
            {
                int nbTiersAdded = CSV.ExportCombosToExcel(CombosToExcelFilePathTextBox.Text, allTiers, profile, multiplyByStake, stakeCell, CombosToExcelActionOnExistingTierTabs, CombosToExcelDisplayCurrentTier);

                if (nbTiersAdded > -1)
                {
                    AddLog($"{nbTiersAdded} tiers added to \"{CombosToExcelFilePathTextBox.Text}\"");
                }
            });

            StopExportInfoProgressBar();

            CombosToExcelAddCombosButton.Enabled = true;

            return;
        }

        private void CombosToExcelDisplayCurrentTier(int totalTotalTiersCount, int tierNumber)
        {
            CombosToExcelSetProgressBarValue((float)(tierNumber + 1) / (float)totalTotalTiersCount);
            ThreadHelper.SetText(this, ExportToPPSTierNumberLabel, tierNumber.ToString());
            AddLog($"Processing tier {tierNumber}");
        }

        private bool CombosToExcelActionOnExistingTierTabs(string errorMessage, bool allowYesNoAnswer)
        {
            if (allowYesNoAnswer)
            {
                DialogResult result = MessageBox.Show(errorMessage, "Error", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }

            MessageBox.Show(errorMessage, "Error");

            return false;
        }
        #endregion

        private async void OfflineGenerationGenerateTicketsButton_Click(object sender, EventArgs e)
        {
            if (!CheckFieldsForOfflineGenerationTickets())
                return;

            StartOfflineGenerationProgressBar(ProgressBarType.CONTINOUS);

            UpdateOfflineGenerationCurrentTierGenerated(0);
            UpdateOfflineGenerationTotalTierGenerated(0);

            int minTier = int.Parse(OfflineGenerationTierMinTextBox.Text);
            int maxTier = int.Parse(OfflineGenerationTierMaxTextBox.Text);
            int nbTickets = int.Parse(OfflineGenerationNbTicketsTextBox.Text);

            ProfileInfo profile = GetSelectedProfile(OfflineTicketsExportProfileComboBox);

            await TicketGenerator.GenerateOfflineTickets(minTier, maxTier, 1, nbTickets, OfflineGenerationCheckTicketsCheckBox.Checked, profile);

            StopOfflineGenerationProgressBar();
        }

        private ProfileInfo GetSelectedProfile(ComboBox profileComboBox)
        {
            return profileComboBox.SelectedItem as ProfileInfo;
        }

        private void ExtraParametersButton_Click(object sender, EventArgs e)
        {
            ExtraParametersForm extraParameterForm = new();
            extraParameterForm.ShowDialog();
        }

        #region Closing app
        /// <summary>
        /// Callback when the app is being closed
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            _isFormClosing = true;
        }

        #endregion

    }

}
