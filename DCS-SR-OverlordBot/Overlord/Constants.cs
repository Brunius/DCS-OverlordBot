﻿using System;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord
{
    class Settings
    {
        public static string SPEECH_REGION = Properties.Settings.Default.SpeechRegion;
        public static string SPEECH_SUBSCRIPTION_KEY = Properties.Settings.Default.SpeechSubscriptionKey;
        public static string SPEECH_CUSTOM_ENDPOINT_ID = Properties.Settings.Default.SpeechCustomEndpointId;

        public static string LUIS_APP_ID = Properties.Settings.Default.LuisAppId;
        public static string LUIS_ENDPOINT_KEY = Properties.Settings.Default.LuisEndpointKey;

        public static string TAC_SCRIBE_HOST = Properties.Settings.Default.TacScribeHost;
        public static string TAC_SCRIBE_PORT = Properties.Settings.Default.TacScribePort.ToString();
        public static string TAC_SCRIBE_DATABASE = Properties.Settings.Default.TacScribeDatabase;
        public static string TAC_SCRIBE_USERNAME = Properties.Settings.Default.TacScribeUsername;
        public static string TAC_SCRIBE_PASSWORD = Properties.Settings.Default.TacScribePassword;
        public static bool TAC_SCRIBE_FORCE_SSL = Properties.Settings.Default.TacScribeForceSSL;
    }

    public enum Coalition
    {
        Neutral,
        Redfor,
        Bluefor
    }

    static class CoalitionMethods
    {

        public static Coalition GetOpposingCoalition(this Coalition coalition)
        {
            switch (coalition)
            {
                case Coalition.Redfor:
                    return Coalition.Bluefor;
                case Coalition.Bluefor:
                    return Coalition.Redfor;
                default:
                    throw new ArgumentException($"Cannot determine opposing coalition for {coalition}");
            }
        }
    }
}
