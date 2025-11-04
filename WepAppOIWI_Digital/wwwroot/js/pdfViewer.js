(function () {
    const PDF_JS_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js";
    const PDF_JS_WORKER_CDN = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js";

    let pdfJsLoaderPromise;

    function ensurePdfJsLoaded() {
        if (window.pdfjsLib) {
            window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
            return Promise.resolve(window.pdfjsLib);
        }

        if (!pdfJsLoaderPromise) {
            pdfJsLoaderPromise = new Promise((resolve, reject) => {
                const scriptTag = document.createElement("script");
                scriptTag.src = PDF_JS_CDN;
                scriptTag.async = true;
                scriptTag.onload = () => {
                    if (window.pdfjsLib) {
                        window.pdfjsLib.GlobalWorkerOptions.workerSrc = PDF_JS_WORKER_CDN;
                        resolve(window.pdfjsLib);
                    } else {
                        reject(new Error("ไม่สามารถโหลดไลบรารี pdf.js ได้"));
                    }
                };
                scriptTag.onerror = () => reject(new Error("โหลดไลบรารี pdf.js ไม่สำเร็จ"));
                document.head.appendChild(scriptTag);
            });
        }

        return pdfJsLoaderPromise;
    }

    async function renderPdf(url, containerId) {
        const container = document.getElementById(containerId);
        if (!container) {
            console.error("PDF container not found", containerId);
            return;
        }

        container.innerHTML = '<div class="pdfjs-loading text-muted text-center p-4">กำลังโหลดไฟล์ PDF...</div>';

        try {
            const pdfjsLib = await ensurePdfJsLoaded();
            const loadingTask = pdfjsLib.getDocument({ url, withCredentials: true });
            const pdfDocument = await loadingTask.promise;

            container.innerHTML = "";

            for (let pageNumber = 1; pageNumber <= pdfDocument.numPages; pageNumber++) {
                const page = await pdfDocument.getPage(pageNumber);
                const viewport = page.getViewport({ scale: 1.4 });

                const canvas = document.createElement("canvas");
                canvas.className = "pdfjs-page-canvas";
                const context = canvas.getContext("2d");

                canvas.height = viewport.height;
                canvas.width = viewport.width;

                container.appendChild(canvas);

                await page.render({ canvasContext: context, viewport }).promise;
            }
        } catch (error) {
            console.error("Failed to render PDF", error);
            container.innerHTML = '<div class="pdfjs-error alert alert-danger m-3">ไม่สามารถโหลดตัวอย่างไฟล์ PDF ได้ กรุณากดดาวน์โหลดไฟล์เพื่อเปิดด้วยโปรแกรมอื่น</div>';
        }
    }

    window.pdfViewer = {
        renderPdf
    };
})();
