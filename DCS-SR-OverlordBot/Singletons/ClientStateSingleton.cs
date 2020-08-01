﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.GameState;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings.RadioChannels;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI.RadioOverlayWindow.PresetChannels;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.DCSState;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using Easy.MessageHub;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons
{
    public sealed class ClientStateSingleton : INotifyPropertyChanged
    {
        private readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static volatile ClientStateSingleton _instance;
        private static object _lock = new Object();

        public delegate bool RadioUpdatedCallback();

        private List<RadioUpdatedCallback> _radioCallbacks = new List<RadioUpdatedCallback>();

        private readonly IMessageHub hub = MessageHub.Instance;

        public event PropertyChangedEventHandler PropertyChanged;

        public DCSPlayerRadioInfo DcsPlayerRadioInfo { get; }
        public DCSPlayerSideInfo PlayerCoaltionLocationMetadata { get; set; }

        // Timestamp the last UDP Game GUI broadcast was received from DCS, used for determining active game connection
        public long DcsGameGuiLastReceived { get; set; }

        // Timestamp the last UDP Export broadcast was received from DCS, used for determining active game connection
        public long DcsExportLastReceived { get; set; }

        // Timestamp for the last time 
        public long LotATCLastReceived { get; set; }

        //store radio channels here?
        public PresetChannelsViewModel[] FixedChannels { get; }

        public long LastSent { get; set; }

        public long LastPostionCoalitionSent { get; set; }

        private static readonly DispatcherTimer _timer = new DispatcherTimer();

        private bool isConnected;
        public bool IsConnected
        {
            get
            {
                return isConnected;
            }
            set
            {
                isConnected = value;
                NotifyPropertyChanged("IsConnected");
            }
        }

        private bool isVoipConnected;
        public bool IsVoipConnected
        {
            get
            {
                return isVoipConnected;
            }
            set
            {
                isVoipConnected = value;
                if(value == true)
                {
                    Logger.Debug($"Publishing Connection State {SRSClientSyncHandler.ConnectionState.Connected}");
                    //hub.Publish(SRSClientSyncHandler.ConnectionState.Connected);
                    SRSClientSyncHandler.Instance.ProcessConnectionState(SRSClientSyncHandler.ConnectionState.Connected);
                }
                if (value == false)
                {
                    Logger.Debug($"Publishing Connection State {SRSClientSyncHandler.ConnectionState.Disconnected}");
                    //hub.Publish(SRSClientSyncHandler.ConnectionState.Disconnected);
                    SRSClientSyncHandler.Instance.ProcessConnectionState(SRSClientSyncHandler.ConnectionState.Disconnected);
                }
                NotifyPropertyChanged("IsVoipConnected");
            }
        }

        private bool isConnectionErrored;
        public string ShortGUID { get; }

        public bool IsConnectionErrored
        {
            get
            {
                return isConnectionErrored;
            }
            set
            {
                isConnectionErrored = value;
                NotifyPropertyChanged("isConnectionErrored");
            }
        }

        // Indicates the user's desire to be in External Awacs Mode or not
        public bool ExternalAWACSModeSelected { get; set; }

        // Indicates whether we are *actually* connected in External Awacs Mode
        // Used by the Name and Password related UI elements to determine if they are editable or not
        public bool ExternalAWACSModeConnected { get; set; }
        /*{
            get
            {
                bool EamEnabled = SyncedServerSettings.Instance.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE);
                return IsConnected && EamEnabled && ExternalAWACSModeSelected && !IsGameExportConnected;
            }
        }*/

        public string LastSeenName { get; set; }

        public string ExternalAWACSModePassword { get; set; }

        public bool IsLotATCConnected { get { return LotATCLastReceived >= DateTime.Now.Ticks - 50000000; } }

        public bool IsGameGuiConnected { get { return DcsGameGuiLastReceived >= DateTime.Now.Ticks - 100000000; } }

        public bool IsCinCConnected { get { return AirfieldUpdater.Instance.IsConnected; } }

        public bool IsGameExportConnected { get { return DcsExportLastReceived >= DateTime.Now.Ticks - 100000000; } }
        // Indicates an active game connection has been detected (1 tick = 100ns, 100000000 ticks = 10s stale timer), not updated by EAM
        public bool IsGameConnected { get { return IsGameGuiConnected && IsGameExportConnected; } }

        private ClientStateSingleton()
        {
            ShortGUID = ShortGuid.NewGuid();
            DcsPlayerRadioInfo = new DCSPlayerRadioInfo();
            PlayerCoaltionLocationMetadata = new DCSPlayerSideInfo();

            // The following members are not updated due to events. Therefore we need to setup a polling action so that they are
            // periodically checked.
            DcsGameGuiLastReceived = 0;
            DcsExportLastReceived = 0;
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (s, e) => {
                NotifyPropertyChanged("IsCinCConnected");
                NotifyPropertyChanged("IsGameConnected");
                NotifyPropertyChanged("IsLotATCConnected");
                NotifyPropertyChanged("ExternalAWACSModeConnected");
            };
            _timer.Start();

            FixedChannels = new PresetChannelsViewModel[10];

            for (int i = 0; i < FixedChannels.Length; i++)
            {
                FixedChannels[i] = new PresetChannelsViewModel(new FilePresetChannelsStore(), i + 1);
            }

            LastSent = 0;

            IsConnected = false;
            ExternalAWACSModeSelected = false;

            LastSeenName = "OverlordBot";
        }

        public static ClientStateSingleton Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ClientStateSingleton();
                    }
                }

                return _instance;
            }
        }

        private void NotifyPropertyChanged(string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public bool ShouldUseLotATCPosition()
        {
            if (!IsLotATCConnected)
            {
                return false;
            }

            if (IsGameExportConnected)
            {
                if (DcsPlayerRadioInfo.inAircraft)
                {
                    return false;
                }
            }

            return true;
        }

        public void ClearPositionsIfExpired()
        {
            //not game or Lotatc - clear it!
            if (!IsLotATCConnected && !IsGameExportConnected)
            {
                PlayerCoaltionLocationMetadata.LngLngPosition = new DCSLatLngPosition();
            }
        }

        public void UpdatePlayerPosition(DCSLatLngPosition latLngPosition)
        {
            PlayerCoaltionLocationMetadata.LngLngPosition = latLngPosition;
            DcsPlayerRadioInfo.latLng = latLngPosition;

        }

    }
}