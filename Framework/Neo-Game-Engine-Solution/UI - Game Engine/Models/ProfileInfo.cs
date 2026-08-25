using Neo.ComboGenerator.Core;
using Neo.ComboGenerator.Core.Interfaces;

namespace UIGameEngine.Models
{
    internal class ProfileInfo
    {
        public string Name { get; init; }
        public string DisplayName { get; init; }
        public string Version { get; init; }
        public string ProjectPath { get; init; }
        public BaseProfile Instance { get; init; }
        public ISettings Settings { get; init; }
    }
}
