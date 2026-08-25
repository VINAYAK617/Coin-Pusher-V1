using ClosedXML.Excel;
using Neo.ComboGenerator.Core.Interfaces;
using Neo.ComboGenerator.Core.Models;
using Neo.ComboGenerator.Offline;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UIGameEngine.Models;

namespace UIGameEngine.Helpers
{
    internal static class CSV
    {
        private static char _csvSeparator = ';';
        private static string _innerListSeparator = " + ";

        private static List<XLTableTheme> _tableThemes = new()
        {
            XLTableTheme.TableStyleMedium7,
            XLTableTheme.TableStyleMedium5,
            XLTableTheme.TableStyleMedium4,
            XLTableTheme.TableStyleMedium3,
            XLTableTheme.TableStyleMedium2,
            XLTableTheme.TableStyleMedium3
        };

        /// <summary>
        /// Add combos
        /// </summary>
        /// <param name="excelFilePath">Path to the excel file</param>
        /// <param name="tiers">Tiers containing the combos to add</param>
        /// <param name="profile">Current profile selected</param>
        /// <param name="ActionOnExisitingTierTabs">Action to execute if tabs with existing tier are found in the excel</param>
        public static int ExportCombosToExcel(string excelFilePath, List<OfflineTier> tiers, ProfileInfo profile, bool multiplyByStake, string defaultStakeCellCoordinate, Func<string, bool, bool> ActionOnExisitingTierTabs, Action<int, int> currentTierProcesingCallBack)
        {
            if (!File.Exists(excelFilePath))
            {
                return -1;
            }

            if (LocalFile.IsFileInUse(excelFilePath))
            {
                ActionOnExisitingTierTabs($"The file \"{excelFilePath}\" is currently in use by another process. Please close it before to continue.", false);
                return -1;
            }

            // Load the Excel file
            using (var workbook = new XLWorkbook(excelFilePath))
            {
                bool @continue = CheckExistingTierTabs(workbook, tiers, ActionOnExisitingTierTabs);

                // Get the name of the first worksheet
                var firstWorksheetName = workbook.Worksheet(1).Name;


                if (@continue)
                {
                    Dictionary<Type, string> gameTypes = GetGameTypes();

                    if (gameTypes.Count == 0)
                    {
                        return -1;
                    }

                    foreach (OfflineTier tier in tiers)
                    {
                        currentTierProcesingCallBack(tiers.Count, tier.TierNumber);

                        // Create a new worksheet
                        IXLWorksheet worksheet = workbook.Worksheets.Add(tier.TierNumber.ToString());

                        int lastColumnIndex = -1;

                        AddTierToWorksheet(worksheet, tier, gameTypes, multiplyByStake, defaultStakeCellCoordinate, out lastColumnIndex);

                        lastColumnIndex += 2;

                        AddPrizeListsToWorksheet(worksheet, tier, profile, gameTypes, lastColumnIndex, defaultStakeCellCoordinate);

                        worksheet.Columns().AdjustToContents();
                    }

                    // Save the changes
                    workbook.Save();
                }
                else
                    return 0;
            }

            return tiers.Count;
        }

        /// <summary>
        /// Return the game types used by the engine. Key: Type, Value: Name as string (without I as a first letter. IMainGame => MaiGame)
        /// </summary>
        /// <returns></returns>
        private static Dictionary<Type, string> GetGameTypes()
        {
            Assembly gameEngineAssembly = DLLHandler.LoadProfileDLL("GameEngine.dll");
            List<Type> gameTypes = DLLHandler.FindDerivedType<IGameConfig>(gameEngineAssembly);

            Dictionary<Type, string> gameTypesDictionary = new();

            foreach (Type type in gameTypes)
            {
                string gameTypeName = type.Name;

                if (type.Name.StartsWith("I", StringComparison.OrdinalIgnoreCase))
                {
                    gameTypeName = type.Name.Substring(1);
                }

                gameTypesDictionary.Add(type, gameTypeName);
            }

            return gameTypesDictionary;
        }

        /// <summary>
        /// Check if tabs with tiers already exist. The function will allow to remove them. Returns true if no existing tier tabs are still present after checking.
        /// </summary>
        /// <param name="workbook"></param>
        /// <param name="tiers"></param>
        /// <param name="ActionOnExisitingTierTabs"></param>
        /// <returns></returns>
        private static bool CheckExistingTierTabs(XLWorkbook workbook, List<OfflineTier> tiers, Func<string, bool, bool> ActionOnExisitingTierTabs)
        {
            List<IXLWorksheet> existingWorksheets = new List<IXLWorksheet>();

            foreach (OfflineTier tier in tiers)
            {
                IXLWorksheet worksheet;
                workbook.TryGetWorksheet(tier.TierNumber.ToString(), out worksheet);

                if (worksheet != null)
                    existingWorksheets.Add(worksheet);
            }

            if (existingWorksheets.Count > 0)
            {
                bool remove = ActionOnExisitingTierTabs($"{existingWorksheets.Count} tabs matching existing tier numbers have been found. Do you want to replace them?", true);

                if (remove)
                {
                    foreach (IXLWorksheet sheet in existingWorksheets)
                    {
                        sheet.Delete();
                    }

                    // Remove extra worksheets which only have a number as a name
                    foreach (IXLWorksheet extraSheet in workbook.Worksheets)
                    {
                        if (double.TryParse(extraSheet.Name, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
                        {
                            extraSheet.Delete();
                        }
                    }

                    return true;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Add all combos for a tier to a worksheet
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="tier"></param>
        /// <param name="gameTypes"></param>
        /// <param name="multiplyByStake"></param>
        /// <param name="defaultStakeCellCoordinate"></param>
        /// <param name="lastColumnIndex"></param>
        private static void AddTierToWorksheet(IXLWorksheet worksheet, OfflineTier tier, Dictionary<Type, string> gameTypes, bool multiplyByStake, string defaultStakeCellCoordinate, out int lastColumnIndex)
        {
            // Get the name of the first worksheet
            var firstWorksheetName = worksheet.Workbook.Worksheet(1).Name;

            string csvContent = GetCombosAsCSVContent(tier, gameTypes, firstWorksheetName, defaultStakeCellCoordinate);
            string[][] csvTable = ConvertCSVStringTo2DArray(csvContent);

            lastColumnIndex = csvTable[0].Length;

            // Populate the worksheet with CSV data
            for (int row = 0; row < csvTable.Length; row++)
            {
                for (int col = 0; col < csvTable[row].Length; col++)
                {
                    string cellValue = csvTable[row][col];
                    var cell = worksheet.Cell(row + 1, col + 1);

                    if (multiplyByStake && row > 0 && col > 1)
                    {
                        //string[] values = cellValue.Split(_innerListSeparator);

                        //string formula = GetConcatenateFormula(values, firstWorksheetName, defaultStakeCellCoordinate);

                        if (cellValue != "")
                        {
                            cell.FormulaA1 = cellValue;
                        }
                    }
                    else
                    {
                        if (double.TryParse(cellValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
                        {
                            cell.Value = number;
                        }
                        else
                        {
                            cell.Value = cellValue;
                        }
                    }

                }
            }

            // Add an Excel table to the specified range
            FormatTable(worksheet, 1, 1, csvTable.Length, csvTable[0].Length, $"Table{tier.TierNumber}", XLTableTheme.TableStyleMedium6);
        }

        private static string GetConcatenateFormula(string[] values, string ppsWorksheetName, string defaultStakeCellCoordinate)
        {
            string formula = "";


            if (values.Length == 1 && values[0] != "")
            {
                formula = $"{values[0]} * '{ppsWorksheetName}'!{defaultStakeCellCoordinate}";
            }
            else if (values.Length > 1)
            {
                formula = "CONCATENATE(";

                for (int i = 0; i < values.Length; i++)
                {
                    formula += $"{values[i]} * '{ppsWorksheetName}'!{defaultStakeCellCoordinate}";

                    if (i < values.Length - 1)
                        formula += $", \"{_innerListSeparator}\" , ";

                }

                formula += ")";
            }
            else
            {
                formula = string.Empty; // Handle the case when values array is empty
            }

            return formula;
        }

        private static string ConcatenateGamePrizesWithFormula(ComboConfig combo, Type gameType, string ppsWorksheetName, string defaultStakeCellCoordinate)
        {
            List<Prize> prizes = GetPrizesForGameType(combo, gameType);
            decimal gameMultiplier = GetGameMultiplierForGameType(combo, gameType);

            if (prizes is null || prizes.Count == 0)
                return "";
            string gamePrizesStr = "";

            if (prizes.Count == 1 && prizes[0].Multiplier == 1 && gameMultiplier == 1)
            {
                gamePrizesStr = $"{prizes[0].Value} * '{ppsWorksheetName}'!{defaultStakeCellCoordinate}";
            }
            else if (prizes.Count >= 1)
            {
                gamePrizesStr = "CONCATENATE(";

                if (gameMultiplier > 1)
                    gamePrizesStr += "\"[\",";

                for (int i = 0; i < prizes.Count; i++)
                {
                    Prize prize = prizes[i];

                    if (prize.Multiplier > 1)
                        gamePrizesStr += $"\"(\", {prize.Value}  * '{ppsWorksheetName}'!{defaultStakeCellCoordinate}, \" x {prize.Multiplier})\"";
                    else
                        gamePrizesStr += $"{prize.Value}  * '{ppsWorksheetName}'!{defaultStakeCellCoordinate}";

                    if (i < prizes.Count - 1)
                        gamePrizesStr += ",\" + \",";
                }

                if (gameMultiplier > 1)
                    gamePrizesStr += $",\"] x {gameMultiplier}\",";

                gamePrizesStr += ")";
            }

            return gamePrizesStr;
        }

        /// <summary>
        /// Add prize list, prize multipliers and game multipliers to the sheet
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="tier"></param>
        /// <param name="profile"></param>
        /// <param name="gameTypes"></param>
        /// <param name="startingColumnIndex"></param>
        /// <param name="defaultStakeCellCoordinate"></param>
        private static void AddPrizeListsToWorksheet(IXLWorksheet worksheet, OfflineTier tier, ProfileInfo profile, Dictionary<Type, string> gameTypes, int startingColumnIndex, string defaultStakeCellCoordinate)
        {
            int row = 1;
            int rowOffset = 0;
            int column = startingColumnIndex;
            int columnOffset = 0;
            int themeIndex = 0;

            foreach (KeyValuePair<Type, string> gameType in gameTypes)
            {
                IGameConfig igc = profile.Settings.Tiers.Where(t => t.TierNumber == tier.TierNumber).FirstOrDefault()?.GameConfigs.Where(gc => gc.GetType().GetInterfaces().Contains(gameType.Key)).FirstOrDefault();

                if (igc != null)
                {
                    XLTableTheme tableTheme = _tableThemes[themeIndex++];

                    // Prizes
                    List<decimal> prizeList = igc.PRIZE_LIST.Distinct().ToList();
                    AddElementsToWorksheet(worksheet, prizeList, $"{gameType.Value} - Prizes", row, column + columnOffset, true, defaultStakeCellCoordinate);
                    FormatTable(worksheet, row, column + columnOffset, prizeList.Count + 1, column + columnOffset, $"Table{tier.TierNumber}-{gameType.Value}Prizes", tableTheme);

                    // Prize Multipliers
                    rowOffset = prizeList.Count + 3;
                    AddElementsToWorksheet(worksheet, igc.PRIZE_MULTIPLIERS, $"{gameType.Value} - Prize Multipliers", row + rowOffset, column + columnOffset, true, defaultStakeCellCoordinate);
                    FormatTable(worksheet, row + rowOffset, column + columnOffset, igc.PRIZE_MULTIPLIERS.Count + 1 + rowOffset, column + columnOffset, $"Table{tier.TierNumber}-{gameType.Value}PrizeMultipliers", tableTheme);

                    // Game Multiplier
                    rowOffset += igc.PRIZE_MULTIPLIERS.Count + 3;
                    AddElementsToWorksheet(worksheet, igc.GAME_MULTIPLIERS, $"{gameType.Value} - Game Multipliers", row + rowOffset, column + columnOffset, true, defaultStakeCellCoordinate);
                    FormatTable(worksheet, row + rowOffset, column + columnOffset, igc.GAME_MULTIPLIERS.Count + 1 + rowOffset, column + columnOffset, $"Table{tier.TierNumber}-{gameType.Value}GameMultipliers", tableTheme);

                    if (themeIndex >= _tableThemes.Count)
                        themeIndex = 0;

                    ++columnOffset;
                }
            }
        }

        /// <summary>
        /// Add an excel table and set its style
        /// </summary>
        /// <param name="worksheet"></param>
        /// <param name="firstCellRow"></param>
        /// <param name="firstCellColumn"></param>
        /// <param name="lastCellRow"></param>
        /// <param name="lastCellColumn"></param>
        /// <param name="tableName"></param>
        /// <param name="theme"></param>
        private static void FormatTable(IXLWorksheet worksheet, int firstCellRow, int firstCellColumn, int lastCellRow, int lastCellColumn, string tableName, XLTableTheme theme)
        {
            // Define the range that covers all the cells populated with CSV data
            var range = worksheet.Range(firstCellRow, firstCellColumn, lastCellRow, lastCellColumn);
            // Add an Excel table to the specified range and name it "CsvTable"
            var table = range.CreateTable(tableName);
            table.ShowAutoFilter = false;

            // Set the style of the table
            table.Theme = theme;
        }

        private static void AddElementsToWorksheet<T>(IXLWorksheet worksheet, List<T> elements, string header, int startingRow, int startingColumn, bool multiplyByStake, string defaultStakeCellCoordinate)
        {
            // Get the name of the first worksheet
            var firstWorksheetName = worksheet.Workbook.Worksheet(1).Name;

            worksheet.Cell(startingRow, startingColumn).Value = header;

            int row = startingRow + 1;

            foreach (T element in elements)
            {
                var cell = worksheet.Cell(row, startingColumn);
                string e = element.ToString();

                if (multiplyByStake && row > 0)
                {
                    string[] values = new string[] { element.ToString() };

                    string formula = GetConcatenateFormula(values, firstWorksheetName, defaultStakeCellCoordinate);

                    if (formula != "")
                    {
                        cell.FormulaA1 = formula;
                    }
                }
                else
                {
                    if (double.TryParse(e, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
                    {
                        cell.Value = number;
                    }
                    else
                    {
                        cell.Value = e;
                    }
                }


                ++row;
            }
        }

        /// <summary>
        /// Convert a csv string line to a 2d array
        /// </summary>
        /// <param name="csvLine"></param>
        /// <returns></returns>
        private static string[][] ConvertCSVStringTo2DArray(string csvLine)
        {
            // Convert CSV data to 2D array
            var csvLines = csvLine.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var csvTable = new string[csvLines.Length][];
            for (int i = 0; i < csvLines.Length; i++)
            {
                csvTable[i] = csvLines[i].Split(_csvSeparator);
            }

            return csvTable;
        }

        /// <summary>
        /// Get the content of a tier as a single string line
        /// </summary>
        /// <param name="tier"></param>
        /// <returns></returns>
        private static string GetCombosAsCSVContent(OfflineTier tier, Dictionary<Type, string> gameTypes, string ppsWorksheetName, string defaultStakeCellCoordinate)
        {
            List<string> headers = new() {
                "Tier Number",
                "Stake Multiplier",
                "Total Win"
            };

            foreach (KeyValuePair<Type, string> gameType in gameTypes)
            {
                headers.Add(gameType.Value);
            }

            string csvContent = GetFormatedCSVLine(headers.ToArray()) + '\n';

            foreach (ComboConfig combo in tier.Combos)
            {
                List<string> comboItems = new() {
                    combo.TierNumber.ToString(),
                    combo.StakeMultiplier.ToString(),
                    $"{combo.StakeMultiplier} * '{ppsWorksheetName}'!{defaultStakeCellCoordinate}"
                };

                foreach (KeyValuePair<Type, string> gameType in gameTypes)
                {
                    comboItems.Add(ConcatenateGamePrizesWithFormula(combo, gameType.Key, ppsWorksheetName, defaultStakeCellCoordinate));
                }

                string newCombo = GetFormatedCSVLine(comboItems.ToArray());
                csvContent += newCombo + '\n';
            }

            return csvContent;
        }

        private static string ConcatenateGamePrizes(ComboConfig combo, Type gameType)
        {
            List<Prize> prizes = GetPrizesForGameType(combo, gameType);
            decimal gameMultiplier = GetGameMultiplierForGameType(combo, gameType);

            if (prizes is null || prizes.Count == 0)
                return "";

            string gamePrizesStr = "";

            if (gameMultiplier > 1)
                gamePrizesStr += "[";

            for (int i = 0; i < prizes.Count; i++)
            {
                Prize prize = prizes[i];

                if (prize.Multiplier > 1)
                    gamePrizesStr += $"({prize.Value} x {prize.Multiplier})";
                else
                    gamePrizesStr += prize.Value;

                if (i < prizes.Count - 1)
                    gamePrizesStr += " + ";
            }

            if (gameMultiplier > 1)
                gamePrizesStr += $"] x {gameMultiplier}";

            return gamePrizesStr;
        }

        private static List<Prize> GetPrizesForGameType(ComboConfig combo, Type gameType)
        {
            // Use reflection to invoke the GetPrizeList<T>() method
            var method = combo.GetType().GetMethod("GetPrizeList");
            var genericMethod = method.MakeGenericMethod(gameType);

            return (List<Prize>)genericMethod.Invoke(combo, null);
        }

        private static decimal GetGameMultiplierForGameType(ComboConfig combo, Type gameType)
        {
            // Use reflection to invoke the GetGameMultiplier<T>() method
            var method = combo.GetType().GetMethod("GetGameMultiplier");
            var genericMethod = method.MakeGenericMethod(gameType);

            return (decimal)genericMethod.Invoke(combo, null);
        }

        /// <summary>
        /// Concatenate data into a formatted csv line
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <returns></returns>
        private static string GetFormatedCSVLine<T>(T[] data)
        {
            string newLine = "";

            for (int i = 0; i < data.Length; i++)
            {
                newLine += data[i];

                if (i < data.Length - 1)
                    newLine += _csvSeparator;
            }

            return newLine;
        }
    }
}
