// Aurora Glass behaviour: theme toggle, sidebar rail, toasts, confirm modal.
// No inline handlers anywhere (CSP): everything is wired here with addEventListener.
(function () {
    'use strict';

    var root = document.documentElement;

    function store(key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch (e) {
            // Ignore: the preference just won't persist.
        }
    }

    function read(key) {
        try {
            return window.localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    // ---------- Theme toggle ----------
    var themeToggle = document.getElementById('themeToggle');

    function syncThemeButton() {
        if (!themeToggle) {
            return;
        }
        var isDark = root.getAttribute('data-bs-theme') === 'dark';
        themeToggle.setAttribute('aria-label', isDark ? 'Switch to light theme' : 'Switch to dark theme');
        themeToggle.setAttribute('title', isDark ? 'Switch to light theme' : 'Switch to dark theme');
    }

    if (themeToggle) {
        themeToggle.addEventListener('click', function () {
            var next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
            root.setAttribute('data-bs-theme', next);
            store('hr.theme', next);
            syncThemeButton();
        });
        syncThemeButton();
    }

    // Follow the OS theme until the user picks one explicitly.
    if (window.matchMedia) {
        var media = window.matchMedia('(prefers-color-scheme: dark)');
        var onSystemChange = function (event) {
            var saved = read('hr.theme');
            if (saved !== 'light' && saved !== 'dark') {
                root.setAttribute('data-bs-theme', event.matches ? 'dark' : 'light');
                syncThemeButton();
            }
        };
        if (media.addEventListener) {
            media.addEventListener('change', onSystemChange);
        }
    }

    // ---------- Sidebar rail (lg and up) ----------
    var collapseToggle = document.getElementById('sidebarCollapseToggle');

    function syncCollapseButton() {
        if (!collapseToggle) {
            return;
        }
        var collapsed = root.classList.contains('sidebar-collapsed');
        collapseToggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        var label = collapsed ? 'Expand sidebar' : 'Collapse sidebar';
        collapseToggle.setAttribute('title', label);
        var text = collapseToggle.querySelector('.nav-label');
        if (text) {
            text.textContent = label;
        }
    }

    if (collapseToggle) {
        collapseToggle.addEventListener('click', function () {
            var collapsed = root.classList.toggle('sidebar-collapsed');
            store('hr.sidebar', collapsed ? 'collapsed' : 'expanded');
            syncCollapseButton();
        });
        syncCollapseButton();
    }

    // ---------- Toasts (TempData) ----------
    if (window.bootstrap) {
        document.querySelectorAll('.toast').forEach(function (element) {
            window.bootstrap.Toast.getOrCreateInstance(element).show();
        });
    }

    // ---------- Confirm modal ----------
    // Usage: <button type="submit" data-confirm="Message" data-confirm-title="Title" data-confirm-action="Delete">
    var modalElement = document.getElementById('confirmModal');
    if (modalElement && window.bootstrap) {
        var modal = window.bootstrap.Modal.getOrCreateInstance(modalElement);
        var titleElement = document.getElementById('confirmModalTitle');
        var bodyElement = document.getElementById('confirmModalBody');
        var acceptButton = document.getElementById('confirmModalAccept');
        var pending = null;

        document.addEventListener('click', function (event) {
            var trigger = event.target.closest('[data-confirm]');
            if (!trigger || trigger.disabled) {
                return;
            }
            if (trigger.dataset.confirmed === 'true') {
                delete trigger.dataset.confirmed;
                return;
            }
            event.preventDefault();
            pending = trigger;
            titleElement.textContent = trigger.dataset.confirmTitle || 'Are you sure?';
            bodyElement.textContent = trigger.dataset.confirm;
            acceptButton.textContent = trigger.dataset.confirmAction || 'Confirm';
            var primary = trigger.dataset.confirmVariant === 'primary';
            acceptButton.classList.toggle('btn-primary-gradient', primary);
            acceptButton.classList.toggle('btn-danger-solid', !primary);
            modal.show(trigger);
        });

        acceptButton.addEventListener('click', function () {
            if (!pending) {
                return;
            }
            var trigger = pending;
            pending = null;
            modal.hide();
            trigger.dataset.confirmed = 'true';
            trigger.click();
        });

        modalElement.addEventListener('shown.bs.modal', function () {
            acceptButton.focus();
        });
    }

    // ---------- Copy to clipboard ----------
    // Usage: <button type="button" data-copy-target="elementId">, with an optional .copy-label inside.
    document.querySelectorAll('[data-copy-target]').forEach(function (button) {
        button.addEventListener('click', function () {
            var source = document.getElementById(button.dataset.copyTarget);
            var label = button.querySelector('.copy-label');
            var status = document.getElementById(button.getAttribute('aria-describedby') || '');
            if (!source || !navigator.clipboard) {
                if (status) {
                    status.textContent = 'Copy is not available; select the text and copy it manually.';
                }
                return;
            }
            navigator.clipboard.writeText(source.textContent.trim()).then(function () {
                if (label) {
                    label.textContent = 'Copied';
                    window.setTimeout(function () { label.textContent = 'Copy'; }, 2000);
                }
                if (status) {
                    status.textContent = 'Copied to the clipboard.';
                }
            }, function () {
                if (status) {
                    status.textContent = 'Copy failed; select the text and copy it manually.';
                }
            });
        });
    });
})();
