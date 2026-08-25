using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using UIGameEngine.Handlers;
using System.Web;

namespace UIGameEngine
{
    public partial class ExtraParametersForm : Form
    {
        public ExtraParametersForm()
        {
            InitializeComponent();

            LoadSettings();

            ActiveControl = null;
        }

        private void LoadSettings()
        {
            string jsonParameters = SettingsHandler.ReadSettingValue("JsonParameters");

            if (!String.IsNullOrWhiteSpace(jsonParameters))
                JsonParametersTextBox.Text = jsonParameters;
        }

        private void CheckJsonButton_Click(object sender, EventArgs e)
        {
            IsValidJson(JsonParametersTextBox.Text, true);
        }

        /// <summary>
        /// Check if a string is a valid JSON object
        /// </summary>
        /// <param name="jsonString">JSON string to check</param>
        /// <param name="showSuccess">Wether a message should be displyed if the JSON string is valid</param>
        /// <returns></returns>
        public bool IsValidJson(string jsonString, bool showSuccess = false)
        {
            if (String.IsNullOrWhiteSpace(jsonString)) { return false; }

            jsonString = jsonString.Trim();
            if ((jsonString.StartsWith("{") && jsonString.EndsWith("}")) || //For object
                (jsonString.StartsWith("[") && jsonString.EndsWith("]"))) //For array
            {
                try
                {
                    var obj = JToken.Parse(jsonString);

                    if (showSuccess)
                        MessageBox.Show("The JSON object valid.");

                    return true;
                }
                catch (Exception)
                {
                    MessageBox.Show("The JSON object is invalid. Please check the syntaxe.");
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            bool isValid = IsValidJson(JsonParametersTextBox.Text);

            if (isValid)
            {
                SettingsHandler.SaveSettingValue("JsonParameters", JsonParametersTextBox.Text);

                Close();
            }

        }
    }
}
