using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace UIGameEngine.Helpers
{
    internal static class DLLHandler
    {
        /// <summary>
        /// Load a DLL with its name
        /// </summary>
        /// <param name="dllName">Name of the profile</param>
        /// <returns></returns>
        public static Assembly LoadProfileDLL(string dllName)
        {
            string executingPath = GetExecutingPath();
            
            CheckDLLName(ref dllName);
            string profilePath = Path.Combine(executingPath, dllName);

            Assembly dllAssembly;

            try
            {
                dllAssembly = AssemblyLoadContext.GetLoadContext(Assembly.GetCallingAssembly()).LoadFromAssemblyPath(profilePath);

                if (dllAssembly is null)
                {
                    throw new Exception("Dll assembly is null");
                }
            }
            catch (Exception)
            {
                return null;
            }

            return dllAssembly;
        }

        ///// <summary>
        ///// Get an instance of the Profile contained in an assembly
        ///// </summary>
        ///// <param name="assembly"></param>
        ///// <param name="errorCode"></param>
        ///// <param name="errorComment"></param>
        ///// <returns></returns>
        //public static BaseProfile GetProfileInstanceFromAssembly(Assembly assembly, ref int errorCode, out string errorComment)
        //{
        //    errorComment = "";

        //    Type[] types;
        //    List<Type> profileTypes;

        //    try
        //    {
        //        types = assembly.GetTypes();
        //    }
        //    catch (Exception ex)
        //    {
        //        errorCode = 18;
        //        errorComment = $"Could not load types. - {assembly} - {ex.Message}";
        //        return null;
        //    }

        //    profileTypes = types.Where(x => x.BaseType.IsAbstract && x.BaseType == typeof(BaseProfile)).ToList();

        //    if (profileTypes.Count == 0)
        //    {
        //        errorCode = 16;
        //        return null;
        //    }
        //    else if (profileTypes.Count > 1)
        //    {
        //        errorCode = 17;
        //        return null;
        //    }

        //    Type profileClass = assembly.GetTypes().Where(t => t.FullName.Contains(profileTypes[0].ToString())).FirstOrDefault();

        //    if (profileClass is null)
        //    {
        //        errorCode = 19;
        //        return null;
        //    }

        //    BaseProfile profileInstance = Activator.CreateInstance(profileClass) as BaseProfile;

        //    if (profileInstance is null)
        //    {
        //        errorCode = 20;
        //        return null;
        //    }

        //    return profileInstance;
        //}

        /// <summary>
        /// Make sure the dll name includes the ".dll" extension
        /// </summary>
        /// <param name="dllName"></param>
        private static void CheckDLLName(ref string dllName)
        {
            // Check if the string ends with ".dll" (case-insensitive)
            if (!dllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // If not, add ".dll" to the end
                dllName += ".dll";
            }
        }

        private static string GetExecutingPath()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string location = assembly.Location;
            string dir = Directory.GetParent(location).FullName;

            return dir;
        }

        public static List<Type> FindDerivedType<T>(Assembly assembly)
        {
            var derivedType = assembly.GetTypes()
                .Where(type => type.IsInterface
                    && typeof(T).IsAssignableFrom(type)
                    && type != typeof(T)).ToList();

            return derivedType;
        }

    }
}
