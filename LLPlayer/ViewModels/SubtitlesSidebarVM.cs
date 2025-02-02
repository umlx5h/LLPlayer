﻿using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Extensions;
using LLPlayer.Services;
using LLPlayer.Views;

namespace LLPlayer.ViewModels;

public class SubtitlesSidebarVM : Bindable
{
    public FlyleafManager FL { get; }
    private readonly IDialogService _dialogService;

    public SubtitlesSidebarVM(FlyleafManager fl, IDialogService dialogService)
    {
        FL = fl;
        _dialogService = dialogService;
    }

    public bool IsPrimary
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                OnPropertyChanged(nameof(SubIndex));
                OnPropertyChanged(nameof(SubManager));
            }
        }
    } = true;

    public int SubIndex => IsPrimary ? 0 : 1;

    public SubManager SubManager => FL.Player.SubtitlesManager[SubIndex];

    // ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

    public DelegateCommand CmdSubIndexToggle => field ??= new(() =>
    {
        IsPrimary = !IsPrimary;
    });

    // TODO: L: Fix implicit changes to reflect
    public DelegateCommand<string> CmdSubFontSizeChange => field ??= new(increase =>
    {
        if (int.TryParse(increase, out var value))
        {
            FL.Config.SidebarFontSize += value;
        }
    });

    public DelegateCommand CmdSubTextMaskToggle => field ??= new(() =>
    {
        FL.Config.SidebarTextMask = !FL.Config.SidebarTextMask;

        // Update ListBox
        OnPropertyChanged(nameof(SubManager));
    });

    public DelegateCommand CmdShowOriginalTextToggle => field ??= new(() =>
    {
        FL.Config.SidebarShowOriginalText = !FL.Config.SidebarShowOriginalText;

        // Update ListBox
        OnPropertyChanged(nameof(SubManager));
    });

    public DelegateCommand CmdSwapSidebarPosition => field ??= new(() =>
    {
        FL.Config.SidebarLeft = !FL.Config.SidebarLeft;
    });

    public DelegateCommand CmdShowDownloadSubDialog => field ??= new(() =>
    {
        _dialogService.ShowSingleton(nameof(SubtitlesDownloaderDialog), true);
    });

    public DelegateCommand<int?> CmdSubPlay => field ??= new((index) =>
    {
        if (!index.HasValue)
        {
            return;
        }

        // If paused, start playing after seek
        if (!FL.Player.IsPlaying)
        {
            FL.Player.SeekCompleted += PlayerOnSeekCompleted;

            void PlayerOnSeekCompleted(object? sender, int args)
            {
                FL.Player.SeekCompleted -= PlayerOnSeekCompleted;

                if (args != -1)
                {
                    Utils.UI(() =>
                    {
                        if (!FL.Player.IsPlaying)
                        {
                            FL.Player.Play();
                        }
                    });
                }
            }
        }

        var sub = SubManager.Subs[index.Value];
        FL.Player.SeekAccurate(sub.StartTime, SubIndex);
    });

    public DelegateCommand<int?> CmdSubSync => field ??= new((index) =>
    {
        if (!index.HasValue)
        {
            return;
        }

        var sub = SubManager.Subs[index.Value];
        var newDelay = FL.Player.CurTime - sub.StartTime.Ticks;

        // Delay's OSD is not displayed, temporarily set to Active
        FL.Player.Activity.ForceActive();

        FL.PlayerConfig.Subtitles[SubIndex].Delay = newDelay;
    });

    // ReSharper restore NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
}
