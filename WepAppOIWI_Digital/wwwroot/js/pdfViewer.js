// wwwroot/js/pdfViewer.js
// GOD MODE: ป้องกัน canvas ดัน Sidebar แบบ 100% พร้อม maintain logic เดิม
(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";
    let loaderPromise;
    const views = new Map();

    // ====== 🔥 GOD MODE: Lock Sidebar Function ======
    function lockSidebarGPU() {
        const sidebar = document.querySelector('.shell__sidebar');
        if (!sidebar) return;

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
            return Promise.resolve(window.pdfjsLib);
        }
        if (!loaderPromise) {
            loaderPromise = new Promise((resolve, reject) => {
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
        return loaderPromise;
    }

    async function ready() {
        await ensureLoaded();
        // 🔥 Lock sidebar ทันทีที่ ready
        lockSidebarGPU();
    }

    // ====== utils (เดิม) ======
    function computeFitWidthScale(page, containerWidth) {
        const vp = page.getViewport({ scale: 1 });
        const w = Math.max(containerWidth || vp.width, 300);
        return w / vp.width;
    }

    async function fetchPdfArrayBuffer(url) {
        const res = await fetch(url, { credentials: "include" });
        if (!res.ok) throw new Error(`fetch pdf failed: ${res.status} ${res.statusText}`);
        return await res.arrayBuffer();
    }

    // ====== 🔥 render หลัก (เพิ่ม GOD MODE protection) ======
    async function render(url, containerId) {
        await ready();

        // 🔥 Lock sidebar ก่อนทำอะไร
        lockSidebarGPU();

        const host = document.getElementById(containerId);
        if (!host) return;

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
            const pdf = await window.pdfjsLib.getDocument({ data: buffer }).promise;
            const state = { pdf, scale: 1, fitWidthScale: 1, pages: [], containerId };
            views.set(containerId, state);

            host.innerHTML = "";

            // 🔥 Lock อีกครั้งหลัง clear
            lockContainer(host);

            if (!host.clientWidth) host.style.width = "100%";

            // 🔥 ใช้ requestAnimationFrame เพื่อ sync กับ browser paint cycle
            await new Promise(resolve => requestAnimationFrame(resolve));

            for (let i = 1; i <= pdf.numPages; i++) {
                const page = await pdf.getPage(i);
                if (i === 1) {
                    const fit = computeFitWidthScale(page, host.clientWidth);
                    state.fitWidthScale = fit;
                    state.scale = fit;
                }
                await renderPage(host, state, page);

                // 🔥 Lock sidebar หลัง render แต่ละหน้า
                lockSidebarGPU();
            }

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

            // 🔥 Final lock
            lockSidebarGPU();

        } catch (e) {
            console.error("PDF render error:", e);
            host.innerHTML = `<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถเปิดเอกสาร PDF ได้: ${e.message}</div>`;
        }
    }

    // ====== 🔥 render หน้าเดียว (เพิ่ม GOD MODE) ======
    async function renderPage(host, state, page) {
        const dpr = window.devicePixelRatio || 1;
        const vp = page.getViewport({ scale: state.scale });
        const canvas = document.createElement("canvas");
        canvas.className = "pdfjs-page-canvas";

        // 🔥 Lock canvas ก่อน append (ป้องกันดัน sidebar)
        lockCanvas(canvas, vp.width, vp.height);

        // Set actual pixel dimensions
        canvas.width = Math.floor(vp.width * dpr);
        canvas.height = Math.floor(vp.height * dpr);

        const ctx = canvas.getContext("2d", {
            alpha: false, // ป้องกัน alpha blending
            willReadFrequently: false
        });

        // 🔥 Force layout ก่อน append
        canvas.offsetHeight;

        // 🔥 Append โดยใช้ requestAnimationFrame (sync กับ browser paint)
        await new Promise(resolve => {
            requestAnimationFrame(() => {
                host.appendChild(canvas);
                // Force reflow
                canvas.offsetHeight;
                resolve();
            });
        });

        // Render (เดิม)
        await page.render({
            canvasContext: ctx,
            viewport: vp,
            transform: dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : null,
            background: 'white' // ป้องกัน transparent bleeding
        }).promise;

        state.pages.push({ canvas, ctx, page, dpr });

        // 🔥 Force reflow หลัง render + Lock sidebar
        canvas.offsetHeight;
        lockSidebarGPU();
    }

    // ====== 🔥 re-render เมื่อ zoom (เพิ่ม GOD MODE) ======
    async function reRender(containerId) {
        const st = views.get(containerId);
        if (!st) return;

        // 🔥 Lock sidebar ก่อน re-render
        lockSidebarGPU();

        // 🔥 Batch DOM updates ด้วย requestAnimationFrame
        await new Promise(resolve => requestAnimationFrame(resolve));

        for (const p of st.pages) {
            const vp = p.page.getViewport({ scale: st.scale });

            // 🔥 Lock canvas ใหม่ทุกครั้ง
            lockCanvas(p.canvas, vp.width, vp.height);

            p.canvas.width = Math.floor(vp.width * p.dpr);
            p.canvas.height = Math.floor(vp.height * p.dpr);

            // 🔥 Force layout
            p.canvas.offsetHeight;

            await p.page.render({
                canvasContext: p.ctx,
                viewport: vp,
                transform: p.dpr !== 1 ? [p.dpr, 0, 0, p.dpr, 0, 0] : null,
                background: 'white'
            }).promise;

            // 🔥 Force reflow หลัง render
            p.canvas.offsetHeight;
        }

        // 🔥 Lock sidebar หลัง re-render เสร็จ
        lockSidebarGPU();
    }

    // ====== API (เดิม + เพิ่ม lock) ======
    async function zoomIn(id) {
        lockSidebarGPU(); // 🔥 Lock ก่อน zoom
        const st = views.get(id);
        if (!st) return 100;
        st.scale = Math.min(st.scale * 1.1, 4);
        await reRender(id);
        lockSidebarGPU(); // 🔥 Lock หลัง zoom
        return Math.round(st.scale * 100);
    }

    async function zoomOut(id) {
        lockSidebarGPU(); // 🔥 Lock ก่อน zoom
        const st = views.get(id);
        if (!st) return 100;
        st.scale = Math.max(st.scale / 1.1, 0.25);
        await reRender(id);
        lockSidebarGPU(); // 🔥 Lock หลัง zoom
        return Math.round(st.scale * 100);
    }

    async function fitWidth(id) {
        lockSidebarGPU(); // 🔥 Lock ก่อน fit
        const st = views.get(id);
        if (!st) return 100;
        const host = document.getElementById(id);
        if (!host || !st.pdf) return Math.round(st.scale * 100);
        const p = await st.pdf.getPage(1);
        st.fitWidthScale = computeFitWidthScale(p, host.clientWidth);
        st.scale = st.fitWidthScale;
        await reRender(id);
        lockSidebarGPU(); // 🔥 Lock หลัง fit
        return Math.round(st.scale * 100);
    }

    function getZoomPercent(id) {
        const st = views.get(id);
        return st ? Math.round(st.scale * 100) : 100;
    }

    // ====== Export API ======
    window.pdfViewer = {
        ready,
        render,
        renderPdf: render,
        zoomIn,
        zoomOut,
        fitWidth,
        getZoomPercent
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