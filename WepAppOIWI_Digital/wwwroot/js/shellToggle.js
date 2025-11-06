// wwwroot/js/shellToggle.js
(function () {
    let observer = null;
    let dotnetRef = null;

    function host() {
        return document.getElementById('app-shell');
    }

    function isCollapsed() {
        const h = host();
        return h ? h.classList.contains('shell--collapsed') : null;
    }

    function notifyIfChanged(before) {
        const after = isCollapsed();
        if (after !== null && after !== before && dotnetRef && window.DotNet) {
            dotnetRef.invokeMethodAsync('OnShellStateChanged', after);
        }
    }

    window.shellToggle = {
        // บังคับยุบ/กาง และแจ้งกลับหา Razor ถ้าสถานะเปลี่ยน
        setCollapsed(collapsed) {
            const h = host();
            if (!h) return;
            const before = h.classList.contains('shell--collapsed');
            h.classList.toggle('shell--collapsed', collapsed);
            notifyIfChanged(before);
        },

        // ใช้โดย ShellToggle.razor เพื่อติดตามการเปลี่ยนแปลง class จากแหล่งอื่น
        watch(ref) {
            dotnetRef = ref;
            const h = host();
            if (!h || !window.MutationObserver) return;

            if (observer) {
                try { observer.disconnect(); } catch { }
                observer = null;
            }

            observer = new MutationObserver(() => {
                const c = isCollapsed();
                if (c !== null && dotnetRef) {
                    dotnetRef.invokeMethodAsync('OnShellStateChanged', c);
                }
            });

            observer.observe(h, { attributes: true, attributeFilter: ['class'] });

            // แจ้งสถานะครั้งแรกทันที
            const c = isCollapsed();
            if (c !== null && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnShellStateChanged', c);
            }
        }
    };
})();
