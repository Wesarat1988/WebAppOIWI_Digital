// wwwroot/js/pdfViewer.js
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";

    const views = new Map();
    let loaderPromise;
    let readyResolve;
    const readyPromise = new Promise(resolve => {
        readyResolve = resolve;
    });

    function ensureLoaded() {
        if (window.pdfjsLib) {
            window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
            if (readyResolve) {
                readyResolve();
                readyResolve = null;
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
                    if (readyResolve) {
                        readyResolve();
                        readyResolve = null;
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

    function cleanupView(containerId) {
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

    function computeFitWidthScale(page, state) {
        const viewport = page.getViewport({ scale: 1 });
        const containerWidth = state.host.clientWidth || (state.scrollEl ? state.scrollEl.clientWidth : viewport.width);
        const width = containerWidth > 0 ? containerWidth : viewport.width;
        return width / viewport.width;
    }

    function updateScaleIndicator(state) {
        const element = document.getElementById(`${state.containerId}-scale`);
        if (element) {
            element.textContent = `${Math.round(state.scale * 100)}%`;
        }
    }

    function updatePageIndicator(state) {
        const element = document.getElementById(`${state.containerId}-page`);
        if (element) {
            element.textContent = `${state.currentPage} / ${state.pageCount}`;
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

    async function renderCurrentPage(state) {
        if (!state.pdf) {
            return;
        }
        const page = await state.pdf.getPage(state.currentPage);
        const viewport = page.getViewport({ scale: state.scale });
        const dpr = window.devicePixelRatio || 1;

        state.canvas.width = Math.floor(viewport.width * dpr);
        state.canvas.height = Math.floor(viewport.height * dpr);
        state.canvas.style.width = `${Math.floor(viewport.width)}px`;
        state.canvas.style.height = `${Math.floor(viewport.height)}px`;

        const ctx = state.ctx;
        if (!ctx) {
            return;
        }
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, state.canvas.width, state.canvas.height);

        const renderContext = {
            canvasContext: ctx,
            viewport,
            transform: dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : null
        };

        await page.render(renderContext).promise;

        if (state.scrollEl) {
            state.scrollEl.scrollTop = 0;
        }

        updateScaleIndicator(state);
        updatePageIndicator(state);
    }

    function goToPageInternal(state, pageNumber) {
        if (!state.pdf) {
            return Promise.resolve(false);
        }
        const next = Math.max(1, Math.min(pageNumber, state.pageCount));
        if (next === state.currentPage) {
            return Promise.resolve(false);
        }
        state.currentPage = next;
        return renderCurrentPage(state).then(() => true).catch(() => false);
    }

    function attachInteraction(state) {
        const wheelHandler = (event) => {
            if (!state.scrollEl) {
                return;
            }

            const delta = event.deltaY;
            const el = state.scrollEl;
            if (delta > 0) {
                if (el.scrollTop + el.clientHeight >= el.scrollHeight - 1 && state.currentPage < state.pageCount) {
                    event.preventDefault();
                    goToPageInternal(state, state.currentPage + 1);
                }
            } else if (delta < 0) {
                if (el.scrollTop <= 0 && state.currentPage > 1) {
                    event.preventDefault();
                    goToPageInternal(state, state.currentPage - 1);
                }
            }
        };

        const keyHandler = (event) => {
            switch (event.key) {
                case "ArrowDown":
                case "PageDown":
                    event.preventDefault();
                    goToPageInternal(state, state.currentPage + 1);
                    break;
                case "ArrowUp":
                case "PageUp":
                    event.preventDefault();
                    goToPageInternal(state, state.currentPage - 1);
                    break;
                case "Home":
                    event.preventDefault();
                    goToPageInternal(state, 1);
                    break;
                case "End":
                    event.preventDefault();
                    goToPageInternal(state, state.pageCount);
                    break;
                default:
                    break;
            }
        };

        if (state.scrollEl) {
            state.scrollEl.addEventListener("wheel", wheelHandler, { passive: false });
            state.scrollEl.addEventListener("keydown", keyHandler);
        }

        state.wheelHandler = wheelHandler;
        state.keyHandler = keyHandler;
    }

    async function render(url, containerId) {
        await ready();
        const host = document.getElementById(containerId);
        if (!host) {
            return;
        }

        cleanupView(containerId);

        const scrollEl = host.closest(".pdf-scroll") || host;
        setScrollFocus(scrollEl);

        host.innerHTML = "";
        const canvas = document.createElement("canvas");
        canvas.className = "pdfjs-canvas";
        host.appendChild(canvas);
        const ctx = canvas.getContext("2d", { alpha: false });

        const state = {
            containerId,
            host,
            scrollEl,
            canvas,
            ctx,
            pdf: null,
            pageCount: 0,
            currentPage: 1,
            scale: 1,
            fitWidthScale: 1,
            isFitWidth: true,
            resizeObserver: null,
            wheelHandler: null,
            keyHandler: null
        };

        views.set(containerId, state);

        let pdf;
        try {
            const loadingTask = window.pdfjsLib.getDocument({ url, withCredentials: true });
            pdf = await loadingTask.promise;
        } catch (error) {
            console.error("Failed to load PDF", error);
            host.innerHTML = '<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถโหลดตัวอย่างไฟล์ PDF ได้</div>';
            views.delete(containerId);
            return;
        }

        state.pdf = pdf;
        state.pageCount = pdf.numPages;
        updatePageIndicator(state);

        try {
            const firstPage = await pdf.getPage(1);
            state.fitWidthScale = computeFitWidthScale(firstPage, state);
            state.scale = state.fitWidthScale;
            state.isFitWidth = true;
            await renderCurrentPage(state);
        } catch (error) {
            console.error("Failed to render initial PDF page", error);
            host.innerHTML = '<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถแสดงตัวอย่างไฟล์ PDF ได้</div>';
            views.delete(containerId);
            return;
        }

        const ro = new ResizeObserver(() => {
            if (!state.pdf) {
                return;
            }
            const wasFit = state.isFitWidth || Math.abs(state.scale - state.fitWidthScale) < 0.001;
            state.pdf.getPage(1).then(page => {
                state.fitWidthScale = computeFitWidthScale(page, state);
                if (wasFit) {
                    state.scale = state.fitWidthScale;
                    state.isFitWidth = true;
                    renderCurrentPage(state);
                }
            }).catch(() => { /* ignore */ });
        });
        ro.observe(state.host);
        state.resizeObserver = ro;

        attachInteraction(state);
    }

    async function zoomIn(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return 100;
        }
        state.isFitWidth = false;
        state.scale *= 1.1;
        await renderCurrentPage(state);
        return Math.round(state.scale * 100);
    }

    async function zoomOut(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return 100;
        }
        state.isFitWidth = false;
        state.scale /= 1.1;
        await renderCurrentPage(state);
        return Math.round(state.scale * 100);
    }

    async function fitWidth(containerId) {
        const state = views.get(containerId);
        if (!state || !state.pdf) {
            return 100;
        }
        try {
            const page = await state.pdf.getPage(state.currentPage);
            state.fitWidthScale = computeFitWidthScale(page, state);
        } catch {
            // ignore errors computing fit width
        }
        state.scale = state.fitWidthScale;
        state.isFitWidth = true;
        await renderCurrentPage(state);
        return Math.round(state.scale * 100);
    }

    async function nextPage(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return { page: 1, pageCount: 1 };
        }
        await goToPageInternal(state, state.currentPage + 1);
        return { page: state.currentPage, pageCount: state.pageCount };
    }

    async function prevPage(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return { page: 1, pageCount: 1 };
        }
        await goToPageInternal(state, state.currentPage - 1);
        return { page: state.currentPage, pageCount: state.pageCount };
    }

    async function goToPage(containerId, pageNumber) {
        const state = views.get(containerId);
        if (!state) {
            return { page: 1, pageCount: 1 };
        }
        await goToPageInternal(state, pageNumber);
        return { page: state.currentPage, pageCount: state.pageCount };
    }

    function getZoomPercent(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return 100;
        }
        return Math.round(state.scale * 100);
    }

    function getPageInfo(containerId) {
        const state = views.get(containerId);
        if (!state) {
            return { page: 1, pageCount: 1 };
        }
        return { page: state.currentPage, pageCount: state.pageCount };
    }

    window.pdfViewer = {
        ready,
        render,
        renderPdf: render,
        zoomIn,
        zoomOut,
        fitWidth,
        nextPage,
        prevPage,
        goToPage,
        getZoomPercent,
        getPageInfo
    };
})();
