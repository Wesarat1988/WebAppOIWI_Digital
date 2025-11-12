window.oiwi_getPageSize = () => {
  try {
    const value = localStorage.getItem('oiwi.pageSize');
    if (value === null) {
      return null;
    }
    const parsed = parseInt(value, 10);
    return Number.isNaN(parsed) ? null : parsed;
  } catch {
    return null;
  }
};

window.oiwi_setPageSize = (n) => {
  try {
    localStorage.setItem('oiwi.pageSize', String(n));
  } catch {
    // ignore persistence failures
  }
};

window.oiwi_scrollToId = (id) => {
  const el = document.getElementById(id);
  if (el) {
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
};

window.oiwi_focusById = (id) => {
  const el = document.getElementById(id);
  if (el && typeof el.focus === 'function') {
    el.focus({ preventScroll: true });
  }
};
