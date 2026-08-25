using System;
using System.Collections.Generic;
using System.Linq;

namespace GameEngine
{
    internal static class Helpers
    {
        /// <summary>
        /// Shuffle list
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <param name="random"></param>
        public static void ShuffleList<T>(IList<T> list, Random random)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = random.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        /// <summary>
        /// Shuffle the keys order in a dictionary
        /// </summary>
        /// <typeparam name="Tkey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="dictionary"></param>
        /// <param name="random"></param>
        public static void ShuffleDictionary<Tkey, TValue>(Dictionary<Tkey, TValue> dictionary, Random random)
            where Tkey : notnull
        {
            dictionary = dictionary.OrderBy(x => random.Next()).ToDictionary(item => item.Key, item => item.Value);
        }

        /// <summary>
        /// Returns if 2 lists contain the same elements (no matter in which order)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list1"></param>
        /// <param name="list2"></param>
        /// <returns></returns>
        public static bool AreListsEqual<T>(List<T> list1, List<T> list2)
        {
            if (list1.Count != list2.Count)
                return false;

            List<T> l2 = new();
            l2.AddRange(list2);

            for (int i = 0; i < list1.Count; ++i)
            {
                if (l2.Contains(list1[i]))
                {
                    l2.Remove(list1[i]);
                }
            }

            return l2.Count == 0;
        }

        /// <summary>
        /// Split a value into sub values
        /// </summary>
        /// <param name="value">Value to split</param>
        /// <param name="lowerBound">The minimum sub value allowed to use.</param>
        /// <param name="upperbound">The maximum sub value allowed to use.</param>
        /// <param name="minCount">Minimum amount of sub values the list should have.</param>
        /// <param name="maxCount">Maximum amount of sub values the list can have</param>
        /// <returns></returns>
        public static List<int> SplitValue(int value, int lowerBound, int upperbound, Random random, int minCount = 1, int? maxCount = null)
        {
            List<int> split = new();

            Action<int> Break = (currentValue) =>
            {
                int max = currentValue > upperbound ? upperbound : currentValue;

                int subValue = random.Next(lowerBound, max + 1);

                split.Add(subValue);
            };

            // Return empty list if the value to split is lower than the minimum value allowed
            if (value < lowerBound)
            {
                return split;
            }

            bool processDone = false;

            while (!processDone)
            {
                split.Clear();

                int currentValue = value;

                Break(currentValue);

                currentValue -= split.Sum();

                while (currentValue > 0 && currentValue >= lowerBound)
                {
                    Break(currentValue);

                    currentValue = value - split.Sum();
                }

                if (currentValue == 0 && split.Count >= minCount && (maxCount == null || (split.Count <= maxCount)))
                {
                    processDone = true;
                }
            }

            return split;
        }

        /// <summary>
        /// Split a value into sub values
        /// </summary>
        /// <param name="value">Value to split</param>
        /// <param name="allowedSubValues">List of allowed sub values to use.</param>
        /// <param name="minCount">Minimum amount of sub values the list should have.</param>
        /// <param name="maxCount">Maximum amount of sub values the list can have.</param>
        /// <returns></returns>
        public static List<int> SplitValue(int value, List<int> allowedSubValues, Random random, int minCount = 1, int? maxCount = null)
        {
            List<int> split = new();

            Action<int> Break = (currentValue) =>
            {
                int subValue = allowedSubValues[random.Next(0, allowedSubValues.Count)];

                split.Add(subValue);
            };

            int minSubValue = allowedSubValues.Min();

            // Return empty list if the value to split is lower than the minimum value allowed
            if (value < minSubValue)
            {
                return split;
            }

            bool processDone = false;

            while (!processDone)
            {
                split.Clear();

                int currentValue = value;

                Break(currentValue);

                currentValue -= split.Sum();

                while (currentValue > 0 && currentValue >= minSubValue)
                {
                    Break(currentValue);

                    currentValue = value - split.Sum();
                }

                if (currentValue == 0 && split.Count >= minCount && (maxCount == null || (split.Count <= maxCount)))
                {
                    processDone = true;
                }
            }

            return split;
        }

        /// <summary>
        /// Returns a boolean based on a chance
        /// </summary>
        /// <param name="chance"></param>
        /// <param name="rand"></param>
        /// <returns></returns>
        public static bool Chance(int chance, Random rand)
        {
            return rand.Next(1, 101) < chance; // between 1 (inclusive) and 101 (exclusive), returns 1-100.
        }

        /// <summary>
        /// Inclusively checks that a value is between 2 other values
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="val"></param>
        /// <returns></returns>
        public static bool IsBetween(int min, int max, int val)
        {
            return (val >= min) && (val <= max);
        }

        /// <summary>
        /// Picks a number that is larger than or equal to min and smaller than or equal to max (inclusive)
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="rand"></param>
        /// <returns></returns>
        public static int RandomBetween(int min, int max, Random rand)
        {
            return rand.Next(min, max + 1);
        }

        /// <summary>
        /// Track how many times an item has been used
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="tracker"></param>
        /// <param name="item"></param>
        public static void TrackItem<T>(Dictionary<T, int> tracker, T item)
            where T : notnull
        {
            if (!tracker.ContainsKey(item))
                tracker.Add(item, 0);

            ++tracker[item];
        }

        /// <summary>
        /// Track how many times an item has been used. Allow force specific value
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="tracker"></param>
        /// <param name="item"></param>
        /// <param name="value"></param>
        public static void TrackItem<T>(Dictionary<T, int> tracker, T item, int value)
            where T : notnull
        {
            if (!tracker.ContainsKey(item))
                tracker.Add(item, 0);

            tracker[item] += value;
        }

        /// <summary>
        /// Returns how many times an item has been tracked
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="tracker"></param>
        /// <param name="item"></param>
        /// <returns></returns>
        public static int CheckTracker<T>(Dictionary<T, int> tracker, T item)
            where T : notnull
        {
            if (tracker.ContainsKey(item))
                return tracker[item];

            return 0;
        }

        /// <summary>
        /// Return a random value from an enumeration
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="rand"></param>
        /// <returns></returns>
        public static T RandomValueFromEnumeration<T>(Random rand)
        {
            Array values = Enum.GetValues(typeof(T));
            return (T)values.GetValue(rand.Next(values.Length))!;
        }

        /// <summary>
        /// Return a list of all the elements in a enum
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static List<T> GetAllValuesFromEnum<T>()
        {
            return ((T[])Enum.GetValues(typeof(T))).ToList();
        }

        /// <summary>
        /// Returns if a list has duplicates
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <returns></returns>
        public static bool HasDuplicate<T>(List<T> list)
        {
            return GetDuplicates(list).Count > 0;
        }

        /// <summary>
        /// Returns a list of objects that are duplicates
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <returns></returns>
        public static List<T> GetDuplicates<T>(List<T> list)
        {
            return list.GroupBy(x => x)
              .Where(g => g.Count() > 1)
              .Select(y => y.Key).ToList();
        }

        /// <summary>
        /// Get how many times each duplicate appears
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <returns></returns>
        public static Dictionary<T, int> GetDuplicatesOccurrences<T>(List<T> list)
            where T : notnull
        {
            return list.GroupBy(x => x)
              .Where(g => g.Count() > 1)
              .ToDictionary(x => x.Key, y => y.Count());
        }
    }
}
