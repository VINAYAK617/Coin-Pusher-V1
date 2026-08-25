using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UIGameEngine.Helpers
{
    internal static class Time
    {
        public static string CurrentTime => DateTime.Now.ToString("dd.MM.yyy_HH.mm.ss.fff");
    }
}
