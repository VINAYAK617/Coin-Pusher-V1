using System.Windows.Forms;

namespace UIGameEngine.Helpers
{
    public static class ThreadHelper
    {
        delegate void SetEnableCallback(Form f, Control ctrl, bool isEnabled);
        /// <summary>
        /// Set text property of various controls
        /// </summary>
        /// <param name="form">The calling form</param>
        /// <param name="ctrl"></param>
        /// <param name="text"></param>
        public static void SetEnable(Form form, Control ctrl, bool isEnabled)
        {
            // InvokeRequired required compares the thread ID of the 
            // calling thread to the thread ID of the creating thread. 
            // If these threads are different, it returns true. 
            if (ctrl.InvokeRequired)
            {
                SetEnableCallback d = new SetEnableCallback(SetEnable);
                form.Invoke(d, new object[] { form, ctrl, isEnabled });
            }
            else
            {
                ctrl.Enabled = isEnabled;
            }
        }

        delegate void SetTextCallback(Form f, Control ctrl, string text);

        public static void SetText(Form form, Control ctrl, string text)
        {
            // InvokeRequired required compares the thread ID of the 
            // calling thread to the thread ID of the creating thread. 
            // If these threads are different, it returns true. 
            if (ctrl.InvokeRequired)
            {
                SetTextCallback d = new SetTextCallback(SetText);
                form.Invoke(d, new object[] { form, ctrl, text });
            }
            else
            {
                ctrl.Text = text;
            }
        }

        delegate void AddTextCallback(Form f, Control ctrl, string text);

        public static void AddText(Form form, Control ctrl, string text)
        {
            // InvokeRequired required compares the thread ID of the 
            // calling thread to the thread ID of the creating thread. 
            // If these threads are different, it returns true. 
            if (ctrl.InvokeRequired)
            {
                AddTextCallback d = new AddTextCallback(AddText);
                form.Invoke(d, new object[] { form, ctrl, text });
            }
            else
            {
                ctrl.Text += text;
            }
        }

        delegate void SetProgressBarValueCallback(Form form, ProgressBar pb, int value);

        public static void SetProgressBarValue(Form form, ProgressBar pb, int value)
        {
            if (pb.InvokeRequired)
            {
                SetProgressBarValueCallback d = new SetProgressBarValueCallback(SetProgressBarValue);
                form.Invoke(d, new object[] { form, pb, value });
            }
            else
            {
                pb.Value = value;
            }
        }
    }
}
