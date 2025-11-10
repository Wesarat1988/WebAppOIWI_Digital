// wwwroot/js/pdfViewer.js
// GOD MODE: ป้องกัน canvas ดัน Sidebar แบบ 100% พร้อม maintain logic เดิม
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";
    let loaderPromise;
    const views = new Map();

    let loader; // promise โหลด pdf.js
    const views = new Map(); // เก็บ state ต่อ containerId
    let isReadyResolve;
    const readyPromise = new Promise(resolve => {
        isReadyResolve = resolve;
    });

        // บังคับ GPU layer + ป้องกันทุกอย่างที่จะดัน layout
        const criticalLock = [
            'position: fixed !important',
            'transform: translate3d(0, 0, 0) !important',
            'will-change: transform !important',
            'contain: strict !important',
            'backface-visibility: hidden !important',
            '-webkit-backface-visibility: hidden !important',
            'perspective: 1000px !important',
            'isolation: isolate !important'
        ];

        sidebar.style.cssText += '; ' + criticalLock.join('; ');
    }

    // ====== 🔥 GOD MODE: Lock Container ======
    function lockContainer(host) {
        if (!host) return;

        const containerLock = [
            'position: static !important',
            'contain: strict !important',
            'transform: translateZ(0) !important',
            'isolation: isolate !important',
            'overflow: visible !important',
            'will-change: auto !important'
        ];

        host.style.cssText += '; ' + containerLock.join('; ');

        // Force layout
        host.offsetHeight;
    }

    // ====== 🔥 GOD MODE: Lock Canvas (เพิ่ม requestAnimationFrame) ======
    function lockCanvas(canvas, width, height) {
        if (!canvas) return;

        const canvasLock = [
            'display: block !important',
            'margin: 0 auto 16px auto !important',
            'position: static !important', // ห้ามใช้ relative/absolute/fixed
            `width: ${Math.floor(width)}px !important`,
            `height: ${Math.floor(height)}px !important`,
            'transform: translateZ(0) !important',
            'backface-visibility: hidden !important',
            '-webkit-backface-visibility: hidden !important',
            'will-change: transform !important',
            'contain: strict !important',
            'isolation: isolate !important',
            'image-rendering: -webkit-optimize-contrast !important',
            'image-rendering: crisp-edges !important'
        ];

        canvas.style.cssText = canvasLock.join('; ');

        // Force reflow
        canvas.offsetHeight;
    }

    // ====== โหลด pdf.js (เดิม) ======
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
        return loaderPromise;
    }

    async function ready() {
        await ensureLoaded();
        // 🔥 Lock sidebar ทันทีที่ ready
        lockSidebarGPU();
    }

    // preload
    ensureLoaded().catch(console.error);

    function computeFitWidthScale(page, containerWidth) {
        const viewport = page.getViewport({ scale: 1 });
        const width = containerWidth || viewport.width;
        return width / viewport.width;
    }

    async function fetchPdfArrayBuffer(url) {
        const res = await fetch(url, { credentials: "include" });
        if (!res.ok) throw new Error(`fetch pdf failed: ${res.status} ${res.statusText}`);
        return await res.arrayBuffer();
    }

    // ====== 🔥 render หลัก (เพิ่ม GOD MODE protection) ======
    async function render(url, containerId) {
        await ready();
        const host = document.getElementById(containerId);
        if (!host) {
            return;
        }

        host.innerHTML = '<div class="pdfjs-loading text-muted text-center p-4">กำลังโหลดตัวอย่างเอกสาร PDF...</div>';

        // 🔥 Lock container ทันที
        lockContainer(host);

        let buffer;
        try {
            buffer = await fetchPdfArrayBuffer(url);
        } catch (e) {
            console.error("Fetch PDF error:", e);
            host.innerHTML = `<div class="pdfjs-error alert alert-danger m-3">โหลดไฟล์ไม่ได้: ${e.message}</div>`;
            return;
        }

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
                await renderPage(host, state, page);

                const viewport = page.getViewport({ scale: state.scale });
                const dpr = window.devicePixelRatio || 1;

            // ResizeObserver สำหรับ Fit Width (เดิม)
            const ro = new ResizeObserver(() => {
                const st = views.get(containerId);
                if (!st) return;
                const wasFit = Math.abs(st.scale - st.fitWidthScale) < 0.001;
                st.pdf.getPage(1).then(p => {
                    st.fitWidthScale = computeFitWidthScale(p, host.clientWidth);
                    if (wasFit) {
                        st.scale = st.fitWidthScale;
                        reRender(containerId);
                    }
                });
            });
            ro.observe(host);
            state._ro = ro;

                canvas.width = Math.floor(viewport.width * dpr);
                canvas.height = Math.floor(viewport.height * dpr);

                const context = canvas.getContext("2d", { alpha: false });
                host.appendChild(canvas);
                // Force reflow
                canvas.offsetHeight;
                resolve();
            });
        });

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

            // 🔥 Force reflow หลัง render
            p.canvas.offsetHeight;
        }

        updateToolbarScale(containerId, state.scale);
    }

    function updateToolbarScale(containerId, scale) {
        const element = document.getElementById(`${containerId}-scale`);
        if (element) {
            element.textContent = `${Math.round(scale * 100)}%`;
        }
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

    function ready() {
        ensureLoaded().catch(console.error);
        return readyPromise;
    }

    window.pdfViewer = {
        ready,
        render,
        renderPdf: render,
        zoomIn,
        zoomOut,
        fitWidth,
        ready,
        renderPdf: render
    };

    // ====== 🔥 GOD MODE: Auto-lock on DOM events ======
    document.addEventListener('DOMContentLoaded', lockSidebarGPU);
    window.addEventListener('resize', () => requestAnimationFrame(lockSidebarGPU));

    // 🔥 Lock sidebar ทุกครั้งที่มี scroll event ใน content area
    const lockOnScroll = () => {
        const contentArea = document.querySelector('.content-area');
        const pdfScroll = document.querySelector('.pdf-scroll');

        [contentArea, pdfScroll].forEach(el => {
            if (!el) return;
            el.addEventListener('scroll', () => {
                requestAnimationFrame(lockSidebarGPU);
            }, { passive: true });
        });
    };

    // Lock on scroll หลัง DOM ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', lockOnScroll);
    } else {
        lockOnScroll();
    }
})();