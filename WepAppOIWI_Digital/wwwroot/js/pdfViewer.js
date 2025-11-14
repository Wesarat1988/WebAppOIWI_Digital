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

        const html = `<!DOCTYPE html>
<html lang="th">
<head>
    <meta charset="utf-8" />
    <title>${safeTitle}</title>
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <style>
        :root { color-scheme: only dark; }
        *, *::before, *::after { box-sizing: border-box; }
        html, body { height: 100%; margin: 0; padding: 0; font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #111; color: #f5f5f5; }
        body { display: flex; }
        #pdfContainer { flex: 1 1 auto; display: flex; justify-content: center; align-items: flex-start; padding: 24px 16px 96px; overflow: auto; }
        #pdfCanvas { border: none; box-shadow: 0 18px 48px rgba(0, 0, 0, 0.6); transform-origin: top center; transition: transform 0.16s ease-out; max-width: 100%; height: auto; }
        #pdfToolbar { position: fixed; bottom: 24px; left: 50%; transform: translateX(-50%); display: inline-flex; gap: 8px; align-items: center; padding: 10px 16px; border-radius: 999px; background: rgba(0, 0, 0, 0.75); backdrop-filter: blur(10px); box-shadow: 0 12px 32px rgba(0,0,0,0.45); }
        #pdfToolbar button { border: none; border-radius: 999px; background: #fff; color: #111; font-size: 15px; padding: 8px 14px; cursor: pointer; min-width: 40px; }
        #pdfToolbar button:hover { background: #e5e5e5; }
        #pdfToolbar .label { color: #fff; font-size: 13px; margin-right: 4px; }
        #pdfScaleValue { min-width: 48px; text-align: right; color: #fff; font-variant-numeric: tabular-nums; font-size: 13px; }
        @media (max-width: 640px) {
            #pdfToolbar { bottom: 16px; flex-wrap: wrap; justify-content: center; }
            #pdfToolbar button { flex: 1 1 30%; }
        }
    </style>
</head>
<body>
    <div id="pdfContainer">
        <embed id="pdfCanvas" src="${safeSource}" type="application/pdf" />
    </div>
    <div id="pdfToolbar" role="toolbar" aria-label="PDF fullscreen controls">
        <span class="label">Zoom</span>
        <button id="btnZoomOut" type="button" aria-label="Zoom out">−</button>
        <button id="btnZoomIn" type="button" aria-label="Zoom in">+</button>
        <button id="btnFitWidth" type="button" aria-label="Fit width">Fit width</button>
        <span id="pdfScaleValue">100%</span>
    </div>
    <script>
        (() => {
            const pdf = document.getElementById('pdfCanvas');
            const container = document.getElementById('pdfContainer');
            const zoomInBtn = document.getElementById('btnZoomIn');
            const zoomOutBtn = document.getElementById('btnZoomOut');
            const fitWidthBtn = document.getElementById('btnFitWidth');
            const scaleValue = document.getElementById('pdfScaleValue');

            let currentScale = 1.0;
            const MIN_SCALE = 0.3;
            const MAX_SCALE = 3.0;
            const STEP = 0.1;

            function clamp(scale) {
                return Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));
            }

            function applyScale() {
                if (!pdf) {
                    return;
                }

                currentScale = clamp(currentScale);

                // Scale only the PDF embed inside the new window so the main site is unaffected.
                pdf.style.transform = 'scale(' + currentScale.toFixed(2) + ')';

                if (scaleValue) {
                    scaleValue.textContent = Math.round(currentScale * 100) + '%';
                }
            }

            function zoomIn() {
                currentScale += STEP;
                applyScale();
            }

            function zoomOut() {
                currentScale -= STEP;
                applyScale();
            }

            function fitWidth() {
                if (!pdf || !container) {
                    return;
                }

                // Reset scale to measure the intrinsic width of the PDF frame.
                pdf.style.transform = 'scale(1)';
                currentScale = 1.0;

                const containerWidth = container.clientWidth;
                const pdfWidth = pdf.getBoundingClientRect().width;

                if (containerWidth > 0 && pdfWidth > 0) {
                    // Calculate the scale that fills the available width while respecting limits.
                    currentScale = clamp(containerWidth / pdfWidth);
                }

                applyScale();
            }

            if (zoomInBtn) {
                zoomInBtn.addEventListener('click', zoomIn);
            }

            if (zoomOutBtn) {
                zoomOutBtn.addEventListener('click', zoomOut);
            }

            if (fitWidthBtn) {
                // Fit width scales the PDF to match the viewer container width.
                fitWidthBtn.addEventListener('click', fitWidth);
            }

            applyScale();

            if (pdf && typeof pdf.focus === 'function') {
                pdf.focus();
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
