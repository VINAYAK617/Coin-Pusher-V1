using Neo.ComboGenerator.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UIGameEngine.Models;

namespace UIGameEngine.Handlers
{
    internal static class ProfileHandler
    {
        private static string _currentPath = Directory.GetCurrentDirectory();
        private static string _profilesPath = Path.Combine(_currentPath, @"Profiles");

        public static string ProfilesPath { get { return _profilesPath; } }

        /// <summary>
        /// Get profiles
        /// </summary>
        /// <returns></returns>
        public static Dictionary<string, ProfileInfo> GetProfiles()
        {
            string solutionPath = Path.GetFullPath(Path.Combine(_currentPath, @"..\..\..\..\"));
            string profilesPath = Path.Combine(solutionPath, "Profiles");

            /// Key: Profile name - Value: Profile info
            Dictionary<string, ProfileInfo> profiles = new Dictionary<string, ProfileInfo>();

            string[] profileDLLs = Directory.GetFiles(_profilesPath, "*.dll", SearchOption.AllDirectories);
            string[] allProjectFiles = Directory.GetFiles(profilesPath, "*.csproj", SearchOption.AllDirectories);

            foreach (string profileDLL in profileDLLs)
            {
                string profileName = Path.GetFileNameWithoutExtension(profileDLL);

                BaseProfile profile = GetProfileInstance(profileName);

                string projectPath = Path.GetDirectoryName(allProjectFiles.Where(x => Path.GetFileNameWithoutExtension(x.ToString()) == profileName).First());


                ProfileInfo profileComboItem = new ProfileInfo()
                {
                    Name = profileName,
                    DisplayName = $"{profileName} - {profile.Version}",
                    Version = profile.Version,
                    ProjectPath = projectPath,
                    Instance = profile,
                    Settings = profile.Settings
                };

                profiles.Add(profileName, profileComboItem);
            }

            return profiles;
        }

        /// <summary>
        /// Return settings from a profile
        /// </summary>
        /// <param name="profileName"></param>
        /// <returns></returns>
        private static BaseProfile GetProfileInstance(string profileName)
        {
            Type profileClass = GetClassTypeFromProfile<BaseProfile>(profileName);

            BaseProfile profile = Activator.CreateInstance(profileClass) as BaseProfile;

            return profile;
        }

        /// <summary>
        /// Return a specific class type from a Profile
        /// </summary>
        /// <typeparam name="T">Class type to retrieve</typeparam>
        /// <param name="profileName">Name of the profile</param>
        /// <returns></returns>
        private static Type GetClassTypeFromProfile<T>(string profileName)
        {
            string dllPath = Path.Combine(_profilesPath, profileName + ".dll");

            Assembly dllAssembly = Assembly.LoadFrom(dllPath);

            Type[] types = dllAssembly.GetTypes();

            List<Type> profileTypes = types.Where(x => x.IsSubclassOf(typeof(T))).ToList();

            return dllAssembly.GetTypes().Where(t => t.FullName.Contains(profileTypes[0].ToString())).FirstOrDefault();
        }


    }
}
