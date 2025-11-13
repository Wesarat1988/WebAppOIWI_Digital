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


window.appTextZoom = window.appTextZoom || {};

(function attachTextZoomHelpers(api) {
    const storageKey = 'oiwi_text_zoom';

    api.get = function () {
        try {
            if (window.localStorage) {
                return window.localStorage.getItem(storageKey) || null;
            }
        } catch (err) {
            // ignore storage access errors (e.g. private mode)
        }

        return null;
    };

    api.set = function (value) {
        try {
            if (window.localStorage) {
                window.localStorage.setItem(storageKey, value);
            }
        } catch (err) {
            // ignore persistence failures
        }
    };
})(window.appTextZoom);
