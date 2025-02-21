﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Data;

using SharpGen.Runtime;
using SharpGen.Runtime.Win32;

using Vortice.MediaFoundation;
using static Vortice.XAudio2.XAudio2;

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
    public bool         Failed              { get; private set; }

    /// <summary>
    /// List of Audio Capture Devices
    /// </summary>
    public ObservableCollection<AudioDevice>
                        CapDevices          { get; set; } = new();

    public void         RefreshCapDevices() => AudioDevice.RefreshDevices();

    /// <summary>
    /// List of Audio Devices
    /// </summary>
    public ObservableCollection<AudioEndpoint>
                        Devices             { get; private set; } = new();

    private readonly object lockDevices = new();
    #endregion

    IMMDeviceEnumerator deviceEnum;
    private object      locker = new();

    public event PropertyChangedEventHandler PropertyChanged;

    public AudioEngine()
    {
        if (Engine.Config.DisableAudio)
        {
            Failed = true;
            return;
        }

        BindingOperations.EnableCollectionSynchronization(Devices, lockDevices);
        EnumerateDevices();
    }

    private void EnumerateDevices()
    {
        try
        {
            deviceEnum = new IMMDeviceEnumerator();

            var defaultDevice = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice == null)
            {
                Failed = true;
                return;
            }

            lock (lockDevices)
            {
                Devices.Clear();
                Devices.Add(DefaultDevice);
                foreach (var device in deviceEnum.EnumAudioEndpoints(DataFlow.Render, DeviceStates.Active))
                    Devices.Add(new() { Id = device.Id, Name = device.FriendlyName });
            }

            CurrentDevice.Id    = defaultDevice.Id;
            CurrentDevice.Name  = defaultDevice.FriendlyName;

            if (Logger.CanInfo)
            {
                string dump = "";
                foreach (var device in deviceEnum.EnumAudioEndpoints(DataFlow.Render, DeviceStates.Active))
                    dump += $"{device.Id} | {device.FriendlyName} {(defaultDevice.Id == device.Id ? "*" : "")}\r\n";
                Engine.Log.Info($"Audio Devices\r\n{dump}");
            }

            var xaudio2 = XAudio2Create();

            if (xaudio2 == null)
                Failed = true;
            else
                xaudio2.Dispose();

            deviceEnum.RegisterEndpointNotificationCallback(this);

        } catch { Failed = true; }
    }
    private void RefreshDevices()
    {
        lock (locker)
        {
            Utils.UIInvokeIfRequired(() => // UI Required?
            {
                List<AudioEndpoint> curs     = new();
                List<AudioEndpoint> removed  = new();

                lock (lockDevices)
                {
                    foreach(var device in deviceEnum.EnumAudioEndpoints(DataFlow.Render, DeviceStates.Active))
                        curs.Add(new () { Id = device.Id, Name = device.FriendlyName });

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
                if (defaultDevice != null && CurrentDevice.Id != defaultDevice.Id)
                {
                    CurrentDevice.Id    = defaultDevice.Id;
                    CurrentDevice.Name  = defaultDevice.FriendlyName;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDevice)));
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
            });
        }
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
