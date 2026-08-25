using Neo.ComboGenerator.Core;
using Neo.ComboGenerator.Core.Interfaces;

namespace Profile1
{
    public class Profile : BaseProfile
    {
        public override ISettings Settings { get; } = new Settings();
    }
}
