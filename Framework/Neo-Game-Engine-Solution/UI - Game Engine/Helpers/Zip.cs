using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using System.IO;
using System.Linq;
using UIGameEngine.Handlers;
using UIGameEngine.Models;

namespace UIGameEngine.Helpers
{
    internal static class Zip
    {
        public static Form1 _form = Form1.Instance;

        /// <summary>
        /// Zip and save engine dlls
        /// </summary>
        /// <param name="dllsPath"></param>
        /// <param name="outputPath"></param>
        public static void ZipEngineFolderContent(string dllsPath, string outputPath)
        {
            using (FileStream fsOut = File.Create(outputPath))
            {
                using var zipStream = new ZipOutputStream(fsOut);

                //0-9, 9 being the highest level of compression
                zipStream.SetLevel(3);

                // optional. Null is the same as not setting. Required if using AES.
                //zipStream.Password = password;

                // This setting will strip the leading part of the folder path in the entries, 
                // to make the entries relative to the starting folder.
                // To include the full path for each entry up to the drive root, assign to 0.
                int folderOffset = dllsPath.Length + (dllsPath.EndsWith("\\") ? 0 : 1);

                CompressFolder(dllsPath, zipStream, folderOffset);
            }

            _form.AddLog($"Engine exported: {outputPath}");
        }

        public static void ZipProfile(ProfileInfo profile, string outputPath)
        {
            // This setting will strip the leading part of the folder path in the entries, 
            // to make the entries relative to the starting folder.
            // To include the full path for each entry up to the drive root, assign to 0.
            int folderOffset = ProfileHandler.ProfilesPath.Length + (ProfileHandler.ProfilesPath.EndsWith("\\") ? 0 : 1);

            using (FileStream fsOut = File.Create(outputPath))
            {
                using var zipStream = new ZipOutputStream(fsOut);

                //0-9, 9 being the highest level of compression
                zipStream.SetLevel(3);

                // optional. Null is the same as not setting. Required if using AES.
                //zipStream.Password = password;

                string fullFilePath = Path.Combine(ProfileHandler.ProfilesPath, profile.Name + ".dll");

                AddFileToZip(fullFilePath, zipStream, folderOffset);
            }

            _form.AddLog($"Profile exported: {outputPath}");
        }

        // Recursively compresses a folder structure
        private static void CompressFolder(string path, ZipOutputStream zipStream, int folderOffset)
        {

            var files = Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly).Where(s => s.EndsWith(".dll") || s.EndsWith(".deps.json"));

            foreach (var filename in files)
            {
                AddFileToZip(filename, zipStream, folderOffset);
            }

            ZipEntry profileEntry = new("Profiles/");
            zipStream.PutNextEntry(profileEntry);

            // Recursively call CompressFolder on all folders in path
            //var folders = Directory.GetDirectories(path);
            //foreach (var folder in folders)
            //{
            //    CompressFolder(folder, zipStream, folderOffset);
            //}
        }

        private static void AddFileToZip(string fileNameWithFilePath, ZipOutputStream zipStream, int folderOffset)
        {
            var fi = new FileInfo(fileNameWithFilePath);

            // Make the name in zip based on the folder
            var entryName = fileNameWithFilePath.Substring(folderOffset);

            // Remove drive from name and fix slash direction
            entryName = ZipEntry.CleanName(entryName);

            ZipEntry newEntry = new(entryName);

            // Note the zip format stores 2 second granularity
            newEntry.DateTime = fi.LastWriteTime;

            // Specifying the AESKeySize triggers AES encryption. 
            // Allowable values are 0 (off), 128 or 256.
            // A password on the ZipOutputStream is required if using AES.
            //   newEntry.AESKeySize = 256;

            // To permit the zip to be unpacked by built-in extractor in WinXP and Server2003,
            // WinZip 8, Java, and other older code, you need to do one of the following: 
            // Specify UseZip64.Off, or set the Size.
            // If the file may be bigger than 4GB, or you do not need WinXP built-in compatibility, 
            // you do not need either, but the zip will be in Zip64 format which
            // not all utilities can understand.
            //   zipStream.UseZip64 = UseZip64.Off;
            newEntry.Size = fi.Length;

            zipStream.PutNextEntry(newEntry);

            // Zip the file in buffered chunks
            // the "using" will close the stream even if an exception occurs
            var buffer = new byte[4096];
            using (FileStream fsInput = File.OpenRead(fileNameWithFilePath))
            {
                StreamUtils.Copy(fsInput, zipStream, buffer);
            }
            zipStream.CloseEntry();
        }
    }
}
