using System.ComponentModel;
using System.Windows.Data;

using SharpGen.Runtime;
using SharpGen.Runtime.Win32;

using Vortice.MediaFoundation;

using FlyleafLib.MediaFramework.MediaDevice;

namespace FlyleafLib;

public class AudioEngine : CallbackBase, IMMNotificationClient, INotifyPropertyChanged
{
    #region Properties (Public)
    public AudioEndpoint DefaultDevice      { get; private set; } = new() { Id = "0", Name = "Default" };
    public AudioEndpoint CurrentDevice      { get; private set; } = new();

    /// <summary>
    /// Whether no audio devices were found or audio failed to initialize
    /// </summary>
    public bool         Failed              { get; internal set; }

    /// <summary>
    /// List of Audio Capture Devices
    /// </summary>
    public ObservableCollection<AudioDevice>
                        CapDevices          { get; set; } = [];

    /// <summary>
    /// List of Audio Devices
    /// </summary>
    public ObservableCollection<AudioEndpoint>
                        Devices             { get; private set; } = [];

    private readonly object lockDevices = new();
    private readonly object lockCapDevices = new();
    #endregion

    IMMDeviceEnumerator deviceEnum;
    private object      locker = new();

    public event PropertyChangedEventHandler PropertyChanged;

    public AudioEngine() // We consider from UI here
    {
        if (Engine.Config.DisableAudio)
        {
            Failed = true;
            return;
        }

        BindingOperations.EnableCollectionSynchronization(Devices, lockDevices);
        BindingOperations.EnableCollectionSynchronization(CapDevices, lockCapDevices);
        EnumerateDevices();
    }

    /// <summary>
    /// Enumerates Audio Capture Devices which can be retrieved from <see cref="CapDevices"/>
    /// </summary>
    public void RefreshCapDevices()
    {
        lock (lockCapDevices)
        {
            Engine.Audio.CapDevices.Clear();

            var devices = MediaFactory.MFEnumAudioDeviceSources();
            foreach (var device in devices)
            {
                Engine.Audio.CapDevices.Add(new(device.FriendlyName, device.SymbolicLink));
                device.Dispose();
            }

            devices.Dispose();
        }
    }

    private void EnumerateDevices()
    {
        try
        {
            deviceEnum = new();

            IMMDevice defaultDevice = null;
            try { defaultDevice = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
            if (defaultDevice == null)
            {   // TBR: New devices can be connected later on
                Failed = true;
                return;
            }

            lock (lockDevices)
            {
                string dump = "";
                Devices.Clear();
                Devices.Add(DefaultDevice);
                var devices = deviceEnum.EnumAudioEndpoints(DataFlow.Render, DeviceStates.Active);
                foreach (var device in devices)
                {
                    if (CanInfo)
                        dump += $"{device.Id} | {device.FriendlyName} {(defaultDevice.Id == device.Id ? "*" : "")}\r\n";

                    Devices.Add(new() { Id = device.Id, Name = device.FriendlyName });
                    device.Dispose();
                }

                Engine.Log.Info($"Audio Devices\r\n{dump}");
            }

            CurrentDevice.Id    = defaultDevice.Id;
            CurrentDevice.Name  = defaultDevice.FriendlyName;
            defaultDevice.Dispose();
            deviceEnum.RegisterEndpointNotificationCallback(this);

        } catch { Failed = true; }
    }
    private void RefreshDevices()
    {
        UIInvokeIfRequired(() => // UI Required?
        {
            lock (locker)
            {
                List<AudioEndpoint> curs     = [];
                List<AudioEndpoint> removed  = [];

                lock (lockDevices)
                {
                    var devices = deviceEnum.EnumAudioEndpoints(DataFlow.Render, DeviceStates.Active);
                    foreach(var device in devices)
                    {
                        curs.Add(new () { Id = device.Id, Name = device.FriendlyName });
                        device.Dispose();
                    }
                    
                    foreach(var cur in curs)
                    {
                        bool exists = false;
                        foreach (var device in Devices)
                            if (cur.Id == device.Id)
                                { exists = true; break; }

                        if (!exists)
                        {
                            Engine.Log.Info($"Audio device {cur} added");
                            Devices.Add(cur);
                        }
                    }

                    foreach (var device in Devices)
                    {
                        if (device.Id == "0") // Default
                            continue;

                        bool exists = false;
                        foreach(var cur in curs)
                            if (cur.Id == device.Id)
                                { exists = true; break; }

                        if (!exists)
                        {
                            Engine.Log.Info($"Audio device {device} removed");
                            removed.Add(device);
                        }
                    }

                    foreach(var device in removed)
                        Devices.Remove(device);
                }

                var defaultDevice =  deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (defaultDevice != null)
                {
                    if (CurrentDevice.Id != defaultDevice.Id)
                    {
                        CurrentDevice.Id    = defaultDevice.Id;
                        CurrentDevice.Name  = defaultDevice.FriendlyName;
                        PropertyChanged?.Invoke(this, new(nameof(CurrentDevice)));
                    }
                    
                    defaultDevice.Dispose();
                }

                // Fall back to DefaultDevice *Non-UI thread otherwise will freeze (not sure where and why) during xaudio.Dispose()
                if (removed.Count > 0)
                    Task.Run(() =>
                    {
                        foreach(var device in removed)
                        {
                            foreach(var player in Engine.Players)
                                if (player.Audio.Device == device)
                                    player.Audio.Device = DefaultDevice;
                        }
                    });
            }
        });
    }

    public void OnDeviceStateChanged(string pwstrDeviceId, int newState) => RefreshDevices();
    public void OnDeviceAdded(string pwstrDeviceId) => RefreshDevices();
    public void OnDeviceRemoved(string pwstrDeviceId) => RefreshDevices();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string pwstrDefaultDeviceId) => RefreshDevices();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public class AudioEndpoint
    {
        public string Id    { get; set; }
        public string Name  { get; set; }

        public override string ToString()
            => Name;
    }
}
