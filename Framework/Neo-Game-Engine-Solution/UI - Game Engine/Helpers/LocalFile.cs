using System.IO;

namespace UIGameEngine.Helpers
{
    internal static class LocalFile
    {
        /// <summary>
        /// Returns a full valid path
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="fileName"></param>
        /// <param name="addTimeStamp"></param>
        /// <param name="fileExtension"></param>
        /// <returns></returns>
        public static string GetFullFilePath(string filePath, string fileName, bool addTimeStamp, string fileExtension, bool overwrite = false)
        {
            if (fileExtension != "" && Path.GetExtension(fileName) != fileExtension)
            {
                if (addTimeStamp)
                    fileName += "_" + Time.CurrentTime;

                fileName += fileExtension;
            }
            else
            {
                if (addTimeStamp)
                {
                    string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                    string extension = Path.GetExtension(fileName);

                    fileName += nameWithoutExtension + "_" + Time.CurrentTime + extension;
                }
            }

            string fullPath = Path.Combine(filePath, fileName);

            while (File.Exists(fullPath) && !overwrite)
            {
                string name = Path.GetFileNameWithoutExtension(fullPath);
                string path = Path.GetDirectoryName(fullPath);
                string extension = Path.GetExtension(fullPath);

                int openBracket = name.LastIndexOf('(');
                int closingBracket = name.LastIndexOf(')');
                int iterator = 1;

                if (closingBracket == name.Length - 1)
                {
                    bool success = int.TryParse(name.Substring(openBracket + 1, closingBracket - openBracket - 1), out iterator);

                    if (success)
                        ++iterator;

                    name = name.Substring(0, openBracket - 1);
                }

                name += " (" + iterator.ToString() + ")" + extension;

                fullPath = Path.Combine(path, name);
            }

            return fullPath;
        }

        /// <summary>
        /// Save ticket to a specific location
        /// </summary>
        /// <param name="ticket">Content of the ticket</param>
        /// <param name="filePath">File path</param>
        /// <param name="fileName">File name</param>
        /// <param name="addTimeStamp">Wether or not a timestamp should be added to the name</param>
        /// <param name="overwrite">Overwrite if already exist</param>
        public static void SaveTicket(string ticket, string filePath, string fileName, bool addTimeStamp, bool overwrite = false)
        {
            string fullPath = GetFullFilePath(filePath, fileName, addTimeStamp, ".json", overwrite);

            //open file stream
            using StreamWriter file = File.CreateText(fullPath);
            file.Write(ticket);
        }

        /// <summary>
        /// Check if a file is already open by another process
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public static bool IsFileInUse(string filePath)
        {
            try
            {
                using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.Close();
                }
            }
            catch (IOException)
            {
                return true;
            }
            return false;
        }
    }
}
