// wwwroot/js/pdfViewer.js
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";

    let loader; // promise โหลด pdf.js
    const views = new Map(); // เก็บ state ต่อ containerId
    let isReadyResolve;
    const readyPromise = new Promise(resolve => {
        isReadyResolve = resolve;
    });

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
        } catch (error) {
            console.error("PDF render error", error);
            host.innerHTML = '<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถโหลดตัวอย่างไฟล์ PDF ได้</div>';
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

    async function renderPageToCanvas(containerId, pageNumber, canvasId) {
        await ready();

        const state = views.get(containerId);
        if (!state || !state.pdf) {
            return;
        }

        const target = typeof canvasId === "string" ? document.getElementById(canvasId) : canvasId;
        if (!target) {
            return;
        }

        const pageIndex = Math.max(1, Math.min(pageNumber, state.pdf.numPages));
        const stored = state.pages[pageIndex - 1];
        const page = stored ? stored.page : await state.pdf.getPage(pageIndex);
        const dpr = window.devicePixelRatio || 1;
        const viewport = page.getViewport({ scale: state.scale || 1 });
        const context = target.getContext("2d", { alpha: false });

        target.width = Math.floor(viewport.width * dpr);
        target.height = Math.floor(viewport.height * dpr);
        target.style.width = `${viewport.width}px`;
        target.style.height = `${viewport.height}px`;

        const transform = dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : null;

        await page.render({ canvasContext: context, viewport, transform }).promise;
    }

    function focusElement(element) {
        if (element && typeof element.focus === "function") {
            element.focus();
        }
    }

    function ready() {
        ensureLoaded().catch(console.error);
        return readyPromise;
    }

    window.pdfViewer = {
        render,
        zoomIn,
        zoomOut,
        fitWidth,
        ready,
        renderPdf: render,
        getPageCount,
        renderPageToCanvas,
        focusElement
    };
})();
