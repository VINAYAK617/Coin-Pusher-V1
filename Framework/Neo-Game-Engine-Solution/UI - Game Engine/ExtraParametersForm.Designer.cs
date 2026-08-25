namespace UIGameEngine
{
    partial class ExtraParametersForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            InfoLabel = new System.Windows.Forms.Label();
            TemplateTextBox = new System.Windows.Forms.TextBox();
            contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(components);
            JsonParametersTextBox = new System.Windows.Forms.TextBox();
            JsonParametersLabel = new System.Windows.Forms.Label();
            TemplateLabel = new System.Windows.Forms.Label();
            CheckJsonButton = new System.Windows.Forms.Button();
            SaveButton = new System.Windows.Forms.Button();
            SuspendLayout();
            // 
            // InfoLabel
            // 
            InfoLabel.Location = new System.Drawing.Point(12, 9);
            InfoLabel.Name = "InfoLabel";
            InfoLabel.Size = new System.Drawing.Size(357, 22);
            InfoLabel.TabIndex = 0;
            InfoLabel.Text = "Simulate extra parameters sent from the Front End to the engine.";
            // 
            // TemplateTextBox
            // 
            TemplateTextBox.BackColor = System.Drawing.SystemColors.ControlLight;
            TemplateTextBox.CausesValidation = false;
            TemplateTextBox.ImeMode = System.Windows.Forms.ImeMode.NoControl;
            TemplateTextBox.Location = new System.Drawing.Point(385, 94);
            TemplateTextBox.Multiline = true;
            TemplateTextBox.Name = "TemplateTextBox";
            TemplateTextBox.ReadOnly = true;
            TemplateTextBox.Size = new System.Drawing.Size(208, 168);
            TemplateTextBox.TabIndex = 1;
            TemplateTextBox.TabStop = false;
            TemplateTextBox.Text = "{\r\n    \"MyNumber\": 1,\r\n    \"MyBoolean\": true,\r\n    \"MyString\": \"Hello\",\r\n    \"MyObject\": {\r\n        \"SubNumber\": 2,\r\n        \"SubString\": \"Hi\",\r\n    },\r\n    \"MyArray\":[1, 2, 3]\r\n}";
            // 
            // contextMenuStrip1
            // 
            contextMenuStrip1.ImageScalingSize = new System.Drawing.Size(28, 28);
            contextMenuStrip1.Name = "contextMenuStrip1";
            contextMenuStrip1.Size = new System.Drawing.Size(61, 4);
            // 
            // JsonParametersTextBox
            // 
            JsonParametersTextBox.CausesValidation = false;
            JsonParametersTextBox.Location = new System.Drawing.Point(12, 63);
            JsonParametersTextBox.Multiline = true;
            JsonParametersTextBox.Name = "JsonParametersTextBox";
            JsonParametersTextBox.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            JsonParametersTextBox.Size = new System.Drawing.Size(315, 215);
            JsonParametersTextBox.TabIndex = 3;
            JsonParametersTextBox.TabStop = false;
            JsonParametersTextBox.Text = "{\r\n    \"NbLines\": 5,\r\n    \"Token\": \"Spade\"\r\n}";
            JsonParametersTextBox.WordWrap = false;
            // 
            // JsonParametersLabel
            // 
            JsonParametersLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            JsonParametersLabel.AutoSize = true;
            JsonParametersLabel.Location = new System.Drawing.Point(12, 45);
            JsonParametersLabel.Name = "JsonParametersLabel";
            JsonParametersLabel.Size = new System.Drawing.Size(97, 15);
            JsonParametersLabel.TabIndex = 22;
            JsonParametersLabel.Text = "JSON Parameters";
            // 
            // TemplateLabel
            // 
            TemplateLabel.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right;
            TemplateLabel.AutoSize = true;
            TemplateLabel.Location = new System.Drawing.Point(385, 76);
            TemplateLabel.Name = "TemplateLabel";
            TemplateLabel.Size = new System.Drawing.Size(55, 15);
            TemplateLabel.TabIndex = 23;
            TemplateLabel.Text = "Template";
            // 
            // CheckJsonButton
            // 
            CheckJsonButton.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            CheckJsonButton.Location = new System.Drawing.Point(57, 283);
            CheckJsonButton.Margin = new System.Windows.Forms.Padding(2);
            CheckJsonButton.Name = "CheckJsonButton";
            CheckJsonButton.Size = new System.Drawing.Size(123, 23);
            CheckJsonButton.TabIndex = 27;
            CheckJsonButton.Text = "Check JSON";
            CheckJsonButton.UseVisualStyleBackColor = true;
            CheckJsonButton.Click += CheckJsonButton_Click;
            // 
            // SaveButton
            // 
            SaveButton.Anchor = System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right;
            SaveButton.Location = new System.Drawing.Point(252, 316);
            SaveButton.Margin = new System.Windows.Forms.Padding(2);
            SaveButton.MaximumSize = new System.Drawing.Size(143, 51);
            SaveButton.MinimumSize = new System.Drawing.Size(143, 51);
            SaveButton.Name = "SaveButton";
            SaveButton.Size = new System.Drawing.Size(143, 51);
            SaveButton.TabIndex = 28;
            SaveButton.Text = "Save";
            SaveButton.UseVisualStyleBackColor = true;
            SaveButton.Click += SaveButton_Click;
            // 
            // ExtraParametersForm
            // 
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(608, 378);
            Controls.Add(SaveButton);
            Controls.Add(CheckJsonButton);
            Controls.Add(TemplateLabel);
            Controls.Add(JsonParametersLabel);
            Controls.Add(JsonParametersTextBox);
            Controls.Add(TemplateTextBox);
            Controls.Add(InfoLabel);
            Name = "ExtraParametersForm";
            Text = "Extra Parameters";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label InfoLabel;
        private System.Windows.Forms.TextBox TemplateTextBox;
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.Windows.Forms.TextBox JsonParametersTextBox;
        private System.Windows.Forms.Label JsonParametersLabel;
        private System.Windows.Forms.Label TemplateLabel;
        private System.Windows.Forms.Button CheckJsonButton;
        private System.Windows.Forms.Button SaveButton;
    }
}