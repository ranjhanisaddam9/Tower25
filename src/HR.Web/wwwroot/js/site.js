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

    // ---------- Pay form: live derived figure, per-period preview and "loses money" warning ----------
    // Mirrors the server (SPEC §2/§5, f = 1): Billed = Salary + Commission (monthly USD; CompanyRecommended enters billed and
    // commission, BudgetHire budget and salary, Owner salary); per period each figure is
    // round2(monthly / 2), billed = salary + commission, PKR = round0(salary × rate). The server re-derives everything.
    document.querySelectorAll('[data-pay-calc]').forEach(function (box) {
        var form = box.closest('form');
        var source = box.dataset.source;
        var rate = parseFloat(box.dataset.rate || '');
        var field = function (name) { return form.elements.namedItem(name); };
        var salary = field('Salary');
        var commission = field('Commission');
        var budget = field('Budget');
        var derivedSalary = box.querySelector('[data-derived="Salary"]');
        var derivedCommission = box.querySelector('[data-derived="Commission"]');
        var derivedBilled = box.querySelector('[data-derived="Billed"]');
        var period = function (name) { return box.querySelector('[data-period="' + name + '"]'); };
        var loss = form.querySelector('[data-loss-warning]');
        var round2 = function (x) { return Math.sign(x) * Math.round((Math.abs(x) + Number.EPSILON) * 100) / 100; };
        var round0 = function (x) { return Math.sign(x) * Math.round(Math.abs(x)); };
        var number = function (input) { var v = input ? parseFloat(input.value) : NaN; return isNaN(v) ? null : v; };
        var usd = function (x) {
            return (x < 0 ? '−$' : '$') + Math.abs(x).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        };
        var pkr = function (x) { return 'Rs ' + x.toLocaleString('en-PK', { maximumFractionDigits: 0 }); };
        var setText = function (el, text) { if (el) { el.textContent = text; } };

        function update() {
            var s = number(salary);
            var c = null;
            var b = null;
            if (source === 'CompanyRecommended') {
                // The Admin enters billed and commission; the salary is the difference.
                b = number(budget);
                c = number(commission);
                s = b !== null && c !== null ? round2(b - c) : null;
            } else if (source === 'BudgetHire') {
                b = number(budget);
                c = s !== null && b !== null ? round2(b - s) : null;
            } else {
                c = 0;
                b = s;
            }

            if (derivedSalary) { derivedSalary.value = s === null ? '' : s.toFixed(2); }
            if (derivedCommission) { derivedCommission.value = c === null ? '' : c.toFixed(2); }
            if (derivedBilled) { derivedBilled.value = b === null ? '' : b.toFixed(2); }

            var ready = s !== null && c !== null;
            var ps = ready ? round2(s / 2) : null;
            var pc = ready ? round2(c / 2) : null;
            setText(period('Salary'), ready ? usd(ps) : '—');
            setText(period('Commission'), ready ? usd(pc) : '—');
            setText(period('Billed'), ready ? usd(round2(ps + pc)) : '—');
            setText(period('Pkr'), ready && rate > 0 ? '≈ ' + pkr(round0(ps * rate)) : '—');

            if (loss) {
                var losing = source === 'BudgetHire' && c !== null && c < 0;
                loss.hidden = !(losing || loss.dataset.serverLoss === 'true');
            }
        }

        [salary, commission, budget].forEach(function (input) { if (input) { input.addEventListener('input', update); } });
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

    // ---------- Print buttons ----------
    // Usage: <button type="button" data-print>Print</button> (no inline handlers under the CSP).
    document.addEventListener('click', function (event) {
        if (event.target.closest('[data-print]')) {
            window.print();
        }
    });
})();
