// wwwroot/js/shellToggle.js
window.shellToggle = (function () {
    let observer = null;

    function el() {
        return document.getElementById('app-shell');
    }

    function currentCollapsed() {
        const host = el();
        return host ? host.classList.contains('shell--collapsed') : null;
    }

    return {
        setCollapsed(collapsed) {
            const host = el();
            if (!host) return;
            host.classList.toggle('shell--collapsed', collapsed);
            try { localStorage.setItem('sidebarCollapsed', String(collapsed)); } catch { }
        },
        save(collapsed) {
            try { localStorage.setItem('sidebarCollapsed', String(collapsed)); } catch { }
        },
        getSaved() {
            try { return localStorage.getItem('sidebarCollapsed'); } catch { return null; }
        },
        getState() {
            const c = currentCollapsed();
            return c === null ? null : String(c);
        },
        // เฝ้า class ของ #app-shell แล้วเรียกกลับหา Blazor
        watch(dotnetRef) {
            const host = el();
            if (!host || !window.MutationObserver) return;

            if (observer) {
                try { observer.disconnect(); } catch { }
                observer = null;
            }

            observer = new MutationObserver(() => {
                const c = currentCollapsed();
                if (c !== null && dotnetRef) {
                    dotnetRef.invokeMethodAsync('OnShellStateChanged', c);
                }
            });

            observer.observe(host, { attributes: true, attributeFilter: ['class'] });

            // แจ้งสถานะครั้งแรกทันที
            const c = currentCollapsed();
            if (c !== null && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnShellStateChanged', c);
            }
        }
    };
})();
