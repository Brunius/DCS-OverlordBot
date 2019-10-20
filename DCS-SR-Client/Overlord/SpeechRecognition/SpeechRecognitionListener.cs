﻿using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Intents;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.LuisModels;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechOutput;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using FragLabs.Audio.Codecs;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using NLog;
using System.IO;
using NewRelic.Api.Agent;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechRecognition
{
    public class SpeechRecognitionListener
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        // Used when an exception is thrown so that the caller isn't left wondering.
        private static readonly byte[] _failureMessage = File.ReadAllBytes("Overlord/equipment-failure.wav");

        // Authorization token expires every 10 minutes. Renew it every 9 minutes.
        private static TimeSpan RefreshTokenDuration = TimeSpan.FromMinutes(9);

        private readonly BufferedWaveProviderStreamReader _streamReader;
        private readonly AudioConfig _audioConfig;
        private readonly OpusEncoder _encoder;

        private readonly string _voice;

        public TCPVoiceHandler _voiceHandler;

        public int lastReceivedRadio = -1;

        // Allows OverlordBot to listen for a specific word to start listening. Currently not used although the setup has all been done.
        // This is due to wierd state transition errors thatI cannot be bothered to debug.
        KeywordRecognitionModel _wakeWord;

        public SpeechRecognitionListener(BufferedWaveProvider bufferedWaveProvider, string callsign = null, string voice = "en-US-JessaRUS")
        {

            Logger.Debug("VOICE: " + voice);

            _voice = voice;

           _encoder = OpusEncoder.Create(AudioManager.INPUT_SAMPLE_RATE, 1, FragLabs.Audio.Codecs.Opus.Application.Voip);
           _encoder.ForwardErrorCorrection = false;
           _encoder.FrameByteCount(AudioManager.SEGMENT_FRAMES);

            _streamReader = new BufferedWaveProviderStreamReader(bufferedWaveProvider);
            _audioConfig = AudioConfig.FromStreamInput(_streamReader, AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

            _wakeWord = KeywordRecognitionModel.FromFile($"Overlord/WakeWords/{callsign}.table");

        }

        // Gets an authorization token by sending a POST request to the token service.
        public static async Task<string> GetToken()
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", Constants.SPEECH_SUBSCRIPTION_KEY);
                UriBuilder uriBuilder = new UriBuilder("https://" + Constants.SPEECH_REGION + ".api.cognitive.microsoft.com/sts/v1.0/issueToken");

                using (var result = await client.PostAsync(uriBuilder.Uri.AbsoluteUri, null))
                {
                    if (result.IsSuccessStatusCode)
                    {
                        return await result.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        throw new HttpRequestException($"Cannot get token from {uriBuilder.ToString()}. Error: {result.StatusCode}");
                    }
                }
            }
        }

        // Renews authorization token periodically until cancellationToken is cancelled.
        public static Task StartTokenRenewTask(CancellationToken cancellationToken, SpeechRecognizer recognizer)
        {
            return Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(RefreshTokenDuration, cancellationToken);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        recognizer.AuthorizationToken = await GetToken();
                    }
                }
            });
        }

        public async Task StartListeningAsync()
        {
            Logger.Debug($"Started Continuous Recognition");

            // Initialize the recognizer
            var authorizationToken = Task.Run(() => GetToken()).Result;
            SpeechConfig speechConfig = SpeechConfig.FromAuthorizationToken(authorizationToken, Constants.SPEECH_REGION);
            speechConfig.EndpointId = Constants.SPEECH_CUSTOM_ENDPOINT_ID;
            SpeechRecognizer recognizer = new SpeechRecognizer(speechConfig, _audioConfig);

            // Setup the cancellation code
            var stopRecognition = new TaskCompletionSource<int>();
            CancellationTokenSource source = new CancellationTokenSource();

            // Start the token renewal so we can do long-running recognition.
            var tokenRenewTask = StartTokenRenewTask(source.Token, recognizer);

            recognizer.Recognized += async (s, e) =>
            {
                await ProcessAwacsCall(e);
            };

            recognizer.Canceled += async (s, e) =>
            {
                Logger.Debug($"CANCELLED: Reason={e.Reason}");

                if (e.Reason == CancellationReason.Error)
                {
                    Logger.Debug($"CANCELLED: ErrorCode={e.ErrorCode}");
                    Logger.Debug($"CANCELLED: ErrorDetails={e.ErrorDetails}");

                    if (e.ErrorCode != CancellationErrorCode.BadRequest && e.ErrorCode != CancellationErrorCode.ConnectionFailure)
                    {
                        await SendResponse(_failureMessage, _failureMessage.Length);
                    }
                }
                stopRecognition.TrySetResult(1);
            };

            recognizer.SpeechStartDetected += (s, e) =>
            {
                Logger.Debug("\nSpeech started event.");
            };

            recognizer.SpeechEndDetected += (s, e) =>
            {
                Logger.Debug("\nSpeech ended event.");
            };

            recognizer.SessionStarted += (s, e) =>
            {
                Logger.Debug("\nSession started event.");
            };

            recognizer.SessionStopped += (s, e) =>
            {
                Logger.Debug("\nSession stopped event.");
                stopRecognition.TrySetResult(0);
            };

            // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
            await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

            // Waits for completion.
            // Use Task.WaitAny to keep the task rooted.
            Task.WaitAny(new[] { stopRecognition.Task });

            // Stops recognition.
            await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            source.Cancel();
            Logger.Debug($"Stopped Continuous Recognition");
            await StartListeningAsync();
        }

        [Transaction(Web = true)]
        private async Task ProcessAwacsCall(SpeechRecognitionEventArgs e) {
            string response = null;

            try
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    Logger.Debug($"RECOGNIZED: {e.Result.Text}");
                    string luisJson = Task.Run(() => LuisService.ParseIntent(e.Result.Text)).Result;

                    Logger.Debug($"INTENT: {luisJson}");
                    LuisResponse luisResponse = JsonConvert.DeserializeObject<LuisResponse>(luisJson);

                    string awacs;
                    Sender sender = Task.Run(() => SenderExtractor.Extract(luisResponse)).Result;

                    if (luisResponse.Query != null && luisResponse.TopScoringIntent["intent"] == "None" ||
                        luisResponse.Entities.Find(x => x.Type == "awacs_callsign") == null)
                    {
                        Logger.Debug($"RESPONSE NO-OP");
                        // NO-OP
                    }
                    else if (sender == null)
                    {
                        Logger.Debug($"SENDER IS NULL");
                        response = "Last transmitter, I could not recognise your call-sign.";
                    }
                    else
                    {
                        awacs = luisResponse.Entities.Find(x => x.Type == "awacs_callsign").Resolution.Values[0];

                        Logger.Debug($"SENDER: " + sender);

                        if (Task.Run(() => SenderVerifier.Verify(sender)).Result == false)
                        {
                            Logger.Debug($"SenderVerified: false");
                            response = $"{sender.ToString()}, {awacs}, I cannot find you on scope. ";
                        }
                        else
                        {
                            if (luisResponse.Query != null && luisResponse.TopScoringIntent["intent"] == "RequestBogeyDope")
                            {
                                response = $"{sender.ToString()}, {awacs}, ";
                                response += Task.Run(() => RequestBogeyDope.Process(luisResponse, sender)).Result;
                            }
                            else if (luisResponse.Query != null && (luisResponse.TopScoringIntent["intent"] == "RequestBearingToAirbase" || luisResponse.TopScoringIntent["intent"] == "RequestHeadingToAirbase"))
                            {
                                response = $"{sender.ToString()}, {awacs}, ";
                                response += Task.Run(() => RequestBearingToAirbase.Process(luisResponse, sender)).Result;
                            }
                        }
                    }
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    Logger.Debug($"NOMATCH: Speech could not be recognized.");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex);
                await SendResponse(_failureMessage, _failureMessage.Length);
                response = null;
            }
            if (response != null)
            {
                await Respond($"<speak version=\"1.0\" xmlns=\"https://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\"><voice name =\"{_voice}\">{response}</voice></speak>");
            }
        }

        private async Task Respond(string response)
        {
            Logger.Debug($"RESPONSE: {response}");
            byte[] audioResponse = Task.Run(() => Speaker.CreateResponse(response)).Result;
            if (audioResponse != null)
            {
                await SendResponse(audioResponse, audioResponse.Length);
            }
            else
            {
                await SendResponse(_failureMessage, _failureMessage.Length);
            }
        }

        // Expects a byte buffer containing 16 bit 16KHz 1 channel PCM WAV
        private async Task SendResponse(byte[] buffer, int length)
        {

            Queue<byte> audioQueue = new Queue<byte>(length);

            for (var i = 0; i < length; i++)
            {
                audioQueue.Enqueue(buffer[i]);
            }

            //read out the queue
            while (audioQueue.Count >= AudioManager.SEGMENT_FRAMES)
            {

                byte[] packetBuffer = new byte[AudioManager.SEGMENT_FRAMES];

                for (var i = 0; i < AudioManager.SEGMENT_FRAMES; i++)
                {
                    if (audioQueue.Count > 0)
                    {
                        packetBuffer[i] = audioQueue.Dequeue();
                    } else
                    {
                        packetBuffer[i] = 0;
                    }
                }

                //encode as opus bytes
                int len;
                var buff = _encoder.Encode(packetBuffer, AudioManager.SEGMENT_FRAMES, out len);

                if ((_voiceHandler != null) && (buff != null) && (len > 0))
                {
                    //create copy with small buffer
                    var encoded = new byte[len];

                    Buffer.BlockCopy(buff, 0, encoded, 0, len);

                    await Task.Run(() =>_voiceHandler.Send(encoded, len, lastReceivedRadio));
                    // Sleep between sending 40ms worth of data so that we do not overflow the 3 second audio buffers of
                    // normal SRS clients. The lower the sleep the less chance of audio corruption due to network issues
                    // but the greater the chance of over-flowing buffers. 10-20ms sleep per 40ms of audio being sent seems
                    // to be about the right balance.
                    Thread.Sleep(10);
                }
                else
                {
                    Logger.Debug($"Invalid Bytes for Encoding - {length} should be {AudioManager.SEGMENT_FRAMES}");
                }
            }
            // Send one null to reset the sending state
            await Task.Run(() => _voiceHandler.Send(null, 0, lastReceivedRadio));
        }

    }
}
