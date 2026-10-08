// Runs in <head> before first paint: applies the saved (or system) theme and the sidebar rail state,
// so the page never flashes the wrong theme. External file because the CSP forbids inline scripts.
(function () {
    'use strict';
    var root = document.documentElement;
    var theme = null;
    var collapsed = false;

    try {
        theme = window.localStorage.getItem('hr.theme');
        collapsed = window.localStorage.getItem('hr.sidebar') === 'collapsed';
    } catch (e) {
        // Storage can be unavailable (private mode, blocked site data); fall back to defaults.
    }

    if (theme !== 'light' && theme !== 'dark') {
        theme = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    root.setAttribute('data-bs-theme', theme);
    if (collapsed) {
        root.classList.add('sidebar-collapsed');
    }
})();
