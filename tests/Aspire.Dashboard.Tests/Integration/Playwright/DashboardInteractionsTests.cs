// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.TestUtilities;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright;

// Browser coverage for grid auto-fit and the <aspire-scroll-to-bottom> custom element.
[RequiresFeature(TestFeature.Playwright)]
public class DashboardInteractionsTests : PlaywrightTestsBase<DashboardInteractionsTests.InteractionsDashboardServerFixture>
{
    public DashboardInteractionsTests(InteractionsDashboardServerFixture dashboardServerFixture)
        : base(dashboardServerFixture)
    {
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task GridColumn_DoubleClickResizeHandle_AutoFitsColumnWidth()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);

            var grid = page.Locator(".main-grid").First;
            await Assertions.Expect(grid).ToBeVisibleAsync();

            // Guard against a Fluent UI Blazor rename of the resize handle marker: the auto-fit
            // double-click handler keys off exactly these selectors, so if none is present the
            // feature is silently dead and this count assertion surfaces it.
            var handles = page.Locator(".main-grid [actual-resize-handle], .main-grid .resize-handle, .main-grid .col-width-draghandle");
            Assert.True(await handles.CountAsync() > 0, "Expected at least one grid resize handle to be rendered.");

            // Fluent writes the resolved template from GetGridTemplateColumns() to the table's *inline*
            // grid-template-columns, so it is already populated at rest (typically with fr/auto tracks).
            // auto-fit resolves every track to concrete px and rewrites the inline template with the
            // fitted column, so the reliable end-to-end proof is that the inline value *changes* and is
            // now an explicit px template.
            var inlineBefore = await grid.EvaluateAsync<string>("el => el.style.gridTemplateColumns");

            // The auto-fit behavior is a delegated document-level "dblclick" listener that keys off
            // e.target.closest(".resize-handle"). Dispatch the dblclick straight onto the handle element
            // rather than a pixel-precise click: the handle is a thin edge bar whose pointer-events are
            // gated, so a coordinate double-click retargets to the header behind it and never reaches the
            // handler. DispatchEvent fires a real bubbling MouseEvent on the exact element, which bubbles
            // to the document listener exactly as a user double-click on the handle would.
            await handles.First.DispatchEventAsync("dblclick");

            await page.WaitForFunctionAsync(
                @"before => {
                    const g = document.querySelector('.main-grid');
                    if (!g) { return false; }
                    const now = g.style.gridTemplateColumns;
                    return now.length > 0 && now !== before && /px/.test(now);
                }",
                inlineBefore)
                .DefaultTimeout();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_DelaysReveal_AndCancelsRevealWhenScrolledToBottom()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.Clock.InstallAsync();
            await page.Clock.PauseAtAsync(DateTime.UtcNow.AddMinutes(1));
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToHaveCountAsync(1);

            await page.Clock.RunForAsync(100);
            Assert.Equal("scroll-button scroll-to-bottom", await bottomButton.GetAttributeAsync("class"));

            // Restore the initial bottom position while the reveal timer is still pending.
            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    region.scrollTop = region.scrollHeight;
                    region.dispatchEvent(new Event('scroll'));
                }
                """);
            await page.Clock.RunForAsync(300);
            Assert.Equal("scroll-button scroll-to-bottom", await bottomButton.GetAttributeAsync("class"));

            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    region.scrollTop = 0;
                    region.dispatchEvent(new Event('scroll'));
                }
                """);
            await page.Clock.RunForAsync(199);
            Assert.Equal("scroll-button scroll-to-bottom", await bottomButton.GetAttributeAsync("class"));
            await page.Clock.RunForAsync(50);
            Assert.Equal("scroll-button scroll-to-bottom is-visible", await bottomButton.GetAttributeAsync("class"));
            await page.Clock.ResumeAsync();
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_SmoothScrollHidesImmediately_AndReachesNewBottom()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.NoPreference });
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();

            await page.EvaluateAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    const initialBottom = region.scrollHeight - region.clientHeight;
                    window.__grewDuringScroll = false;
                    region.addEventListener('scroll', () => {
                        window.__grewDuringScroll = region.scrollTop > 0 && region.scrollTop < initialBottom;
                        region.querySelector('.scroll-content').style.height = '4000px';
                    }, { once: true });
                    document.querySelector('.scroll-to-bottom').addEventListener('click', event => {
                        window.__hiddenOnClick = event.currentTarget.hidden;
                    }, { once: true });
                }
                """);

            await bottomButton.ClickAsync();
            Assert.True(await page.EvaluateAsync<bool>("() => window.__hiddenOnClick"));
            await page.WaitForFunctionAsync("""
                () => {
                    const region = document.getElementById('scroll-region');
                    return window.__grewDuringScroll && region.scrollHeight >= 4000 &&
                        Math.abs(region.scrollHeight - region.clientHeight - region.scrollTop) < 1;
                }
                """).DefaultTimeout();
            await Assertions.Expect(bottomButton).ToBeHiddenAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ReducedMotionScrollsImmediately()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();

            // Observe the destination in the same click dispatch, before any animation can run.
            await page.EvaluateAsync("""
                () => document.querySelector('.scroll-to-bottom').addEventListener('click', event => {
                    const region = document.getElementById('scroll-region');
                    window.__remainingOnClick = region.scrollHeight - region.clientHeight - region.scrollTop;
                    window.__hiddenOnClick = event.currentTarget.hidden;
                }, { once: true })
                """);
            await bottomButton.ClickAsync();
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__remainingOnClick"));
            Assert.True(await page.EvaluateAsync<bool>("() => window.__hiddenOnClick"));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_ModalBackdropBlocksClicks()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await AddScrollRegionAsync(page);
            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            var buttonBounds = (await bottomButton.BoundingBoxAsync())!;
            await page.EvaluateAsync("""
                () => {
                    const dialog = document.createElement('fluent-dialog');
                    dialog.id = 'scroll-overlay-dialog';
                    dialog.setAttribute('type', 'modal');
                    dialog.innerHTML = '<p>Modal content</p>';
                    document.body.appendChild(dialog);
                    dialog.show();
                }
                """);
            await page.WaitForFunctionAsync("() => document.getElementById('scroll-overlay-dialog').shadowRoot.querySelector('dialog').open").DefaultTimeout();
            await Assertions.Expect(bottomButton).ToBeVisibleAsync();
            await page.Mouse.ClickAsync(buttonBounds.X + buttonBounds.Width / 2, buttonBounds.Y + buttonBounds.Height / 2);
            Assert.Equal(0, await page.Locator("#scroll-region").EvaluateAsync<int>("region => region.scrollTop"));
            Assert.Null(await bottomButton.GetAttributeAsync("hidden"));
            await page.EvaluateAsync("""
                () => {
                    const dialog = document.getElementById('scroll-overlay-dialog');
                    dialog.hide();
                    dialog.remove();
                }
                """);

            await bottomButton.ClickAsync();
            Assert.Equal(0, await page.Locator("#scroll-region").EvaluateAsync<int>(
                "region => region.scrollHeight - region.clientHeight - region.scrollTop"));
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_CustomElementReplacesRegistration_AndCleansUpOnDisconnect()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
            await AddScrollRegionAsync(page);
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(1);

            await page.EvaluateAsync("""
                () => {
                    window.__previousScrollButton = document.querySelector('.scroll-to-bottom');
                    const parent = document.getElementById('scroll-owner').cloneNode(true);
                    parent.id = 'next-scroll-owner';
                    parent.firstElementChild.id = 'next-scroll-region';
                    document.body.appendChild(parent);
                    window.__nextScrollRoot = document.querySelector('.scroll-buttons');
                }
                """);
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(1);
            Assert.False(await page.EvaluateAsync<bool>("() => window.__previousScrollButton.isConnected"));

            // Click the replaced button while its container is still connected. A detached
            // container reports scrollTop=0 even if cleanup failed to remove the click listener.
            Assert.Equal(new[] { 0, 0 }, await page.EvaluateAsync<int[]>("""
                () => {
                    window.__previousScrollButton.click();
                    return [document.getElementById('scroll-region').scrollTop,
                        document.getElementById('next-scroll-region').scrollTop];
                }
                """));

            await page.EvaluateAsync("() => document.getElementById('scroll-owner').remove()");
            Assert.True(await page.EvaluateAsync<bool>("() => window.__nextScrollRoot.isConnected"));
            await page.Locator(".scroll-to-bottom").ClickAsync();
            Assert.Equal(0, await page.Locator("#next-scroll-region").EvaluateAsync<int>(
                "region => region.scrollHeight - region.clientHeight - region.scrollTop"));

            await page.EvaluateAsync("""
                () => {
                    window.__removedScrollOwner = document.getElementById('next-scroll-owner');
                    window.__removedScrollOwner.remove();
                }
                """);
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(0);

            await page.EvaluateAsync("() => document.body.appendChild(window.__removedScrollOwner)");
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(1);

            Assert.Equal(0, await page.EvaluateAsync<int>("""
                () => {
                    const region = document.getElementById('next-scroll-region');
                    region.scrollTop = 0;
                    const button = document.querySelector('.scroll-to-bottom');
                    region.querySelector('aspire-scroll-to-bottom').remove();
                    button.click();
                    return region.scrollTop;
                }
                """));
            await Assertions.Expect(page.Locator(".scroll-buttons")).ToHaveCountAsync(0);
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_RemainInactiveWhenRegionHasNoRoomForControl()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            await AddScrollRegionAsync(page);
            await Assertions.Expect(page.Locator(".scroll-to-bottom")).ToBeVisibleAsync();
            await page.Locator("#scroll-region").EvaluateAsync("region => region.style.top = '-280px'");
            var buttons = page.Locator(".scroll-buttons");
            await Assertions.Expect(buttons).ToHaveCountAsync(1);
            await page.EvaluateAsync("() => window.dispatchEvent(new Event('resize'))");
            await Assertions.Expect(buttons).ToHaveClassAsync("scroll-buttons");
            await Assertions.Expect(page.Locator(".scroll-to-bottom")).ToBeHiddenAsync();

            await page.EvaluateAsync("""
                () => {
                    document.getElementById('scroll-region').style.top = '0';
                    window.dispatchEvent(new Event('resize'));
                }
                """);
            await Assertions.Expect(page.Locator(".scroll-to-bottom")).ToBeVisibleAsync();
        });
    }

    [Fact]
    [OuterloopTest("Resource-intensive Playwright browser test")]
    public async Task ScrollButtons_UsesLabelFromCustomElement()
    {
        await RunTestAsync(async page =>
        {
            await GoToResourcesAndWaitAsync(page);
            Assert.Null(await page.Locator("body").GetAttributeAsync("data-scroll-to-bottom-label"));
            await AddScrollRegionAsync(page);

            var bottomButton = page.Locator(".scroll-to-bottom");
            await Assertions.Expect(bottomButton).ToHaveAttributeAsync("aria-label", "Jump to latest");
            await Assertions.Expect(bottomButton).ToHaveAttributeAsync("title", "Jump to latest");
        });
    }

    private static Task AddScrollRegionAsync(IPage page)
    {
        // Render the marker before the content, as in the Razor views. The Resources page has
        // no scroll marker of its own, so registration must come from this element's connection.
        return page.EvaluateAsync("""
            () => {
                const owner = document.createElement('div');
                owner.id = 'scroll-owner';
                owner.innerHTML = `
                    <div id="scroll-region" style="position:fixed;left:0;top:100px;width:400px;height:300px;overflow:auto;">
                        <aspire-scroll-to-bottom hidden data-scroll-to-bottom-label="Jump to latest"></aspire-scroll-to-bottom>
                        <div class="scroll-content" style="height:2000px"></div>
                    </div>`;
                document.body.appendChild(owner);
            }
            """);
    }

    private static async Task GoToResourcesAndWaitAsync(IPage page)
    {
        await page.GotoAsync("/");
        await Assertions
            .Expect(page.GetByText(InteractionsDashboardServerFixture.ParentResourceName).First)
            .ToBeVisibleAsync();
    }

    public sealed class InteractionsDashboardServerFixture : DashboardServerFixture
    {
        public const string ParentResourceName = "parentapp";

        protected override IReadOnlyList<ResourceViewModel> Resources =>
        [
            ModelTestHelpers.CreateResource(
                resourceName: ParentResourceName,
                resourceType: KnownResourceTypes.Project,
                state: KnownResourceState.Running,
                urls:
                [
                    new UrlViewModel("http", new Uri("about:blank#parent-url"), isInternal: false, isInactive: false, UrlDisplayPropertiesViewModel.Empty)
                ]),
        ];
    }
}
