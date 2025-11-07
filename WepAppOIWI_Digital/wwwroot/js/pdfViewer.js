// wwwroot/js/pdfViewer.js
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";

    let loader;                   // promise โหลด pdf.js
    const views = new Map();      // เก็บ state ต่อ containerId

    function ensureLoaded() {
        if (window.pdfjsLib) {
            window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
            return Promise.resolve(window.pdfjsLib);
        }
        if (!loader) {
            loader = new Promise((resolve, reject) => {
                const s = document.createElement("script");
                s.src = PDF_JS_CDN;
                s.async = true;
                s.onload = () => {
                    if (!window.pdfjsLib) return reject(new Error("pdf.js not found"));
                    window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
                    resolve(window.pdfjsLib);
                };
                s.onerror = () => reject(new Error("failed to load pdf.js"));
                document.head.appendChild(s);
            });
        }
        return loader;
    }

    function computeFitWidthScale(page, containerWidth) {
        const v = page.getViewport({ scale: 1 });
        const w = containerWidth || v.width;
        return w / v.width;
    }

    async function render(url, containerId) {
        const host = document.getElementById(containerId);
        if (!host) return;

        host.innerHTML = '<div class="pdfjs-loading text-muted text-center p-4">กำลังโหลดตัวอย่างเอกสาร PDF...</div>';
        host.style.position = "relative";

        try {
            const pdfjsLib = await ensureLoaded();
            const pdf = await pdfjsLib.getDocument({ url, withCredentials: true }).promise;

            // state ต่อคอนเทนเนอร์
            const state = {
                pdf,
                scale: 1,
                fitWidthScale: 1,
                pages: [],     // {canvas, ctx, page, outputScale}
                containerId
            };
            views.set(containerId, state);

            host.innerHTML = "";

            // วาดทีละหน้า
            for (let i = 1; i <= pdf.numPages; i++) {
                const page = await pdf.getPage(i);

                // fit กว้างเท่าคอนเทนเนอร์
                const fit = computeFitWidthScale(page, host.clientWidth);
                if (i === 1) {
                    state.fitWidthScale = fit;
                    state.scale = fit; // เริ่มต้นพอดีกว้าง
                    updateToolbarScale(containerId, state.scale);
                }

                const viewport = page.getViewport({ scale: state.scale });
                const outputScale = window.devicePixelRatio || 1;

                const canvas = document.createElement("canvas");
                canvas.className = "pdfjs-page-canvas";
                canvas.style.display = "block";
                canvas.style.margin = "0 auto 16px";
                canvas.style.width = `${Math.floor(viewport.width)}px`;
                canvas.style.height = `${Math.floor(viewport.height)}px`;

                // ขนาดจริงบนแคนวาส (คูณ DPI)
                canvas.width = Math.floor(viewport.width * outputScale);
                canvas.height = Math.floor(viewport.height * outputScale);

                const ctx = canvas.getContext("2d", { alpha: false });
                host.appendChild(canvas);

                await page.render({
                    canvasContext: ctx,
                    viewport,
                    transform: outputScale !== 1 ? [outputScale, 0, 0, outputScale, 0, 0] : null
                }).promise;

                // ไม่สร้าง textLayer เพื่อไม่ให้ทับแคนวาส
                state.pages.push({ canvas, ctx, page, outputScale });
            }
        } catch (err) {
            console.error(err);
            host.innerHTML = '<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถโหลดตัวอย่างไฟล์ PDF ได้</div>';
        }
    }

    // re-render ทุกหน้าเมื่อสเกลเปลี่ยน
    async function reRender(containerId) {
        const state = views.get(containerId);
        if (!state) return;

        for (const p of state.pages) {
            const viewport = p.page.getViewport({ scale: state.scale });

            p.canvas.style.width = `${Math.floor(viewport.width)}px`;
            p.canvas.style.height = `${Math.floor(viewport.height)}px`;

            p.canvas.width = Math.floor(viewport.width * p.outputScale);
            p.canvas.height = Math.floor(viewport.height * p.outputScale);

            await p.page.render({
                canvasContext: p.ctx,
                viewport,
                transform: p.outputScale !== 1 ? [p.outputScale, 0, 0, p.outputScale, 0, 0] : null
            }).promise;
        }
        updateToolbarScale(containerId, state.scale);
    }

    function updateToolbarScale(containerId, scale) {
        const el = document.getElementById(`${containerId}-scale`);
        if (el) el.textContent = `${Math.round(scale * 100)}%`;
    }

    // ===== public zoom controls =====
    function zoomIn(containerId) {
        const st = views.get(containerId);
        if (!st) return;
        st.scale = st.scale * 1.1;
        reRender(containerId);
    }

    function zoomOut(containerId) {
        const st = views.get(containerId);
        if (!st) return;
        st.scale = st.scale / 1.1;
        reRender(containerId);
    }

    function fitWidth(containerId) {
        const st = views.get(containerId);
        if (!st) return;
        st.scale = st.fitWidthScale || st.scale;
        reRender(containerId);
    }

    // ให้ทั้งชื่อใหม่และชื่อเก่า (กันโค้ดเก่าที่เรียก renderPdf)
    window.pdfViewer = {
        render,
        zoomIn,
        zoomOut,
        fitWidth,
        renderPdf: render
    };
})();
