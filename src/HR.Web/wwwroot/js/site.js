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
            // Optional condition: only confirm when a field in the same form has a given value.
            if (trigger.dataset.confirmIfName) {
                var field = trigger.form && trigger.form.elements.namedItem(trigger.dataset.confirmIfName);
                if (!field || field.value !== trigger.dataset.confirmIfValue) {
                    return;
                }
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

    // ---------- Pay form: live margin preview and "loses money" warning (BudgetHire) ----------
    // Mirrors the server (SPEC §5, f = 1): margin = round2(budget/2) − payUsd, where PKR pay is
    // payPkr = round0(pay/2), payUsd = round2(payPkr / rate). The server re-checks everything on save.
    document.querySelectorAll('[data-margin-preview]').forEach(function (box) {
        var form = box.closest('form');
        var budget = form.elements.namedItem('Budget');
        var pay = form.elements.namedItem('Pay');
        var currency = form.elements.namedItem('Currency');
        var output = box.querySelector('[data-margin-value]');
        var loss = form.querySelector('[data-loss-warning]');
        var rate = parseFloat(box.dataset.rate || '');
        var round2 = function (x) { return Math.sign(x) * Math.round((Math.abs(x) + Number.EPSILON) * 100) / 100; };
        var round0 = function (x) { return Math.sign(x) * Math.round(Math.abs(x)); };
        var money = function (x) {
            return (x < 0 ? '−$' : '$') + Math.abs(x).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        };

        function update() {
            var b = parseFloat(budget.value);
            var p = parseFloat(pay.value);
            var isPkr = currency.value === 'PKR';
            var monthlyPayUsd = null;
            var payUsd = null;
            if (b > 0 && p > 0) {
                if (!isPkr) {
                    monthlyPayUsd = p;
                    payUsd = round2(p / 2);
                } else if (rate > 0) {
                    monthlyPayUsd = p / rate;
                    payUsd = round2(round0(p / 2) / rate);
                }
            }

            output.textContent = payUsd === null ? '—' : money(round2(round2(b / 2) - payUsd));
            if (loss) {
                var losing = monthlyPayUsd !== null && monthlyPayUsd >= b;
                loss.hidden = !(losing || loss.dataset.serverLoss === 'true');
            }
        }

        [budget, pay].forEach(function (input) { input.addEventListener('input', update); });
        currency.addEventListener('change', update);
        update();
    });

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
            var text = source.textContent.trim();
            if (button.dataset.copyCompact === 'true') {
                text = text.replace(/\s+/g, '');
            }
            navigator.clipboard.writeText(text).then(function () {
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
