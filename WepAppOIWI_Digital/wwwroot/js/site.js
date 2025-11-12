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
