window.blazorFocus = (element) => {
    if (!element) {
        return;
    }

    try {
        element.focus();
    } catch (err) {
        // ignore focus errors (element might be gone)
    }
};


window.appTextZoom = (function () {
    const storageKey = 'oiwi_text_zoom';
    const allowed = new Set(['small', 'normal', 'large']);
    const classPrefix = 'text-zoom-';
    const zoomClasses = ['text-zoom-small', 'text-zoom-normal', 'text-zoom-large'];

    function sanitize(level) {
        return allowed.has(level) ? level : 'normal';
    }

    function apply(level) {
        const finalLevel = sanitize(level);
        const className = classPrefix + finalLevel;
        const root = document.documentElement;
        const body = document.body;

        zoomClasses.forEach(cls => {
            if (root) root.classList.remove(cls);
            if (body) body.classList.remove(cls);
        });

        if (root) root.classList.add(className);
        if (body) body.classList.add(className);

        const host = document.getElementById('app-shell');
        if (host) {
            zoomClasses.forEach(cls => host.classList.remove(cls));
            host.classList.add(className);
        }

        return finalLevel;
    }

    function get() {
        try {
            const value = window.localStorage ? window.localStorage.getItem(storageKey) : null;
            return sanitize(value || '');
        } catch (err) {
            return 'normal';
        }
    }

    return {
        initialize() {
            const level = get();
            apply(level);
            return level;
        },
        set(level) {
            const finalLevel = apply(level);
            try {
                if (window.localStorage) {
                    window.localStorage.setItem(storageKey, finalLevel);
                }
            } catch (err) {
                // ignore persistence failures (e.g. private mode)
            }
        }
    };
})();
