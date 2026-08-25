using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UIGameEngine.Helpers
{
    internal static class TextField
    {
        /// <summary>
        /// Return if a key pressed is a number
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static bool IsNumeric(KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Return if a key pressed is a decimal
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static bool IsDecimal(KeyPressEventArgs e)
        {
            if (e.KeyChar == '.')
                return true;

            return false;
        }

        /// <summary>
        /// Return if the key pressed is a separator
        /// </summary>
        /// <param name="e"></param>
        /// <param name="separator"></param>
        /// <returns></returns>
        public static bool IsSeparator(KeyPressEventArgs e, char separator)
        {
            if (e.KeyChar == separator)
                return true;

            return false;
        }

        /// <summary>
        ///  Returns if a string is a decmial value
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static bool IsStringDecimalValue(string str)
        {
            Regex regex = new Regex(@"^\d+(\.\d{0,2})?$");

            if (regex.IsMatch(str))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        ///  Returns if a value is an int
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool IsInt(string value)
        {
            int v;
            if (value.Length > 0 && !int.TryParse(value, out v))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if a string is empty 
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static bool IsStringEmpty(string str)
        {
            if (string.IsNullOrEmpty(str) || string.IsNullOrWhiteSpace(str))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if a string is an excel cell with format AB32. Allows to just be AB to allow the user to start to type
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static bool IsExcelCellBeingBuilt(string str)
        {
            Regex regex = new Regex(@"^([A-Z])+\d*$");

            if (regex.IsMatch(str.ToUpper()))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if a string is an excel cell with format A32.
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static bool IsExcelCell(string str)
        {
            Regex regex = new Regex(@"^([A-Z])+\d+$");

            if (regex.IsMatch(str.ToUpper()))
            {
                return true;
            }

            return false;
        }
    }
}
