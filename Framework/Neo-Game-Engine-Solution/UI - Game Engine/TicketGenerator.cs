using GameEngine;
using Neo.ComboGenerator.Core.Interfaces;
using Neo.ComboGenerator.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TicketChecker;
using UIGameEngine.Handlers;
using UIGameEngine.Helpers;
using UIGameEngine.Models;

namespace UIGameEngine
{
    internal static class TicketGenerator
    {
        private class GenerateTicketInfo
        {
            public int NbTickets;
            public bool IncrementalNaming;
            public int TierNumber;
            public bool TicketInFolder;
            public string CurrentFileName;
            public bool ShouldExport;
            public string ExportPath;
            public string FolderPrefix;
            public bool AddTimeStamp;
            public decimal Stake;
            public int CurrentTicketNumber;
            public int TicketsCount;
            public bool IsMultipleTiers;
            public string ProfileName;
            public ISettings ProfileSettings;

            public GenerateTicketInfo() { }

            public GenerateTicketInfo(GenerateTicketInfo other)
            {
                NbTickets = other.NbTickets;
                IncrementalNaming = other.IncrementalNaming;
                TierNumber = other.TierNumber;
                TicketInFolder = other.TicketInFolder;
                CurrentFileName = other.CurrentFileName;
                ShouldExport = other.ShouldExport;
                ExportPath = other.ExportPath;
                FolderPrefix = other.FolderPrefix;
                AddTimeStamp = other.AddTimeStamp;
                Stake = other.Stake;
                CurrentTicketNumber = other.CurrentTicketNumber;
                TicketsCount = other.TicketsCount;
                IsMultipleTiers = other.IsMultipleTiers;
                ProfileName = other.ProfileName;
                ProfileSettings = other.ProfileSettings;
            }
        }

        private static Form1 _form = Form1.Instance;
        private static AutoResetEvent _currentTierGeneratedEvent = new AutoResetEvent(false);
        private static readonly object _lockUI = new object();
        private static readonly object _lockSaveTicket = new object();

        private static int _counterTotalTierGenerated;
        private static int _counterCurrentTierGenerated;

        private static readonly object _lockVolumeCheck = new object();
        private static Dictionary<int, int> _volumeCheckReport = new();

        /// <summary>
        /// Generate tickets for multiple tiers
        /// </summary>
        /// <param name="minTier"></param>
        /// <param name="maxTier"></param>
        /// <param name="shouldExport"></param>
        /// <param name="exportPath"></param>
        /// <param name="fileName"></param>
        /// <param name="nbTickets"></param>
        /// <param name="incrementalNaming"></param>
        /// <param name="addTimeStamp"></param>
        /// <param name="ticketInFolder"></param>
        /// <param name="folderPrefix"></param>
        /// <param name="profile"></param>
        /// <returns></returns>
        public static async Task GenerateMultipleTiersTickets(int minTier, int maxTier, bool shouldExport, string exportPath, string fileName, int nbTickets, bool incrementalNaming, bool addTimeStamp, bool ticketInFolder, string folderPrefix, ProfileInfo profile)
        {
            _volumeCheckReport.Clear();

            _counterTotalTierGenerated = 0;
            _counterCurrentTierGenerated = 0;

            _form.UpdateGenerationCurrentTierGenerated(_counterCurrentTierGenerated);
            _form.UpdateGenerationTotalTierGenerated(_counterTotalTierGenerated);

            string currentFileName = fileName;
            decimal stake = decimal.Parse(SettingsHandler.ReadSettingValue("Stake"));

            await Task.Run(() =>
            {
                for (int i = minTier; i <= maxTier; i++)
                {
                    if (_form.GenerationCTS.IsCancellationRequested)
                    {
                        break;
                    }

                    int tierNumber = i;

                    lock (_lockVolumeCheck)
                    {
                        if (!_volumeCheckReport.ContainsKey(tierNumber))
                            _volumeCheckReport.Add(tierNumber, 0);
                    }

                    _form.UpdateGenerationCurrentTierNumber(i);
                    GenerateTicketInfo info = new()
                    {
                        NbTickets = nbTickets,
                        CurrentFileName = currentFileName,
                        ExportPath = exportPath,
                        FolderPrefix = folderPrefix,
                        IncrementalNaming = incrementalNaming,
                        ShouldExport = shouldExport,
                        TicketInFolder = ticketInFolder,
                        TierNumber = tierNumber,
                        AddTimeStamp = addTimeStamp,
                        Stake = stake,
                        IsMultipleTiers = true,
                        ProfileName = profile.Name,
                        ProfileSettings = profile.Settings,
                    };

                    GenerateTier(info);
                }
            });

            if (_form.CheckTicketsOnTheFlyStatus && _form.ExportReportStatus)
            {
                ExportVolumeCheckReport(profile, nbTickets);
            }
        }

        private static void ExportVolumeCheckReport(ProfileInfo profile, int nbTickets)
        {
            string filePath = _form.ExportReportPathValue;
            string fileName = $"Volume Check Report - {profile.Name}";
            string fullPath = LocalFile.GetFullFilePath(filePath, fileName, false, ".txt", false);

            using (StreamWriter sw = new(fullPath))
            {
                sw.WriteLine($"Volume check report for the profile: {profile.Name}");
                sw.WriteLine(DateTime.Now.ToString("dddd dd MMMM yyyy - HH:mm:ss"));
                sw.WriteLine();

                foreach (KeyValuePair<int, int> tierResult in _volumeCheckReport)
                {
                    sw.WriteLine($"Tier {tierResult.Key} : {tierResult.Value} tickets out of {nbTickets}. {((float)tierResult.Value / nbTickets) * 100}% success rate.");
                }
            }

            _form.AddLog($"Volume check report saved: {fullPath}");
        }

        /// <summary>
        /// Generate tickets for a specific tier
        /// </summary>
        /// <param name="shouldExport"></param>
        /// <param name="exportPath"></param>
        /// <param name="fileName"></param>
        /// <param name="tierNumber"></param>
        /// <param name="nbTickets"></param>
        /// <param name="incrementalNaming"></param>
        /// <param name="addTimeStamp"></param>
        /// <param name="ticketInFolder"></param>
        /// <param name="folderPrefix"></param>
        /// <param name="profileName"></param>
        /// <returns></returns>
        public static async Task GenerateSingleTierTickets(bool shouldExport, string exportPath, string fileName, int tierNumber, int nbTickets, bool incrementalNaming, bool addTimeStamp, bool ticketInFolder, string folderPrefix, ProfileInfo profile)
        {
            _volumeCheckReport.Clear();

            _counterTotalTierGenerated = 0;
            _counterCurrentTierGenerated = 0;

            _form.UpdateGenerationCurrentTierNumber(tierNumber);
            _form.UpdateGenerationCurrentTierGenerated(_counterCurrentTierGenerated);
            _form.UpdateGenerationTotalTierGenerated(_counterTotalTierGenerated);

            decimal stake = decimal.Parse(SettingsHandler.ReadSettingValue("Stake"));

            await Task.Run(() =>
            {
                GenerateTicketInfo info = new()
                {
                    NbTickets = nbTickets,
                    CurrentFileName = fileName,
                    ExportPath = exportPath,
                    FolderPrefix = folderPrefix,
                    IncrementalNaming = incrementalNaming,
                    ShouldExport = shouldExport,
                    TicketInFolder = ticketInFolder,
                    TierNumber = tierNumber,
                    AddTimeStamp = addTimeStamp,
                    Stake = stake,
                    IsMultipleTiers = false,
                    ProfileName = profile.Name,
                    ProfileSettings = profile.Settings
                };

                lock (_lockVolumeCheck)
                {
                    if (!_volumeCheckReport.ContainsKey(tierNumber))
                        _volumeCheckReport.Add(tierNumber, 0);
                }

                GenerateTier(info);
            });

            if (_form.CheckTicketsOnTheFlyStatus && _form.ExportReportStatus)
            {
                ExportVolumeCheckReport(profile, nbTickets);
            }
        }

        /// <summary>
        /// Generate all the tickets for a specific tier
        /// </summary>
        /// <param name="stateInfo"></param>
        private static void GenerateTier(Object stateInfo)
        {
            GenerateTicketInfo info = (GenerateTicketInfo)stateInfo;

            _counterCurrentTierGenerated = 0;

            for (int j = 0; j < info.NbTickets; j++)
            {
                if (_form.GenerationCTS.IsCancellationRequested)
                    return;

                GenerateTicketInfo newInfo = new(info);
                newInfo.CurrentTicketNumber = j;
                ThreadPool.QueueUserWorkItem(new WaitCallback(GenerateAndExportTicket), newInfo);

            }

            while (true)
            {
                if (_currentTierGeneratedEvent.WaitOne(0) ||
                _form.GenerationCTS.IsCancellationRequested)
                {
                    break;
                }
            }

        }

        /// <summary>
        /// Generate and export a ticket if necessary
        /// </summary>
        /// <param name="stateInfo">Object of type GenerateTicketInfo</param>
        private static void GenerateAndExportTicket(Object stateInfo)
        {
            if (_form.GenerationCTS.IsCancellationRequested)
                return;

            GenerateTicketInfo info = (GenerateTicketInfo)stateInfo;

            // Get ticket
            string ticket = QueryEngine(info.TierNumber, info.Stake, info.ProfileName, info.ProfileSettings);

            // Export the ticket
            if (info.ShouldExport && ticket != null)
            {
                // Use incremental value as name
                if (info.IncrementalNaming)
                {
                    if (info.IsMultipleTiers)
                    {
                        if (!info.TicketInFolder)
                            info.CurrentFileName = _counterTotalTierGenerated.ToString();
                        else
                            info.CurrentFileName = info.CurrentTicketNumber.ToString();
                    }
                    else
                        info.CurrentFileName = info.CurrentTicketNumber.ToString();
                }

                // Get the full export path
                string currentExportPath = GetFilePath(info.ExportPath, info.TicketInFolder, info.TierNumber.ToString(), info.FolderPrefix);

                // Save the ticket locally
                lock (_lockSaveTicket)
                    Helpers.LocalFile.SaveTicket(ticket, currentExportPath, info.CurrentFileName, info.AddTimeStamp);
            }

            // Check ticket
            if (_form.CheckTicketsOnTheFlyStatus)
            {
                bool hasError = CheckTicketDuringLiveGeneration(ticket, info.ProfileSettings);

                if (!hasError)
                {
                    lock (_lockVolumeCheck)
                    {
                        _volumeCheckReport[info.TierNumber]++;
                    }
                }
            }

            // Update UI
            lock (_lockUI)
            {
                _counterCurrentTierGenerated++;
                _counterTotalTierGenerated++;
                _form.UpdateGenerationCurrentTierGenerated(_counterCurrentTierGenerated);
                _form.UpdateGenerationTotalTierGenerated(_counterTotalTierGenerated);
            }

            // The whole tier has been generated
            if (_counterCurrentTierGenerated == info.NbTickets)
            {
                _currentTierGeneratedEvent.Set();
            }
        }

        /// <summary>
        /// Query the engine and returns a JSON ticket as a string
        /// </summary>
        /// <param name="profile"></param>
        /// <param name="stake"></param>
        /// <returns></returns>
        private static string QueryEngine(int tier, decimal stake, string profileName, ISettings setting)
        {
            Tier tierConfig = setting.Tiers.Where(x => x.TierNumber == tier).First();

            Decimal cashWin = tierConfig.StakeMultiplier * stake;
            String currency = "$";
            int versionNum = 1;
            Decimal[] individualPrizes = new decimal[0];
            int[] indivdualPrizeSpecifiers = new int[0];

            Startup engine;

            if (_form.ForceSeedStatus)
            {
                int seed = int.Parse(_form.ForceSeedValue);

                engine = new Startup(seed);
            }
            else
            {
                engine = new Startup();

            }

            decimal jackpotValue = 0;

            if (_form.UseJackpotStatus)
                jackpotValue = decimal.Parse(_form.JackpotValue);

            int[] winTiers = new int[] { tier, _form.UseJackpotStatus ? _form.JackpotLevelValue : 0 };
            decimal[] cashWins = new decimal[] { cashWin, jackpotValue };

            string privateState = GetFakePrivateState();

            return engine.GenerateGame(winTiers, cashWins, currency, versionNum, individualPrizes, indivdualPrizeSpecifiers, stake, null, profileName, privateState);

        }

        /// <summary>
        /// Return a private state to generate a ticket
        /// </summary>
        /// <returns></returns>
        private static string GetFakePrivateState()
        {
            string platformReponse = "";
            platformReponse += "{";
            platformReponse += "\"publicState\":{";
            platformReponse += "\"action\":\"start\",";

            // Add extra parameters if necessary
            if (_form.UseExtraParametersStatus)
            {
                string jsonParameters = SettingsHandler.ReadSettingValue("JsonParameters");

                if (!String.IsNullOrWhiteSpace(jsonParameters))
                {
                    jsonParameters = jsonParameters.Trim();

                    if (jsonParameters.StartsWith("{") && jsonParameters.EndsWith("}"))
                    {
                        jsonParameters = jsonParameters.Substring(1, jsonParameters.Length - 2);
                    }

                    platformReponse += jsonParameters;

                }
            }

            platformReponse += "},";
            platformReponse += "\"privateState\":{";

            platformReponse += "}";

            platformReponse += "}";
            return platformReponse;
        }

        /// <summary>
        /// Get file path to export ticket
        /// </summary>
        /// <param name="exportPath"></param>
        /// <param name="ticketInTierFolder"></param>
        /// <param name="currentTier"></param>
        /// <param name="folderPrefix"></param>
        /// <returns></returns>
        private static string GetFilePath(string exportPath, bool ticketInTierFolder, string currentTier, string folderPrefix)
        {
            string currentPath = exportPath;

            if (ticketInTierFolder)
            {
                if (!Helpers.TextField.IsStringEmpty(folderPrefix))
                {
                    currentPath = Path.Combine(currentPath, folderPrefix + currentTier);
                }
                else
                {
                    currentPath = Path.Combine(currentPath, currentTier);
                }
            }

            if (!Directory.Exists(currentPath))
            {
                Directory.CreateDirectory(currentPath);
            }

            return currentPath;
        }

        /// <summary>
        /// Check ticket. Return true if errors were found.
        /// </summary>
        /// <param name="ticket"></param>
        private static bool CheckTicketDuringLiveGeneration(string ticket, ISettings settings)
        {
            if (ticket != null)
            {
                string ticketStatus = Ticket.GetTicketStatusFromJsonString(ticket);

                if (ticketStatus == "success")
                {
                    bool hasError = Checker.CheckTicket(ticket, settings);

                    if (hasError)
                    {
                        _form.AddLog($"{ticket}{Environment.NewLine}");
                        return true;
                    }
                }
                else
                {
                    _form.AddLog($"Error with ticket => {ticket}");
                    _form.AddLog($"{ticketStatus}{Environment.NewLine}");

                    return true;
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Generate offline tickets and add them to project
        /// </summary>
        /// <param name="minTier"></param>
        /// <param name="maxTier"></param>
        /// <param name="stake"></param>
        /// <param name="nbTickets"></param>
        /// <param name="checkTickets"></param>
        /// <returns></returns>
        public static async Task GenerateOfflineTickets(int minTier, int maxTier, decimal stake, int nbTickets, bool checkTickets, ProfileInfo profile)
        {
            int counter = 0;
            int nbMaxTickets = (maxTier - minTier + 1) * nbTickets;

            Dictionary<int, string> ticketsPerTier = new();

            for (int i = minTier; i <= maxTier; i++)
            {
                int tierNumber = i;

                _form.UpdateOfflineGenerationCurrentTierNumber(i);

                List<string> tickets = new();

                for (int j = 0; j < nbTickets; j++)
                {
                    string ticket = QueryEngine(tierNumber, stake, profile.Name, profile.Settings);

                    ++counter;

                    _form.UpdateOfflineGenerationCurrentTierGenerated(j + 1);
                    _form.UpdateOfflineGenerationTotalTierGenerated(counter);

                    if (checkTickets)
                    {
                        bool hasError = CheckTicketDuringLiveGeneration(ticket, profile.Settings);

                        if (!hasError)
                        {
                            lock (_lockVolumeCheck)
                            {
                                _volumeCheckReport[tierNumber]++;
                            }
                        }
                    }

                    tickets.Add(ticket);

                    int progressBarValue = (int)((float)counter / (float)nbMaxTickets * 100);

                    _form.UpdateOfflineGenerationProgressBar(progressBarValue);

                }

                string finalJSON = "{\"tickets\":[" + Environment.NewLine;


                for (int t = 0; t < tickets.Count; ++t)
                {
                    finalJSON += tickets[t];

                    if (t < tickets.Count - 1)
                    {
                        finalJSON += ",";
                    }

                    finalJSON += Environment.NewLine;
                }

                finalJSON += "]}";

                ticketsPerTier.Add(i, finalJSON);
            }

            AddOfflineTicketsToProject(ticketsPerTier, profile);
        }

        /// <summary>
        /// Add offline tickets to project
        /// </summary>
        /// <param name="allTickets"></param>
        private static void AddOfflineTicketsToProject(Dictionary<int, string> allTickets, ProfileInfo profile)
        {
            string itemGroupOpenTag = @"  <ItemGroup>";
            string itemGroupCloseTag = @"  </ItemGroup>";

            string currentLocation = Directory.GetCurrentDirectory();
            //string projectLocation = Path.GetFullPath(Path.Combine(currentLocation, @"..\..\..\..", @"GameEngine"));
            string csprojFileLocation = Path.Combine(profile.ProjectPath, @"" + profile.Name + ".csproj");
            string resourceLocation = Path.Combine(profile.ProjectPath, @"Resources", @"Tickets", @"Tiers");

            if (!Directory.Exists(resourceLocation))
                Directory.CreateDirectory(resourceLocation);

            // Read csproj
            List<string> csprojLines = File.ReadAllLines(csprojFileLocation).ToList();

            List<string> tiersToAdd = new();


            foreach (KeyValuePair<int, string> tickets in allTickets)
            {
                string itemGroupTier = $"    <EmbeddedResource Include=\"Resources\\Tickets\\Tiers\\Tier{tickets.Key}\\tickets.json\" />";

                if (!csprojLines.Contains(itemGroupTier))
                {
                    tiersToAdd.Add(itemGroupTier);
                }

                string currentPath = Path.Combine(resourceLocation, $"Tier{tickets.Key}");

                if (!Directory.Exists(currentPath))
                    Directory.CreateDirectory(currentPath);

                Helpers.LocalFile.SaveTicket(tickets.Value, currentPath, "tickets.json", false, true);
                _form.AddLog($"Tickets for tier {tickets.Key} exported to Resources folder: {resourceLocation}\\Tickets\\Tier{tickets.Key}\\tickets.json");
            }

            int endOfProjectIndex = -1;

            for (int i = 0; i < csprojLines.Count; i++)
            {
                string csprojLine = csprojLines[i];

                if (csprojLine.Contains($"    <EmbeddedResource Include=\"Resources\\Tickets\\Tiers\\"))
                {
                    int tierNumStartIndex = csprojLine.LastIndexOf("\\Tier") + 5;
                    int tierNumEndIndex = csprojLine.LastIndexOf("\\tickets.json");
                    int length = tierNumEndIndex - tierNumStartIndex;

                    int tierNumber = int.Parse(csprojLine.Substring(tierNumStartIndex, length));

                    if (!allTickets.ContainsKey(tierNumber))
                    {
                        csprojLines.RemoveAt(i + 2);
                        csprojLines.RemoveAt(i + 1);
                        csprojLines.RemoveAt(i);
                        csprojLines.RemoveAt(i - 1);

                        --i;

                        string folderToDeletePath = Path.Combine(resourceLocation, $"Tier{tierNumber}");

                        try
                        {
                            Directory.Delete(folderToDeletePath, true);
                            _form.AddLog($"Tickets for tier {tierNumber} deleted from Resources: {folderToDeletePath}");
                        }
                        catch (Exception)
                        {
                            _form.AddLog($"Couldn't delete tickets for tier {tierNumber}: {folderToDeletePath}");
                        }
                    }
                }
                else if (csprojLine == "</Project>")
                {
                    endOfProjectIndex = i;
                }
            }

            foreach (string tierToAdd in tiersToAdd)
            {
                csprojLines.Insert(endOfProjectIndex++, itemGroupOpenTag);
                csprojLines.Insert(endOfProjectIndex++, tierToAdd);
                csprojLines.Insert(endOfProjectIndex++, itemGroupCloseTag);
                csprojLines.Insert(endOfProjectIndex++, "");
            }

            // Write the data
            File.WriteAllLines(csprojFileLocation, csprojLines);

            _form.AddLog($"Csproj file updated. {csprojFileLocation}");
        }
    }
}
