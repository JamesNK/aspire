// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunSelect : ComponentBase
{
    private readonly List<MenuButtonItem> _menuItems = [];
    private string RunSelectAriaLabel => Loc[nameof(LayoutResources.DashboardRunSelectAriaLabel)];
    private string SelectedRunText => SelectedRunIsCurrent
        ? Loc[nameof(LayoutResources.DashboardRunSelectCurrent)]
        : FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, SelectedRunStartedAtUtc.UtcDateTime);

    [Parameter, EditorRequired]
    public required string SelectedRunId { get; set; }

    [Parameter]
    public bool SelectedRunIsCurrent { get; set; }

    [Parameter]
    public DateTimeOffset SelectedRunStartedAtUtc { get; set; }

    [Parameter]
    public EventCallback<string?> SelectedRunIdChanged { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IDashboardRunStore RunStore { get; init; }

    private void LoadRuns()
    {
        var runs = RunStore.GetRuns().Values
            .Select(run => new
            {
                Run = run,
                run.IsPinned,
                Text = FormatRunOption(run)
            })
            .OrderByDescending(item => item.Run.IsCurrent)
            .ThenByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.Run.StartedAtUtc)
            .ToArray();

        _menuItems.Clear();
        foreach (var item in runs)
        {
            var run = item.Run;
            _menuItems.Add(new MenuButtonItem
            {
                Text = item.Text,
                Icon = string.Equals(run.RunId, SelectedRunId, StringComparison.Ordinal)
                    ? new Icons.Regular.Size16.Checkmark()
                    : null,
                OnClick = () => SelectedRunIdChanged.InvokeAsync(run.IsCurrent ? null : run.RunId),
                SecondaryActionIcon = item.IsPinned ? new Icons.Filled.Size16.Pin() : new Icons.Regular.Size16.Pin(),
                SecondaryActionAriaLabel = Loc[item.IsPinned ? nameof(LayoutResources.DashboardRunSelectUnpin) : nameof(LayoutResources.DashboardRunSelectPin)].Value,
                OnSecondaryActionClick = () => SetRunPinnedAsync(run, !item.IsPinned),
                IsSecondaryActionSelected = item.IsPinned
            });

            if (run.IsCurrent && runs.Any(candidate => !candidate.Run.IsCurrent))
            {
                _menuItems.Add(new MenuButtonItem { IsDivider = true });
            }
        }
    }

    private Task SetRunPinnedAsync(DashboardRunDescriptor run, bool isPinned)
    {
        RunStore.SetRunPinned(run, isPinned);
        LoadRuns();
        return InvokeAsync(StateHasChanged);
    }

    private string FormatRunOption(DashboardRunDescriptor run)
    {
        if (run.IsCurrent)
        {
            return Loc[nameof(LayoutResources.DashboardRunSelectCurrent)];
        }

        return FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, run.StartedAtUtc.UtcDateTime);
    }
}