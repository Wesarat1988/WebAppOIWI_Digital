// wwwroot/js/pdfViewer.js
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";

    let loader; // promise โหลด pdf.js
    const views = new Map(); // เก็บ state ต่อ containerId
    const hostRegistrations = new Map();
    const viewerCallbacks = new Map();
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
            const resolvedUrl = toAbsoluteUrl(url);
            console.debug("pdfViewer.render -> loading", { containerId, url: resolvedUrl });
            const pdf = await window.pdfjsLib.getDocument({ url: resolvedUrl, withCredentials: true }).promise;
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
            console.error("PDF render error", {
                containerId,
                source: url,
                error
            });
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

    function getPageElements(scrollContainer, selector) {
        if (!scrollContainer || typeof scrollContainer.querySelectorAll !== "function") {
            return [];
        }

        if (!selector) {
            return Array.from(scrollContainer.children || []);
        }

        return Array.from(scrollContainer.querySelectorAll(selector));
    }

    function getVisiblePageIndexInContainer(scrollContainer, selector) {
        if (!scrollContainer) {
            return 0;
        }

        const pages = getPageElements(scrollContainer, selector);
        if (!pages.length) {
            return 0;
        }

        const containerRect = scrollContainer.getBoundingClientRect();
        let bestIndex = 0;
        let bestVisibility = -1;

        pages.forEach((page, index) => {
            const rect = page.getBoundingClientRect();
            const visibleTop = Math.max(rect.top, containerRect.top);
            const visibleBottom = Math.min(rect.bottom, containerRect.bottom);
            const visibleHeight = Math.max(0, visibleBottom - visibleTop);
            const visibilityRatio = rect.height > 0 ? visibleHeight / rect.height : 0;

            if (visibilityRatio > bestVisibility) {
                bestVisibility = visibilityRatio;
                bestIndex = index;
            }
        });

        return bestIndex + 1;
    }

    function scrollToPageInContainer(scrollContainer, selector, pageNumber, smooth) {
        if (!scrollContainer) {
            return;
        }

        const pages = getPageElements(scrollContainer, selector);
        if (!pages.length) {
            return;
        }

        const targetIndex = Math.min(Math.max((pageNumber || 1) - 1, 0), pages.length - 1);
        const target = pages[targetIndex];
        if (!target) {
            return;
        }

        const containerRect = scrollContainer.getBoundingClientRect();
        const targetRect = target.getBoundingClientRect();
        const offset = targetRect.top - containerRect.top + scrollContainer.scrollTop;

        const behavior = smooth === false ? "auto" : "smooth";
        scrollContainer.scrollTo({
            top: offset,
            behavior
        });
    }

    function initializeFullScreen(dotNetRef, hostId, viewerId) {
        if (!viewerId || !dotNetRef) {
            return;
        }

        viewerCallbacks.set(viewerId, dotNetRef);

        if (hostId) {
            hostRegistrations.set(hostId, { dotNetRef, viewerId });
        }
    }

    function requestFullScreenHost(hostId) {
        const host = hostId ? document.getElementById(hostId) : null;
        if (host) {
            host.classList.add("pdf-fullscreen-overlay");
        }

        return Promise.resolve();
    }

    function exitFullScreenHost(hostId) {
        const host = hostId ? document.getElementById(hostId) : null;
        if (host) {
            host.classList.remove("pdf-fullscreen-overlay");
        }

        return Promise.resolve();
    }

    function disposeFullScreenHost(hostId) {
        if (!hostId) {
            return;
        }

        const registration = hostRegistrations.get(hostId);
        if (registration) {
            viewerCallbacks.delete(registration.viewerId);
            hostRegistrations.delete(hostId);
        }
    }

    function focusFullScreenHost(hostId) {
        const host = hostId ? document.getElementById(hostId) : null;
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

    function ready() {
        ensureLoaded().catch(console.error);
        return readyPromise;
    }

    function getDotNetRefForViewer(containerId) {
        const callback = viewerCallbacks.get(containerId);
        if (callback) {
            return callback;
        }

        for (const registration of hostRegistrations.values()) {
            if (registration.viewerId === containerId && registration.dotNetRef) {
                return registration.dotNetRef;
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

    function toAbsoluteUrl(value) {
        if (!value) {
            return "";
        }

        try {
            return new URL(value, window.location.origin).href;
        } catch (error) {
            return value;
        }
    }

    function openStandalone(source, title) {
        // Legacy hook retained so existing interop calls succeed. The fullscreen experience
        // now lives inside the same tab, so we simply log for debugging instead of opening popups.
        console.warn("Standalone PDF popups have been replaced by the in-page fullscreen overlay.", {
            source,
            title
        });
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
        getVisiblePageIndexInContainer,
        scrollToPageInContainer,
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
