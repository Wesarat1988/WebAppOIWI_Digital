const READY_EVENT = "pdfViewer:initialized";
const POLL_INTERVAL_MS = 50;
const MAX_ATTEMPTS = 200;

function waitForPdfViewer() {
    if (typeof window !== "undefined" && window.pdfViewer) {
        return Promise.resolve(window.pdfViewer);
    }

    return new Promise((resolve) => {
        let attempts = 0;
        let resolved = false;

        const complete = () => {
            if (resolved) {
                return;
            }

            if (typeof window !== "undefined" && window.pdfViewer) {
                resolved = true;
                cleanup();
                resolve(window.pdfViewer);
            }
        };

        const cleanup = () => {
            clearInterval(intervalId);
            document.removeEventListener(READY_EVENT, complete);
        };

        const intervalId = setInterval(() => {
            attempts += 1;
            if (attempts >= MAX_ATTEMPTS) {
                resolved = true;
                cleanup();
                if (typeof window !== "undefined" && window.pdfViewer) {
                    resolve(window.pdfViewer);
                } else {
                    resolve(createFallbackViewer());
                }
            } else {
                complete();
            }
        }, POLL_INTERVAL_MS);

        document.addEventListener(READY_EVENT, complete, { once: true });
        complete();
    });
}

function createFallbackViewer() {
    return {
        ready: () => Promise.resolve(),
        render: () => console.warn("pdfViewer is not available yet; render request ignored."),
        zoomIn: () => console.warn("pdfViewer is not available yet; zoomIn ignored."),
        zoomOut: () => console.warn("pdfViewer is not available yet; zoomOut ignored."),
        fitWidth: () => console.warn("pdfViewer is not available yet; fitWidth ignored."),
        getPageCount: () => 0,
        getCurrentPageIndex: () => 0,
        goToPage: () => console.warn("pdfViewer is not available yet; goToPage ignored."),
        initializeFullScreen: () => { },
        requestFullScreen: () => Promise.resolve(),
        exitFullScreen: () => Promise.resolve(),
        disposeFullScreen: () => { },
        focusFullScreenHost: () => { }
    };
}

async function withViewer(method, ...args) {
    const viewer = await waitForPdfViewer();
    if (!viewer) {
        return undefined;
    }

    const target = viewer[method];
    if (typeof target !== "function") {
        console.warn(`pdfViewer method '${method}' is unavailable.`);
        return undefined;
    }

    return target.apply(viewer, args);
}

export async function initializeFullScreen(dotNetRef, hostId, viewerId) {
    await withViewer("initializeFullScreen", dotNetRef, hostId, viewerId);
}

export async function disposeFullScreen(hostId) {
    await withViewer("disposeFullScreen", hostId);
}

export async function requestFullScreen(hostId) {
    await withViewer("requestFullScreen", hostId);
}

export async function exitFullScreen(hostId) {
    await withViewer("exitFullScreen", hostId);
}

export async function focusFullScreenHost(hostId) {
    await withViewer("focusFullScreenHost", hostId);
}

export async function ready() {
    const result = await withViewer("ready");
    return result;
}

export async function render(source, containerId) {
    await withViewer("render", source, containerId);
}

export async function zoomIn(containerId) {
    await withViewer("zoomIn", containerId);
}

export async function zoomOut(containerId) {
    await withViewer("zoomOut", containerId);
}

export async function fitWidth(containerId) {
    await withViewer("fitWidth", containerId);
}

export async function goToPage(containerId, pageNumber, smooth) {
    await withViewer("goToPage", containerId, pageNumber, smooth);
}

export async function getPageCount(containerId) {
    const value = await withViewer("getPageCount", containerId);
    return typeof value === "number" ? value : 0;
}

export async function getCurrentPageIndex(containerId) {
    const value = await withViewer("getCurrentPageIndex", containerId);
    return typeof value === "number" ? value : 0;
}
