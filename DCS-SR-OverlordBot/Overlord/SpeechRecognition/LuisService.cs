﻿using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechRecognition
{
    class LuisService
    {
        public static async Task<String> ParseIntent(String text)
        {
            var client = new HttpClient();
            var queryString = HttpUtility.ParseQueryString(string.Empty);

            // The request header contains your subscription key
            client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", Settings.LUIS_ENDPOINT_KEY);

            // The "q" parameter contains the utterance to send to LUIS
            queryString["q"] = text;

            // These optional request parameters are set to their default values
            queryString["timezoneOffset"] = "0";
            queryString["verbose"] = "false";
            queryString["spellCheck"] = "false";
            queryString["staging"] = "false";

            var endpointUri = $"https://{Settings.SPEECH_REGION}.api.cognitive.microsoft.com/luis/v2.0/apps/{Settings.LUIS_APP_ID}?{queryString}";
            var response = await client.GetAsync(endpointUri);

            return await response.Content.ReadAsStringAsync();
        }
    }
}
