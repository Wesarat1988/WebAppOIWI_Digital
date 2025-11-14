(function () {
    const READY_EVENT = "pdfViewer:initialized";
    const POLL_INTERVAL_MS = 50;
    const MAX_ATTEMPTS = 200;

    let pendingViewerPromise = null;

    function resetPromise() {
        pendingViewerPromise = null;
    }

    function waitForViewer() {
        if (typeof window !== "undefined" && window.pdfViewer) {
            return Promise.resolve(window.pdfViewer);
        }

        if (!pendingViewerPromise) {
            pendingViewerPromise = new Promise((resolve, reject) => {
                let attempts = 0;

                const cleanup = () => {
                    clearInterval(intervalId);
                    document.removeEventListener(READY_EVENT, handleReady);
                };

                const handleReady = () => {
                    if (typeof window !== "undefined" && window.pdfViewer) {
                        cleanup();
                        resolve(window.pdfViewer);
                    }
                };

                const intervalId = setInterval(() => {
                    attempts += 1;
                    if (typeof window !== "undefined" && window.pdfViewer) {
                        cleanup();
                        resolve(window.pdfViewer);
                        return;
                    }

                    if (attempts >= MAX_ATTEMPTS) {
                        cleanup();
                        reject(new Error("pdfViewer is not available"));
                    }
                }, POLL_INTERVAL_MS);

                document.addEventListener(READY_EVENT, handleReady, { once: true });
                handleReady();
            });
        }

        return pendingViewerPromise.catch(error => {
            console.warn(error?.message ?? error);
            resetPromise();
            throw error;
        });
    }

    async function callViewer(method, args) {
        const viewer = await waitForViewer();
        if (!viewer) {
            throw new Error("pdfViewer instance is unavailable");
        }

        const target = viewer[method];
        if (typeof target !== "function") {
            throw new Error(`pdfViewer method '${method}' is unavailable.`);
        }

        return await target.apply(viewer, args || []);
    }

    async function tryCall(method, args) {
        try {
            return await callViewer(method, args);
        } catch (error) {
            console.warn(error?.message ?? error);
            throw error;
        }
    }

    window.pdfViewerInterop = {
        waitForViewer: () => waitForViewer().catch(() => null),
        initializeFullScreen: async function (dotNetRef, hostId, viewerId) {
            await tryCall("initializeFullScreen", [dotNetRef, hostId, viewerId]);
        },
        disposeFullScreen: function (hostId) {
            return tryCall("disposeFullScreen", [hostId]);
        },
        requestFullScreen: function (hostId) {
            return tryCall("requestFullScreen", [hostId]);
        },
        exitFullScreen: function (hostId) {
            return tryCall("exitFullScreen", [hostId]);
        },
        focusFullScreenHost: function (hostId) {
            return tryCall("focusFullScreenHost", [hostId]);
        },
        ready: function () {
            return tryCall("ready", []);
        },
        render: function (source, containerId) {
            return tryCall("render", [source, containerId]);
        },
        zoomIn: function (containerId) {
            return tryCall("zoomIn", [containerId]);
        },
        zoomOut: function (containerId) {
            return tryCall("zoomOut", [containerId]);
        },
        fitWidth: function (containerId) {
            return tryCall("fitWidth", [containerId]);
        },
        goToPage: function (containerId, pageNumber, smooth) {
            return tryCall("goToPage", [containerId, pageNumber, smooth]);
        },
        getPageCount: function (containerId) {
            return tryCall("getPageCount", [containerId]).catch(() => 0);
        },
        getCurrentPageIndex: function (containerId) {
            return tryCall("getCurrentPageIndex", [containerId]).catch(() => 0);
        }
    };
})();
