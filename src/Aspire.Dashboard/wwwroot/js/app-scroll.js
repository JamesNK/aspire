// Scroll-to-bottom button for live-data scroll containers.
// Console logs, traces, and structured logs can grow to thousands of lines. Add a floating jump-to-
// bottom button only when those regions meaningfully overflow and the user isn't already near the end.
//
// Design notes:
// - The control is appended to <body> and positioned with `position: fixed`, tracking the target's
//   getBoundingClientRect(). We deliberately do NOT wrap or inject nodes inside the scroll container
//   because that DOM is owned by Blazor's renderer; adding foreign children there can trip Blazor's
//   node diffing. A body-level sibling is invisible to the render tree.
// - Discovery re-runs on a debounced MutationObserver so it survives Blazor SPA navigation;
//   registration is idempotent (guarded by a WeakSet).
// - Reposition/visibility updates are throttled through requestAnimationFrame and driven by the
//   container's own 'scroll', a ResizeObserver, and window scroll/resize (capture-phase, because
//   inner scroll events don't bubble to window).
// - Container scrolling only updates visibility. Layout and cached button dimensions are refreshed
//   on resize; ancestor scrolling and discovery changes also invalidate the container's position.

const targetSelector = ".continuous-scroll-overflow";

// Only surface the buttons once there's a meaningful amount to scroll past, so they stay out of
// the way for small content. Roughly 1.5 viewports of the region reads as "large" in practice.
const overflowThreshold = 240;
// How far from an edge the user must be before the matching button appears.
const edgeThreshold = 120;
// Avoid flashing the button while a newly loaded page is still restoring its scroll position.
const buttonShowDelay = 200;

// The only body-level structural changes we care about: a scroll target appearing/disappearing,
// or a dialog opening/closing (updateEntry() also keys visibility off whether a dialog is open).
// Used to cheaply skip the rescan on high-churn mutations that touch none of these.
const mutationTriggerSelector = targetSelector + ", fluent-dialog";

const chevronDown = '<svg viewBox="0 0 20 20" aria-hidden="true"><path d="M4.47 7.03a.75.75 0 0 1 1.06-1.06L10 10.44l4.47-4.47a.75.75 0 1 1 1.06 1.06l-5 5a.75.75 0 0 1-1.06 0l-5-5Z"/></svg>';

const registered = new WeakSet();
const controls = []; // { container, root, bottomBtn, resizeObserver }
let rafPending = false;

function scheduleUpdate() {
    if (rafPending) {
        return;
    }
    rafPending = true;
    requestAnimationFrame(function () {
        rafPending = false;
        updateAll();
    });
}

function scheduleLayoutUpdate() {
    for (const entry of controls) {
        entry.layoutDirty = true;
    }
    scheduleUpdate();
}

function cancelPendingScrollToBottom(entry) {
    if (entry.scrollEndHandler !== null) {
        entry.container.removeEventListener("scrollend", entry.scrollEndHandler);
        entry.scrollEndHandler = null;
    }
}

function clearButtonShowTimer(entry) {
    if (entry.showTimer !== null) {
        clearTimeout(entry.showTimer);
        entry.showTimer = null;
    }
}

function hideBottomButton(entry, immediately = false) {
    clearButtonShowTimer(entry);
    entry.bottomBtn.classList.remove("is-visible");
    if (immediately) {
        // Bypass the normal opacity transition when the user has activated the button.
        entry.bottomBtn.hidden = true;
    }
}

function showBottomButton(entry) {
    clearButtonShowTimer(entry);
    entry.bottomBtn.hidden = false;
    entry.bottomBtn.classList.add("is-visible");
}

function scheduleBottomButtonShow(entry) {
    if (entry.showTimer !== null || entry.bottomBtn.classList.contains("is-visible")) {
        return;
    }

    entry.showTimer = setTimeout(function () {
        entry.showTimer = null;
        // Re-evaluate after the delay because page loading may have scrolled the region to the
        // bottom or removed it while the button was waiting to appear.
        updateEntry(entry, true);
    }, buttonShowDelay);
}

function scrollToLatestBottom(entry) {
    const container = entry.container;
    cancelPendingScrollToBottom(entry);

    const initialScrollHeight = container.scrollHeight;
    if (window.matchMedia?.("(prefers-reduced-motion: reduce)").matches) {
        container.scrollTo({ top: initialScrollHeight, behavior: "auto" });
        return;
    }

    entry.scrollEndHandler = function () {
        entry.scrollEndHandler = null;
        // Native smooth scrolling retains its original destination. If streaming content moved
        // the bottom during the animation, finish with an immediate jump to the latest bottom.
        if (container.scrollHeight > initialScrollHeight) {
            container.scrollTop = container.scrollHeight;
        }
        scheduleUpdate();
    };
    container.addEventListener("scrollend", entry.scrollEndHandler, { once: true });

    container.scrollTo({ top: initialScrollHeight, behavior: "smooth" });
}

function makeButton(kind, label, svg) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "scroll-button scroll-to-" + kind;
    btn.setAttribute("aria-label", label);
    btn.setAttribute("title", label);
    // Supplemental affordance only - keyboard users can already scroll the focused region
    // natively, so keep these out of the tab order to avoid extra tab stops per container.
    btn.tabIndex = -1;
    btn.innerHTML = svg;
    return btn;
}

function register(container) {
    if (registered.has(container)) {
        return;
    }
    registered.add(container);

    const root = document.createElement("div");
    root.className = "scroll-buttons";
    // The label is localized in .NET and rendered onto <body> by App.razor. This button is created
    // purely in JS, so read it from the document and retain a defensive accessible-name fallback.
    const labels = document.body?.dataset ?? {};
    const bottomBtn = makeButton("bottom", labels.scrollToBottomLabel || "Scroll to bottom", chevronDown);
    root.appendChild(bottomBtn);
    document.body.appendChild(root);

    const entry = {
        container, root, bottomBtn, scrollEndHandler: null, showTimer: null,
        layoutDirty: true, layoutActive: false, buttonSize: null
    };

    bottomBtn.addEventListener("click", function () {
        hideBottomButton(entry, true);
        scrollToLatestBottom(entry);
    });

    const cancelForUserInput = function () {
        cancelPendingScrollToBottom(entry);
        scheduleUpdate();
    };
    container.addEventListener("wheel", cancelForUserInput, { passive: true });
    container.addEventListener("pointerdown", cancelForUserInput, { passive: true });
    container.addEventListener("keydown", cancelForUserInput);

    controls.push(entry);

    container.addEventListener("scroll", scheduleUpdate, { passive: true });
    const resizeObserver = new ResizeObserver(function () {
        entry.buttonSize = null;
        entry.layoutDirty = true;
        scheduleUpdate();
    });
    resizeObserver.observe(container);
    entry.resizeObserver = resizeObserver;

    scheduleUpdate();
}

function updateEntry(entry, showImmediately = false) {
    const container = entry.container;
    const root = entry.root;

    // Drop controls whose container has been removed (page navigation, dialog closed).
    if (!container.isConnected) {
        cancelPendingScrollToBottom(entry);
        clearButtonShowTimer(entry);
        if (entry.resizeObserver) {
            entry.resizeObserver.disconnect();
        }
        root.remove();
        return false;
    }

    if (entry.layoutDirty) {
        updateLayout(entry);
    }
    updateVisibility(entry, showImmediately);
    return true;
}

function updateLayout(entry) {
    entry.layoutDirty = false;
    const container = entry.container;
    const root = entry.root;
    const rect = container.getBoundingClientRect();
    const padding = 12;
    const scrollbarWidth = container.offsetWidth - container.clientWidth;
    const visibleLeft = Math.max(rect.left, 0);
    const visibleRight = Math.min(rect.right - scrollbarWidth, window.innerWidth);
    const visibleTop = Math.max(rect.top, 0);
    const visibleBottom = Math.min(rect.bottom, window.innerHeight);
    const visibleWidth = Math.max(0, visibleRight - visibleLeft);
    const visibleHeight = Math.max(0, visibleBottom - visibleTop);
    if (entry.buttonSize === null) {
        const buttonStyle = getComputedStyle(entry.bottomBtn);
        entry.buttonSize = {
            width: Number.parseFloat(buttonStyle.width),
            height: Number.parseFloat(buttonStyle.height)
        };
    }
    let active =
        rect.width > 0 &&
        rect.height > 0 &&
        visibleWidth >= entry.buttonSize.width &&
        visibleHeight >= entry.buttonSize.height + padding * 2;

    // When a modal dialog is open, only show buttons for containers inside it; otherwise the
    // page's own buttons would float on top of the dialog surface.
    const openDialog = document.querySelector("fluent-dialog");
    if (openDialog && !openDialog.contains(container)) {
        active = false;
    }

    entry.layoutActive = active;
    if (!active) {
        return;
    }

    // Center the control horizontally over the region and anchor it near the visible bottom edge.
    // Clamp its span to the viewport and exclude the scrollbar from the horizontal center.
    root.style.right = "auto";
    root.style.bottom = "auto";
    root.style.left = (visibleLeft + visibleWidth / 2) + "px";
    root.style.top = (visibleTop + padding) + "px";
    root.style.height = (visibleHeight - padding * 2) + "px";
}

function updateVisibility(entry, showImmediately) {
    const container = entry.container;
    const overflow = container.scrollHeight - container.clientHeight;
    const active = entry.layoutActive && overflow > overflowThreshold;
    entry.root.classList.toggle("is-active", active);
    if (!active) {
        hideBottomButton(entry);
        return;
    }

    const atBottom = overflow - container.scrollTop <= edgeThreshold;
    const shouldShow = !atBottom && entry.scrollEndHandler === null;
    if (!shouldShow) {
        hideBottomButton(entry);
    } else if (showImmediately) {
        showBottomButton(entry);
    } else {
        scheduleBottomButtonShow(entry);
    }
}

function updateAll() {
    for (let index = controls.length - 1; index >= 0; index--) {
        const keep = updateEntry(controls[index]);
        if (!keep) {
            registered.delete(controls[index].container);
            controls.splice(index, 1);
        }
    }
}

function scan() {
    for (const element of document.querySelectorAll(targetSelector)) {
        register(element);
    }
}

// Debounced rescan so SPA navigation and dialog opens are picked up without thrashing.
let scanTimer = null;
function scheduleScan() {
    if (scanTimer !== null) {
        return;
    }
    scanTimer = setTimeout(function () {
        scanTimer = null;
        scan();
        scheduleLayoutUpdate();
    }, 200);
}

// Capture ancestor scrolls, which move the region relative to the viewport. The region's own
// scrolling changes only its content position and must not invalidate the cached layout.
window.addEventListener("scroll", function (event) {
    for (const entry of controls) {
        if (event.target !== entry.container &&
            (event.target === document || event.target === window || event.target.contains?.(entry.container))) {
            entry.layoutDirty = true;
            scheduleUpdate();
        }
    }
}, { passive: true, capture: true });
window.addEventListener("resize", function () {
    for (const entry of controls) {
        entry.buttonSize = null;
    }
    scheduleLayoutUpdate();
}, { passive: true });

function start() {
    scan();
    // A body-wide subtree observer is required because scroll targets are inserted deep in
    // Blazor's render tree (SPA navigation) and dialogs are appended at the <body> level. But
    // reacting to every mutation batch would run a document-wide querySelectorAll scan on a
    // 200ms cadence for nothing on high-churn pages (streaming console logs, large grids). So we
    // first cheaply check whether a batch actually added or removed a scroll target (or a dialog)
    // before scheduling a rescan; pure content churn inside an already-registered container is
    // ignored. This keeps discovery correct while dropping the continuous idle cost.
    new MutationObserver(onBodyMutations).observe(document.body, { childList: true, subtree: true });
}

function onBodyMutations(mutations) {
    for (const mutation of mutations) {
        if (nodeListHasTrigger(mutation.addedNodes) || nodeListHasTrigger(mutation.removedNodes)) {
            scheduleScan();
            return;
        }
    }
}

function nodeListHasTrigger(nodes) {
    for (const node of nodes) {
        // Only element nodes can be (or contain) a scroll region or dialog; skip text/comment
        // churn, which is what streaming log output mostly produces.
        if (node.nodeType !== 1) {
            continue;
        }
        if (node.matches?.(mutationTriggerSelector) || node.querySelector?.(mutationTriggerSelector)) {
            return true;
        }
    }
    return false;
}

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start, { once: true });
} else {
    start();
}