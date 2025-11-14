// wwwroot/js/pdfViewer.js
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";

    let loader; // promise โหลด pdf.js
    const views = new Map(); // เก็บ state ต่อ containerId
    const fullscreenHosts = new Map();
    const viewerCallbacks = new Map();
    let fullscreenEventsBound = false;
    let isReadyResolve;
    const readyPromise = new Promise(resolve => {
        isReadyResolve = resolve;
    });

    function escapeHtml(value) {
        if (value === null || value === undefined) {
            return "";
        }

        return String(value).replace(/[&<>"']/g, character => {
            switch (character) {
                case "&":
                    return "&amp;";
                case "<":
                    return "&lt;";
                case ">":
                    return "&gt;";
                case '"':
                    return "&quot;";
                case "'":
                    return "&#39;";
                default:
                    return character;
            }
        });
    }

    function sanitizeUrl(value) {
        if (!value) {
            return "";
        }

        return String(value)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    function ensureLoaded() {
        if (window.pdfjsLib) {
            window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
            if (isReadyResolve) {
                isReadyResolve();
                isReadyResolve = null;
            }
            return Promise.resolve(window.pdfjsLib);
        }
        if (!loader) {
            loader = new Promise((resolve, reject) => {
                const script = document.createElement("script");
                script.src = PDF_JS_CDN;
                script.async = true;
                script.onload = () => {
                    if (!window.pdfjsLib) {
                        reject(new Error("pdf.js not found"));
                        return;
                    }
                    window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
                    if (isReadyResolve) {
                        isReadyResolve();
                        isReadyResolve = null;
                    }
                    resolve(window.pdfjsLib);
                };
                script.onerror = () => reject(new Error("failed to load pdf.js"));
                document.head.appendChild(script);
            });
        }
        return loader;
    }

    function getFullscreenElement() {
        return document.fullscreenElement
            || document.webkitFullscreenElement
            || document.msFullscreenElement
            || null;
    }

    function requestFullscreen(element) {
        if (!element) {
            return Promise.reject(new Error("missing fullscreen element"));
        }

        const request = element.requestFullscreen
            || element.webkitRequestFullscreen
            || element.msRequestFullscreen;

        if (request) {
            return request.call(element);
        }

        return Promise.reject(new Error("fullscreen api not supported"));
    }

    function exitFullscreen() {
        const exit = document.exitFullscreen
            || document.webkitExitFullscreen
            || document.msExitFullscreen;

        if (exit) {
            return exit.call(document);
        }

        return Promise.resolve();
    }

    function ensureFullscreenEvents() {
        if (fullscreenEventsBound) {
            return;
        }

        const handler = handleFullscreenChange;
        document.addEventListener("fullscreenchange", handler);
        document.addEventListener("webkitfullscreenchange", handler);
        document.addEventListener("msfullscreenchange", handler);
        fullscreenEventsBound = true;
    }

    function handleFullscreenChange() {
        const activeElement = getFullscreenElement();

        fullscreenHosts.forEach((state, hostId) => {
            const host = document.getElementById(hostId);
            const isActive = !!host && host === activeElement;
            if (state.isActive === isActive) {
                return;
            }

            state.isActive = isActive;

            if (host) {
                host.classList.toggle("pdf-fullscreen-active", isActive);
            }

            if (isActive) {
                focusFullScreenHost(hostId);
                attachKeyHandler(state);
            } else {
                detachKeyHandler(state);
            }

            if (state.dotNetRef) {
                state.dotNetRef.invokeMethodAsync("OnFullScreenChangedFromJsAsync", hostId, isActive).catch(() => { });
            }
        });
    }

    function attachKeyHandler(state) {
        if (state.keyHandler) {
            return;
        }

        state.keyHandler = (event) => {
            if (!state.isActive) {
                return;
            }

            let command;
            switch (event.key) {
                case "ArrowRight":
                case "PageDown":
                case " ":
                    command = "next";
                    break;
                case "ArrowLeft":
                case "PageUp":
                case "Backspace":
                    command = "previous";
                    break;
                case "Escape":
                    command = "exit";
                    break;
                default:
                    return;
            }

            event.preventDefault();

            if (state.dotNetRef) {
                state.dotNetRef.invokeMethodAsync("HandleFullScreenCommandFromJsAsync", command).catch(() => { });
            }
        };

        document.addEventListener("keydown", state.keyHandler, true);
    }

    function detachKeyHandler(state) {
        if (!state.keyHandler) {
            return;
        }

        document.removeEventListener("keydown", state.keyHandler, true);
        state.keyHandler = null;
    }

    function focusFullScreenHost(hostId) {
        const host = document.getElementById(hostId);
        if (!host || typeof host.focus !== "function") {
            return;
        }

        try {
            host.focus({ preventScroll: true });
        } catch (error) {
            try {
                host.focus();
            } catch (innerError) {
                // ignore focus failures
            }
        }
    }

    // preload
    ensureLoaded().catch(console.error);

    function computeFitWidthScale(page, containerWidth) {
        const viewport = page.getViewport({ scale: 1 });
        const width = containerWidth || viewport.width;
        return width / viewport.width;
    }

    async function render(url, containerId) {
        await ready();
        const host = document.getElementById(containerId);
        if (!host) {
            notifyRenderStatus(containerId, false, "ไม่พบตำแหน่งสำหรับแสดงไฟล์ PDF");
            return;
        }

        host.innerHTML = '<div class="pdfjs-loading text-muted text-center p-4">กำลังโหลดตัวอย่างเอกสาร PDF...</div>';
        host.style.position = "relative";

        try {
            const pdf = await window.pdfjsLib.getDocument({ url, withCredentials: true }).promise;
            const state = {
                pdf,
                scale: 1,
                fitWidthScale: 1,
                pages: [],
                containerId
            };
            views.set(containerId, state);
            host.innerHTML = "";

            for (let pageIndex = 1; pageIndex <= pdf.numPages; pageIndex++) {
                const page = await pdf.getPage(pageIndex);

                const fit = computeFitWidthScale(page, host.clientWidth);
                if (pageIndex === 1) {
                    state.fitWidthScale = fit;
                    state.scale = fit;
                    updateToolbarScale(containerId, state.scale);
                }

                const viewport = page.getViewport({ scale: state.scale });
                const dpr = window.devicePixelRatio || 1;

                const canvas = document.createElement("canvas");
                canvas.className = "pdfjs-page-canvas";
                canvas.style.display = "block";
                canvas.style.margin = "0 auto 16px";
                canvas.style.width = `${Math.floor(viewport.width)}px`;
                canvas.style.height = `${Math.floor(viewport.height)}px`;

                canvas.width = Math.floor(viewport.width * dpr);
                canvas.height = Math.floor(viewport.height * dpr);

                const context = canvas.getContext("2d", { alpha: false });
                host.appendChild(canvas);

                await page.render({
                    canvasContext: context,
                    viewport,
                    transform: dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : null
                }).promise;

                state.pages.push({ canvas, ctx: context, page, dpr });
            }
            notifyRenderStatus(containerId, true, null);
        } catch (error) {
            console.error("PDF render error", error);
            host.innerHTML = '<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถโหลดตัวอย่างไฟล์ PDF ได้</div>';
            notifyRenderStatus(containerId, false, "ไม่สามารถโหลดตัวอย่างไฟล์ PDF ได้ กรุณาลองอีกครั้งหรือดาวน์โหลดไฟล์แทน");
        }
    }

    async function reRender(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return;
        }

        for (const pageState of state.pages) {
            const viewport = pageState.page.getViewport({ scale: state.scale });

            pageState.canvas.style.width = `${Math.floor(viewport.width)}px`;
            pageState.canvas.style.height = `${Math.floor(viewport.height)}px`;

            pageState.canvas.width = Math.floor(viewport.width * pageState.dpr);
            pageState.canvas.height = Math.floor(viewport.height * pageState.dpr);

            await pageState.page.render({
                canvasContext: pageState.ctx,
                viewport,
                transform: pageState.dpr !== 1 ? [pageState.dpr, 0, 0, pageState.dpr, 0, 0] : null
            }).promise;
        }

        updateToolbarScale(containerId, state.scale);
    }

    function updateToolbarScale(containerId, scale) {
        const element = document.getElementById(`${containerId}-scale`);
        if (element) {
            element.textContent = `${Math.round(scale * 100)}%`;
        }
    }

    function getPageCount(containerId) {
        const state = views.get(containerId);
        if (!state || !state.pdf) {
            return 0;
        }

        return state.pdf.numPages || 0;
    }

    function zoomIn(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return;
        }
        state.scale *= 1.1;
        reRender(containerId);
    }

    function zoomOut(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return;
        }
        state.scale /= 1.1;
        reRender(containerId);
    }

    function fitWidth(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return;
        }
        state.scale = state.fitWidthScale || state.scale;
        reRender(containerId);
    }

    function goToPage(containerId, pageNumber, smooth) {
        const state = views.get(containerId);
        if (!state || !state.pages || state.pages.length === 0) {
            return;
        }

        const index = Math.max(1, Math.min(pageNumber, state.pages.length)) - 1;
        const canvas = state.pages[index] && state.pages[index].canvas;
        if (!canvas) {
            return;
        }

        const behavior = smooth === false ? "auto" : "smooth";
        canvas.scrollIntoView({ behavior, block: "center", inline: "nearest" });
    }

    function getCurrentPageIndex(containerId) {
        const state = views.get(containerId);
        if (!state || !state.pages || state.pages.length === 0) {
            return 0;
        }

        let bestIndex = 0;
        let bestDistance = Number.POSITIVE_INFINITY;
        const viewportCenter = window.innerHeight / 2;

        state.pages.forEach((pageState, index) => {
            const rect = pageState.canvas.getBoundingClientRect();
            const center = rect.top + rect.height / 2;
            const distance = Math.abs(center - viewportCenter);
            if (distance < bestDistance) {
                bestDistance = distance;
                bestIndex = index;
            }
        });

        return bestIndex + 1;
    }

    function initializeFullScreen(dotNetRef, hostId, viewerId) {
        ensureFullscreenEvents();
        fullscreenHosts.set(hostId, {
            dotNetRef,
            hostId,
            viewerId,
            keyHandler: null,
            isActive: false
        });
        viewerCallbacks.set(viewerId, dotNetRef);
    }

    async function requestFullScreenHost(hostId) {
        ensureFullscreenEvents();
        const host = document.getElementById(hostId);
        if (!host) {
            return;
        }

        try {
            await requestFullscreen(host);
        } catch (error) {
            console.warn("Unable to enter fullscreen", error);
            host.classList.remove("pdf-fullscreen-active");
        }
    }

    async function exitFullScreenHost(hostId) {
        const host = document.getElementById(hostId);
        const activeElement = getFullscreenElement();
        if (!activeElement) {
            if (host) {
                host.classList.remove("pdf-fullscreen-active");
            }
            const state = fullscreenHosts.get(hostId);
            if (state) {
                state.isActive = false;
                detachKeyHandler(state);
            }
            return;
        }

        if (!host || host !== activeElement) {
            await exitFullscreen();
            return;
        }

        await exitFullscreen();
    }

    function disposeFullScreenHost(hostId) {
        const state = fullscreenHosts.get(hostId);
        if (!state) {
            return;
        }

        detachKeyHandler(state);
        fullscreenHosts.delete(hostId);
        viewerCallbacks.delete(state.viewerId);
    }

    function ready() {
        ensureLoaded().catch(console.error);
        return readyPromise;
    }

    function getDotNetRefForViewer(containerId) {
        const callback = viewerCallbacks.get(containerId);
        if (callback) {
            return callback;
        }

        for (const state of fullscreenHosts.values()) {
            if (state.viewerId === containerId && state.dotNetRef) {
                return state.dotNetRef;
            }
        }

        return null;
    }

    function notifyRenderStatus(containerId, success, message) {
        const dotNetRef = getDotNetRefForViewer(containerId);
        if (!dotNetRef) {
            return;
        }

        try {
            dotNetRef.invokeMethodAsync("OnPdfRenderStatusChangedFromJsAsync", containerId, !!success, message || null)
                .catch(() => { });
        } catch (error) {
            // ignore
        }
    }

    function openStandalone(source, title) {
        if (!source) {
            console.warn("Unable to open standalone PDF viewer: missing source");
            return;
        }

        const viewerWindow = window.open("", "_blank", "noopener");
        if (!viewerWindow) {
            console.warn("Unable to open standalone PDF viewer window. Pop-up may be blocked.");
            return;
        }

        const safeTitle = escapeHtml(title || "PDF Viewer");
        const safeSource = sanitizeUrl(source);

        // Compose a lightweight fullscreen document with our floating toolbar controls.
        const html = `<!DOCTYPE html>
<html lang="th">
<head>
    <meta charset="utf-8" />
    <title>${safeTitle}</title>
    <style>
        html, body { margin: 0; height: 100%; background: #000; color: #fff; font-family: 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; }
        #pdfFullScreenShell { position: fixed; inset: 0; padding: 48px 24px 120px; background: #000; display: flex; justify-content: center; align-items: flex-start; overflow: auto; box-sizing: border-box; }
        #pdfScaler { transform-origin: top center; transition: transform 0.25s ease; }
        #pdfCanvas { display: block; width: min(85vw, 1100px); height: calc(100vh - 200px); max-height: 95vh; border: none; background: #1b1b1b; box-shadow: 0 16px 48px rgba(0, 0, 0, 0.6); border-radius: 8px; }
        #pdfFullToolbar { position: fixed; bottom: 32px; left: 50%; transform: translateX(-50%); display: flex; align-items: center; gap: 12px; padding: 12px 20px; border-radius: 999px; background: rgba(22, 22, 22, 0.9); box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45); backdrop-filter: blur(6px); }
        #pdfFullToolbar button { border: 1px solid rgba(255, 255, 255, 0.2); border-radius: 999px; padding: 10px 18px; font-size: 16px; line-height: 1; color: #fff; background: #2d2d2d; cursor: pointer; transition: background 0.2s ease, transform 0.2s ease; }
        #pdfFullToolbar button:hover { background: #3b3b3b; transform: translateY(-1px); }
        #pdfFullToolbar button:active { transform: translateY(0); }
        #pdfScaleLabel { font-size: 14px; color: #d5d5d5; min-width: 52px; text-align: center; font-variant-numeric: tabular-nums; }
        @media (max-width: 768px) {
            #pdfCanvas { width: 92vw; height: calc(100vh - 220px); }
            #pdfFullToolbar { bottom: 16px; flex-wrap: wrap; gap: 8px; }
            #pdfFullToolbar button { padding: 10px 14px; font-size: 15px; }
        }
    </style>
</head>
<body>
    <div id="pdfFullScreenShell">
        <div id="pdfScaler">
            <embed id="pdfCanvas" src="${safeSource}" type="application/pdf" />
        </div>
    </div>
    <!-- Floating toolbar that stays visible for fullscreen zoom controls -->
    <div id="pdfFullToolbar" role="toolbar" aria-label="PDF fullscreen controls">
        <button id="btnZoomOut" type="button" aria-label="Zoom out">−</button>
        <button id="btnZoomIn" type="button" aria-label="Zoom in">+</button>
        <button id="btnResetZoom" type="button" aria-label="Reset zoom">Reset</button>
        <span id="pdfScaleLabel">100%</span>
    </div>
    <script>
        (() => {
            const pdfScaler = document.getElementById('pdfScaler');
            const pdfCanvas = document.getElementById('pdfCanvas');
            const scaleLabel = document.getElementById('pdfScaleLabel');
            const zoomInButton = document.getElementById('btnZoomIn');
            const zoomOutButton = document.getElementById('btnZoomOut');
            const resetButton = document.getElementById('btnResetZoom');

            let currentScale = 1.0;
            const MIN_SCALE = 0.3;
            const MAX_SCALE = 3.0;
            const STEP = 0.1;

            function clamp(value) {
                return Math.min(MAX_SCALE, Math.max(MIN_SCALE, value));
            }

            function applyScale() {
                if (!pdfScaler) {
                    return;
                }

                currentScale = clamp(currentScale);

                // Apply zoom by scaling the wrapper so the embedded PDF grows/shrinks smoothly.
                pdfScaler.style.transform = 'scale(' + currentScale.toFixed(2) + ')';

                if (scaleLabel) {
                    scaleLabel.textContent = Math.round(currentScale * 100) + '%';
                }
            }

            function zoomIn() {
                currentScale = clamp(currentScale + STEP);
                applyScale();
            }

            function zoomOut() {
                currentScale = clamp(currentScale - STEP);
                applyScale();
            }

            function resetZoom() {
                currentScale = 1.0;
                applyScale();
            }

            if (zoomInButton) {
                // Toolbar button: zoom-in increases the scale in 10% increments.
                zoomInButton.addEventListener('click', zoomIn);
            }

            if (zoomOutButton) {
                // Toolbar button: zoom-out decreases the scale in 10% increments.
                zoomOutButton.addEventListener('click', zoomOut);
            }

            if (resetButton) {
                // Reset brings the scale back to the default fit-width view.
                resetButton.addEventListener('click', resetZoom);
            }

            window.addEventListener('keydown', event => {
                switch (event.key) {
                    case '+':
                    case '=':
                        zoomIn();
                        break;
                    case '-':
                    case '_':
                        zoomOut();
                        break;
                    case '0':
                        resetZoom();
                        break;
                }
            });

            applyScale();
            if (pdfCanvas && typeof pdfCanvas.focus === 'function') {
                pdfCanvas.focus();
            }
        })();
    </script>
</body>
</html>`;

        try {
            viewerWindow.document.open();
            viewerWindow.document.write(html);
            viewerWindow.document.close();
        } catch (error) {
            console.error("Unable to write standalone PDF viewer content", error);
        }

        try {
            viewerWindow.focus();
        } catch (error) {
            // Ignore focus errors.
        }
    }

    window.pdfViewer = {
        render,
        zoomIn,
        zoomOut,
        fitWidth,
        ready,
        renderPdf: render,
        getPageCount,
        goToPage,
        getCurrentPageIndex,
        initializeFullScreen,
        requestFullScreen: requestFullScreenHost,
        exitFullScreen: exitFullScreenHost,
        disposeFullScreen: disposeFullScreenHost,
        focusFullScreenHost,
        openStandalone
    };

    try {
        document.dispatchEvent(new CustomEvent("pdfViewer:initialized"));
    } catch (error) {
        // ignore if CustomEvent is not supported
    }
})();
