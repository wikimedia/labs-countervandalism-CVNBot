using System;
using System.Collections;
using System.Text;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;
using System.Web;
using System.Linq;

namespace CVNBot
{
    static class CVNBotUtils
    {
        static Regex rStripper = new Regex(@"(,|and)");
        static Regex rSpaces = new Regex(@"\s{2,}");
        static Regex rfindValues = new Regex(@"(\d+) (year|month|fortnight|week|day|hour|minute|min|second|sec)s?");
        // TODO: Something is still wrong here, some exprs show up as 3 instead of 3 day(s)

        /// <summary>
        /// Like PHP's str_split() function, splits a string into an array of chunks
        /// </summary>
        /// <param name="input">String to split</param>
        /// <param name="chunkLen">Maximum length of each chunk</param>
        /// <returns></returns>
        public static ArrayList StringSplit(string input, int chunkLen)
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
        /// Like PHP's strtotime() function, attempts to parse a GNU date/time into number of seconds
        /// </summary>
        /// <param name="input">String representation of date/time length</param>
        /// <returns></returns>
        public static int ParseDateTimeLength(string input, int defaultLen)
        {
            string parseStr = input.ToLower();
            parseStr = rStripper.Replace(parseStr, "");
            parseStr = rSpaces.Replace(parseStr, " ");

            // Handle specials here
            switch (parseStr)
            {
                case "indefinite":
                case "infinite":
                    return 0;
                case "tomorrow":
                    return 24 * 3600;
            }

            // Now for some real parsing
            Double sumSeconds = 0;
            MatchCollection mc = rfindValues.Matches(parseStr);

            foreach (Match m in mc)
            {
                string unit = m.Groups[2].Captures[0].Value;
                string value = m.Groups[1].Captures[0].Value;
                switch (unit)
                {
                    case "year":
                        sumSeconds += Convert.ToInt32(value) * 8760 * 3600; // 365 days
                        break;
                    case "month":
                        sumSeconds += Convert.ToInt32(value) * 732 * 3600; // 30.5 days
                        break;
                    case "fortnight":
                        sumSeconds += Convert.ToInt32(value) * 336 * 3600; // 14 days
                        break;
                    case "week":
                        sumSeconds += Convert.ToInt32(value) * 168 * 3600; // 7 days
                        break;
                    case "day":
                        sumSeconds += Convert.ToInt32(value) * 24 * 3600; // 24 hours
                        break;
                    case "hour":
                        sumSeconds += Convert.ToInt32(value) * 3600; // 1 hour
                        break;
                    case "minute":
                    case "min":
                        sumSeconds += Convert.ToInt32(value) * 60; // 60 seconds
                        break;
                    case "second":
                    case "sec":
                        sumSeconds += Convert.ToInt32(value); // One second
                        break;
                }
            }

            // Round the double
            Int32 seconds = Convert.ToInt32(sumSeconds);

            if (seconds == 0)
                return defaultLen;

            return seconds;
        }

        /// <summary>
        /// Gets the raw source code for a URL
        /// </summary>
        /// <param name="url">Location of the resource</param>
        /// <returns></returns>
        public static string GetRawDocument(string url)
        {
            string output = string.Empty;

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Mozilla/5.0 (en-US) CVNBot/1.0 (like SWMTBot) More info: https://github.com/countervandalism/CVNBot";

                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {

                    Stream receiveStream = res.GetResponseStream();

                    using (StreamReader readStream = new StreamReader(receiveStream, Encoding.UTF8))
                    {
                        output = readStream.ReadToEnd();
                    }

                    res.Close();
                }
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
        public static string ReplaceStrMax(string input, char oldChar, char newChar, int maxChars)
        {
            //Count the number of times oldChar occurs in input
            int occurences = input.Count(c => c == oldChar);

            if (occurences == 0)
                return input;

            else if (occurences <= maxChars)
                return input.Replace(oldChar, newChar);

            //there are far more occurences than the number we want to replace
            StringBuilder result = new StringBuilder();
            int counter = 1;

            //for every character in the string, replace it with newChar untill you hit the limit.
            //once the limit is hit, continue copying the rest of the chars as is.

            foreach (char item in input)
            {
                if (item.Equals(oldChar) && counter <= maxChars)
                {
                    result.Append(newChar);
                    counter++;
                }
                else
                {
                    result.Append(item);
                }

            }

            return result.ToString();
        }

        /// <summary>
        /// Encodes a string for use with wiki URLs
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string WikiEncode(string input)
        {
            return HttpUtility.UrlEncode(input.Replace(' ', '_')).Replace("(","%28").Replace(")","%29").Replace("!","%21");
        }

        /// <summary>
        /// Use https as protocol in Output RCfeed.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string RootUrl(string input)
        {
            if (Program.config.forceHttps)
                return Regex.Replace(input, "^http:", "https:");

            return input;
        }
    }
}
