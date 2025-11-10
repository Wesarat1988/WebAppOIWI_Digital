// wwwroot/js/shellToggle.js
// เวอร์ชัน GOD MODE - ไม่กระทบ logic เดิม แต่ Sidebar ตายสนิท!
(function () {
    let observer = null;
    let dotnetRef = null;

    function host() {
        return document.getElementById('app-shell');
    }

    function sidebar() {
        return host()?.querySelector('.shell__sidebar');
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

    // บังคับให้ Sidebar กลายเป็น GPU layer ตลอดกาล (แก้ปัญหาขยับตอน scroll PDF)
    function lockSidebarToGPU() {
        const sb = sidebar();
        if (!sb) return;
        // 3 ท่าไม้ตายรวมกัน = ไม่มีอะไรดันได้อีกต่อไป
        sb.style.transform = 'translateZ(0)';
        sb.style.willChange = 'transform';
        sb.style.contain = 'strict';
        // ป้องกัน repaint ดัน layout
        sb.style.backfaceVisibility = 'hidden';
        sb.style.perspective = '1000';
    }

    // เรียกตอนเริ่มต้น
    lockSidebarToGPU();

    window.shellToggle = {
        // เดิม: setCollapsed(collapsed)
        setCollapsed(collapsed) {
            const h = host();
            if (!h) return;
            const before = h.classList.contains('shell--collapsed');
            h.classList.toggle('shell--collapsed', collapsed);
            notifyIfChanged(before);

            // หลัง toggle เสร็จ บังคับล็อก GPU อีกครั้ง (กรณี class เปลี่ยน)
            requestAnimationFrame(lockSidebarToGPU);
        },

        // เดิม: watch(ref)
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
                // ทุกครั้งที่มีการเปลี่ยน class → ล็อก GPU อีกครั้ง
                requestAnimationFrame(lockSidebarToGPU);
            });

            observer.observe(h, { attributes: true, attributeFilter: ['class'] });

            // แจ้งสถานะครั้งแรก + ล็อก GPU
            const c = isCollapsed();
            if (c !== null && dotnetRef) {
                dotnetRef.invokeMethodAsync('OnShellStateChanged', c);
            }
            lockSidebarToGPU();
        }
    };

    // ถ้ามีการ toggle จากที่อื่น (เช่น devtools) ต้องล็อกด้วย
    document.addEventListener('DOMContentLoaded', lockSidebarToGPU);
    window.addEventListener('resize', () => requestAnimationFrame(lockSidebarToGPU));

})();