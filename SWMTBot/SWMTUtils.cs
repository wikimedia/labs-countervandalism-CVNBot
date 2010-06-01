using System;
using System.Collections;
using System.Text;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;

namespace SWMTBot
{
    static class SWMTUtils
    {
        static Regex rStripper = new Regex(@"(,|and)");
        static Regex rSpaces = new Regex(@"\s{2,}");
        static Regex rfindValues = new Regex(@"(\d+) (year|month|fortnight|week|day|hour|minute|min|second|sec)s?");

        /// <summary>
        /// Like PHP's str_split() function, splits a string into an array of chunks
        /// </summary>
        /// <param name="input">String to split</param>
        /// <param name="chunkLen">Maximum length of each chunk</param>
        /// <returns></returns>
        public static ArrayList stringSplit(string input, int chunkLen)
        {
            ArrayList output = new ArrayList();

            // If input is already shorter than chunkLen, then...
            if (input.Length <= chunkLen)
            {
                output.Add(input);
            }
            else
            {
                for (int i = 0; i < input.Length; i = i + chunkLen)
                {
                    int getLen;
                    if (i + chunkLen > input.Length)
                        getLen = input.Length - i;
                    else
                        getLen = chunkLen;
                    output.Add(input.Substring(i, getLen));
                }
            }

            return output;
        }

        /// <summary>
        /// Like PHP's strtotime() function, attempts to parse a GNU date/time into number of hours
        /// </summary>
        /// <param name="input">String representation of date/time length</param>
        /// <returns></returns>
        public static int ParseDateTimeLength(string input, int defaultLen)
        {
            string parseStr = input.ToLower();
            parseStr = rStripper.Replace(parseStr, "");
            parseStr = rSpaces.Replace(parseStr, " ");

            //Handle specials here
            switch (parseStr)
            {
                case "indefinite":
                case "infinite":
                    return 0;
                case "tomorrow":
                    return 24;
            }

            //Now for some real parsing
            Double sumHours = 0;
            MatchCollection mc = rfindValues.Matches(parseStr);

            foreach (Match m in mc)
            {
                switch (m.Groups[2].Captures[0].Value)
                {
                    case "year":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 8760; //365 days
                        break;
                    case "month":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 732; //30.5 days
                        break;
                    case "fortnight":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 336; //14 days
                        break;
                    case "week":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 168; //7 days
                        break;
                    case "day":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 24; //24 hours
                        break;
                    case "hour":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value); //1 hour
                        break;
                    case "minute":
                    case "min":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 0.0167; //One sixtieth of an hour
                        break;
                    case "second":
                    case "sec":
                        sumHours += Convert.ToInt32(m.Groups[1].Captures[0].Value) * 2.7778e-4; //One three-thousand-six-hundreth of an hour
                        break;
                }
            }

            if (sumHours == 0)
                return defaultLen;

            return Convert.ToInt32(sumHours);
        }

        /// <summary>
        /// Gets the raw source code for a URL
        /// </summary>
        /// <param name="url">Location of the resource</param>
        /// <returns></returns>
        public static string getRawDocument(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Mozilla/5.0 (compatible; SWMTBot/1.0)";

                HttpWebResponse res = (HttpWebResponse)req.GetResponse();

                Stream receiveStream = res.GetResponseStream();
                StreamReader readStream = new StreamReader(receiveStream, Encoding.UTF8);
                string output = readStream.ReadToEnd();
                res.Close();
                readStream.Close();
                return output;
            }
            catch (WebException e)
            {
                throw new Exception("Unable to retrieve " + url + " from server. Error was: " + e.Message);
            }
        }

        /// <summary>
        /// Replaces up to the maximum number of old characters with new characters in a string
        /// </summary>
        /// <param name="input">The string to work on</param>
        /// <param name="oldChar">The character to replace</param>
        /// <param name="newChar">The character to insert</param>
        /// <param name="maxChars">The maximum number of instances to replace</param>
        /// <returns></returns>
        public static string replaceStrMax(string input, char oldChar, char newChar, int maxChars)
        {
            for (int i = 1; i <= maxChars; i++)
            {
                int place = input.IndexOf(oldChar); //Find first oldChar
                if (place == -1) //If not found then finish
                    break;
                input = input.Substring(0, place) + newChar + input.Substring(place + 1); //Replace first oldChar with newChar
            }
            return input;
        }
    }
}
