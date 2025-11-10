// wwwroot/js/pdfViewer.js
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";
    const MIN_SCALE = 0.25;
    const MAX_SCALE = 5;

    const views = new Map();
    let loaderPromise;
    let readyResolver;
    const readyPromise = new Promise(resolve => {
        readyResolver = resolve;
    });

    function ensureLoaded() {
        if (window.pdfjsLib) {
            window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
            if (readyResolver) {
                readyResolver();
                readyResolver = null;
            }
            return Promise.resolve(window.pdfjsLib);
        }

        if (!loaderPromise) {
            loaderPromise = new Promise((resolve, reject) => {
                const script = document.createElement("script");
                script.src = PDF_JS_CDN;
                script.async = true;
                script.onload = () => {
                    if (!window.pdfjsLib) {
                        reject(new Error("pdf.js not found"));
                        return;
                    }
                    window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
                    if (readyResolver) {
                        readyResolver();
                        readyResolver = null;
                    }
                    resolve(window.pdfjsLib);
                };
                script.onerror = () => reject(new Error("failed to load pdf.js"));
                document.head.appendChild(script);
            });
        }

        return loaderPromise;
    }

    async function ready() {
        await ensureLoaded();
        return readyPromise;
    }

    ensureLoaded().catch(console.error);

    function cleanup(containerId) {
        const existing = views.get(containerId);
        if (!existing) {
            return;
        }

        if (existing.resizeObserver) {
            existing.resizeObserver.disconnect();
        }
        if (existing.scrollEl && existing.wheelHandler) {
            existing.scrollEl.removeEventListener("wheel", existing.wheelHandler);
        }
        if (existing.scrollEl && existing.keyHandler) {
            existing.scrollEl.removeEventListener("keydown", existing.keyHandler);
        }

        views.delete(containerId);
    }

    function getState(containerId) {
        const state = views.get(containerId);
        if (!state) {
            throw new Error(`PDF viewer for '${containerId}' is not ready`);
        }
        return state;
    }

    function buildInfo(state) {
        return {
            page: state.page,
            pages: state.pages,
            zoom: Math.round(state.scale * 100)
        };
    }

    function updateIndicators(state) {
        const pageEl = document.getElementById(`${state.containerId}-page`);
        if (pageEl) {
            pageEl.textContent = String(state.page);
        }
        const pagesEl = document.getElementById(`${state.containerId}-pages`);
        if (pagesEl) {
            pagesEl.textContent = String(state.pages);
        }
        const scaleEl = document.getElementById(`${state.containerId}-scale`);
        if (scaleEl) {
            scaleEl.textContent = `${Math.round(state.scale * 100)}%`;
        }
    }

    function setScrollFocus(scrollEl) {
        if (!scrollEl) {
            return;
        }
        if (scrollEl.tabIndex < 0) {
            scrollEl.tabIndex = 0;
        }
        try {
            scrollEl.focus({ preventScroll: true });
        } catch {
            // ignore focus errors
        }
    }

    function computeFitWidthScale(pdfPage, hostWidth) {
        const viewport = pdfPage.getViewport({ scale: 1 });
        const width = Math.max(hostWidth || 0, 1);
        return width / viewport.width;
    }

    async function renderCurrent(state, suppliedPage) {
        if (!state.pdf) {
            throw new Error("PDF document not loaded");
        }

        const page = suppliedPage ?? await state.pdf.getPage(state.page);
        const viewport = page.getViewport({ scale: state.scale });
        const dpr = window.devicePixelRatio || 1;
        state.dpr = dpr;

        state.canvas.width = Math.floor(viewport.width * dpr);
        state.canvas.height = Math.floor(viewport.height * dpr);
        state.canvas.style.width = `${viewport.width}px`;
        state.canvas.style.height = `${viewport.height}px`;

        const ctx = state.ctx;
        if (!ctx) {
            throw new Error("2D rendering context not available");
        }
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, state.canvas.width, state.canvas.height);

        const renderContext = {
            canvasContext: ctx,
            viewport
        };
        if (dpr !== 1) {
            renderContext.transform = [dpr, 0, 0, dpr, 0, 0];
        }

        await page.render(renderContext).promise;

        if (state.scrollEl && typeof state.scrollEl.scrollTop === "number") {
            state.scrollEl.scrollTop = 0;
        }

        updateIndicators(state);
        return buildInfo(state);
    }

    function attachInteraction(state) {
        const scrollEl = state.scrollEl;
        if (!scrollEl) {
            return;
        }

        const wheelHandler = (event) => {
            if (event.deltaY > 0) {
                if (scrollEl.scrollTop + scrollEl.clientHeight >= scrollEl.scrollHeight - 1 && state.page < state.pages) {
                    event.preventDefault();
                    gotoPage(state.containerId, state.page + 1).catch(console.error);
                }
            } else if (event.deltaY < 0) {
                if (scrollEl.scrollTop <= 0 && state.page > 1) {
                    event.preventDefault();
                    gotoPage(state.containerId, state.page - 1).catch(console.error);
                }
            }
        };

        const keyHandler = (event) => {
            switch (event.key) {
                case "ArrowDown":
                case "PageDown":
                    event.preventDefault();
                    gotoPage(state.containerId, state.page + 1).catch(console.error);
                    break;
                case "ArrowUp":
                case "PageUp":
                    event.preventDefault();
                    gotoPage(state.containerId, state.page - 1).catch(console.error);
                    break;
                case "Home":
                    event.preventDefault();
                    gotoPage(state.containerId, 1).catch(console.error);
                    break;
                case "End":
                    event.preventDefault();
                    gotoPage(state.containerId, state.pages).catch(console.error);
                    break;
                default:
                    break;
            }
        };

        scrollEl.addEventListener("wheel", wheelHandler, { passive: false });
        scrollEl.addEventListener("keydown", keyHandler);

        state.wheelHandler = wheelHandler;
        state.keyHandler = keyHandler;
        setScrollFocus(scrollEl);
    }

    function observeResize(state) {
        if (state.resizeObserver) {
            state.resizeObserver.disconnect();
        }
        const observer = new ResizeObserver(() => {
            if (!state.isFitWidth) {
                return;
            }
            queueMicrotask(() => {
                if (!views.has(state.containerId)) {
                    return;
                }
                recomputeFitWidth(state).catch(console.error);
            });
        });
        observer.observe(state.host);
        state.resizeObserver = observer;
    }

    async function recomputeFitWidth(state) {
        if (!state.pdf) {
            return buildInfo(state);
        }
        const page = await state.pdf.getPage(state.page);
        const hostWidth = state.host.clientWidth || (state.scrollEl ? state.scrollEl.clientWidth : 0);
        let nextScale = computeFitWidthScale(page, hostWidth);
        if (!isFinite(nextScale) || nextScale <= 0) {
            nextScale = state.scale;
        }
        state.scale = nextScale;
        state.fitWidthScale = nextScale;
        return renderCurrent(state, page);
    }

    async function render(url, containerId) {
        await ready();
        cleanup(containerId);

        const host = document.getElementById(containerId);
        if (!host) {
            throw new Error(`Container '${containerId}' not found`);
        }
        const scrollEl = host.closest('.pdf-scroll') || host.parentElement || host;

        host.innerHTML = "";
        const canvas = document.createElement('canvas');
        canvas.className = 'pdfjs-canvas';
        host.appendChild(canvas);
        const ctx = canvas.getContext('2d');
        if (!ctx) {
            throw new Error('Unable to create 2D context');
        }

        const response = await fetch(url, { credentials: 'include' });
        if (!response.ok) {
            throw new Error(`Failed to fetch PDF: ${response.status}`);
        }
        const data = await response.arrayBuffer();
        const task = window.pdfjsLib.getDocument({ data });
        const pdf = await task.promise;

        const firstPage = await pdf.getPage(1);
        const hostWidth = host.clientWidth || (scrollEl ? scrollEl.clientWidth : 0);
        let fitWidthScale = computeFitWidthScale(firstPage, hostWidth);
        if (!isFinite(fitWidthScale) || fitWidthScale <= 0) {
            fitWidthScale = 1;
        }

        const state = {
            containerId,
            host,
            scrollEl,
            pdf,
            page: 1,
            pages: pdf.numPages,
            scale: fitWidthScale,
            fitWidthScale,
            isFitWidth: true,
            canvas,
            ctx,
            resizeObserver: null,
            wheelHandler: null,
            keyHandler: null,
            dpr: window.devicePixelRatio || 1
        };

        views.set(containerId, state);
        observeResize(state);
        attachInteraction(state);

        const info = await renderCurrent(state, firstPage);
        return info;
    }

    async function gotoPage(containerId, pageNumber) {
        const state = getState(containerId);
        const target = Math.min(Math.max(1, Math.trunc(pageNumber || 1)), state.pages);
        if (target === state.page) {
            updateIndicators(state);
            return buildInfo(state);
        }
        state.page = target;
        if (state.isFitWidth) {
            const page = await state.pdf.getPage(state.page);
            const hostWidth = state.host.clientWidth || (state.scrollEl ? state.scrollEl.clientWidth : 0);
            let scale = computeFitWidthScale(page, hostWidth);
            if (!isFinite(scale) || scale <= 0) {
                scale = state.scale;
            }
            state.scale = scale;
            state.fitWidthScale = scale;
            return renderCurrent(state, page);
        }
        return renderCurrent(state);
    }

    function nextPage(containerId) {
        return gotoPage(containerId, getState(containerId).page + 1);
    }

    function prevPage(containerId) {
        return gotoPage(containerId, getState(containerId).page - 1);
    }

    async function zoomIn(containerId) {
        const state = getState(containerId);
        state.isFitWidth = false;
        state.scale = Math.min(state.scale * 1.1, MAX_SCALE);
        await renderCurrent(state);
        return Math.round(state.scale * 100);
    }

    async function zoomOut(containerId) {
        const state = getState(containerId);
        state.isFitWidth = false;
        state.scale = Math.max(state.scale / 1.1, MIN_SCALE);
        await renderCurrent(state);
        return Math.round(state.scale * 100);
    }

    async function fitWidth(containerId) {
        const state = getState(containerId);
        state.isFitWidth = true;
        const info = await recomputeFitWidth(state);
        return info.zoom;
    }

    function getPageInfo(containerId) {
        const state = getState(containerId);
        updateIndicators(state);
        return buildInfo(state);
    }

    function getZoomPercent(containerId) {
        const state = getState(containerId);
        return Math.round(state.scale * 100);
    }

    window.pdfViewer = {
        ready,
        render,
        renderPdf: render,
        getPageInfo,
        gotoPage,
        goToPage: gotoPage,
        nextPage,
        prevPage,
        zoomIn,
        zoomOut,
        fitWidth,
        getZoomPercent
    };
})();
