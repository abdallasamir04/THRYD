/*
 * School Management — client behaviour. No framework; jQuery is only here for the ASP.NET
 * unobtrusive validation that Razor forms use.
 *
 *  - Theme (light / dark / system) and the phone navigation drawer
 *  - AJAX lists: filters, sorting and paging replace the table without a page reload
 *  - Modal forms: add / edit / details open in a dialog and save with fetch()
 *  - Confirm dialog for destructive actions, toast notifications, loading indicators
 *  - Searchable dropdowns for <select data-searchable>
 *  - File dropzone and the import column-mapping helpers
 */
(function () {
    "use strict";

    var doc = document;
    var $$ = function (sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); };

    // ------------------------------------------------------------------ theme
    var Theme = {
        get: function () { try { return localStorage.getItem("sm.theme") || "light"; } catch (e) { return "light"; } },
        set: function (pref) {
            try { localStorage.setItem("sm.theme", pref); } catch (e) { /* ignore */ }
            Theme.apply(pref);
        },
        apply: function (pref) {
            var dark = pref === "dark" || (pref === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
            doc.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
            doc.documentElement.setAttribute("data-theme-pref", pref);
            $$("[data-theme-toggle]").forEach(function (b) {
                b.setAttribute("aria-pressed", dark ? "true" : "false");
                b.setAttribute("title", dark ? "Switch to light mode" : "Switch to dark mode");
            });
            $$("[data-theme-option]").forEach(function (b) {
                b.setAttribute("aria-pressed", b.getAttribute("data-theme-option") === pref ? "true" : "false");
            });
        }
    };

    doc.addEventListener("click", function (e) {
        var toggle = e.target.closest("[data-theme-toggle]");
        if (toggle) {
            Theme.set(doc.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark");
            return;
        }
        var option = e.target.closest("[data-theme-option]");
        if (option) Theme.set(option.getAttribute("data-theme-option"));
    });
    if (window.matchMedia) {
        window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", function () {
            if (Theme.get() === "system") Theme.apply("system");
        });
    }

    // ------------------------------------------------------------------ navigation drawer
    function setNav(open) {
        doc.body.classList.toggle("nav-open", open);
        $$("[data-nav-toggle]").forEach(function (b) { b.setAttribute("aria-expanded", open ? "true" : "false"); });
        if (open) { var first = doc.querySelector(".sidebar a, .sidebar button"); if (first) first.focus(); }
    }
    doc.addEventListener("click", function (e) {
        if (e.target.closest("[data-nav-toggle]")) setNav(!doc.body.classList.contains("nav-open"));
        else if (e.target.closest("[data-nav-close]")) setNav(false);
    });
    doc.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && doc.body.classList.contains("nav-open")) setNav(false);
    });

    // ------------------------------------------------------------------ PDF downloads
    // A download link holds its export path; on click it takes the page's current query string, so
    // filters, dates and tabs changed in place (AJAX lists, attendance tabs) are what gets exported.
    doc.addEventListener("click", function (e) {
        var link = e.target.closest("a[data-download]");
        if (link) link.href = link.getAttribute("data-download") + window.location.search;
    });

    // ------------------------------------------------------------------ progress + fetch
    var bar = doc.createElement("div");
    bar.className = "progress-bar";
    bar.setAttribute("aria-hidden", "true");
    doc.body.appendChild(bar);
    var pending = 0;
    function busy(on) {
        pending += on ? 1 : -1;
        if (pending > 0) { bar.classList.remove("is-done"); void bar.offsetWidth; bar.classList.add("is-active"); }
        else { bar.classList.remove("is-active"); bar.classList.add("is-done"); }
    }

    function request(url, options) {
        options = options || {};
        options.headers = Object.assign({ "X-Requested-With": "XMLHttpRequest" }, options.headers || {});
        options.credentials = "same-origin";
        busy(true);
        return fetch(url, options).then(function (res) {
            var type = res.headers.get("content-type") || "";
            var body = type.indexOf("application/json") >= 0 ? res.json() : res.text();
            return body.then(function (data) { return { ok: res.ok, status: res.status, json: typeof data === "object", data: data }; });
        }).finally(function () { busy(false); });
    }

    // ------------------------------------------------------------------ toasts
    // One shared notification for every kind of feedback: success, info, warning and error.
    // Screen readers hear them through two hidden live regions (errors and warnings interrupt).
    var toastHost = doc.createElement("div");
    toastHost.className = "toasts";
    // A manual popover lives in the top layer, so toasts stay visible above an open dialog.
    var canPopover = typeof toastHost.showPopover === "function";
    if (canPopover) toastHost.setAttribute("popover", "manual");
    doc.body.appendChild(toastHost);
    function raiseToasts() {
        if (!canPopover) return;
        try {
            if (toastHost.matches(":popover-open")) toastHost.hidePopover();
            toastHost.showPopover(); // re-showing puts it above a dialog opened after it
        } catch (e) { /* not connected yet */ }
    }
    var toastIcons = { success: "check-circle", error: "alert-circle", warning: "alert-triangle", info: "info" };
    var toastLabels = { success: "Success", error: "Error", warning: "Warning", info: "Information" };
    var toastTimeouts = { success: 5000, info: 6000, warning: 8000, error: 9000 };
    var MAX_TOASTS = 4;

    function liveRegion(assertive) {
        var r = doc.createElement("div");
        r.className = "visually-hidden";
        r.setAttribute("aria-live", assertive ? "assertive" : "polite");
        r.setAttribute("aria-atomic", "true");
        doc.body.appendChild(r);
        return r;
    }
    var politeRegion = liveRegion(false), assertiveRegion = liveRegion(true);
    function announce(text, assertive) {
        var region = assertive ? assertiveRegion : politeRegion;
        region.textContent = "";
        setTimeout(function () { region.textContent = text; }, 60);
    }

    function icon(name, cls) {
        return '<svg class="icon ' + (cls || "") + '" aria-hidden="true"><use href="#i-' + name + '"></use></svg>';
    }

    function toast(message, type) {
        type = toastIcons[type] ? type : "success";
        if (!message) return null;
        // The same message already on screen: keep one copy and restart its timer.
        var same = $$(".toast:not(.is-leaving)", toastHost).filter(function (t) { return t._message === message && t._type === type; })[0];
        if (same) { same._restart(); announce(toastLabels[type] + ": " + message, type === "error" || type === "warning"); return same; }

        var el = doc.createElement("div");
        el.className = "toast toast--" + type;
        el.innerHTML = '<span class="toast__icon">' + icon(toastIcons[type]) + '</span>' +
            '<span class="toast__text"></span>' +
            '<button class="icon-btn" type="button" aria-label="Dismiss notification">' + icon("x", "icon--sm") + "</button>";
        el.querySelector(".toast__text").textContent = message;
        el._message = message;
        el._type = type;

        var timer = null;
        var remove = function () {
            clearTimeout(timer);
            if (el.classList.contains("is-leaving")) return;
            el.classList.add("is-leaving");
            setTimeout(function () { el.remove(); }, 200);
        };
        var start = function () { clearTimeout(timer); timer = setTimeout(remove, toastTimeouts[type]); };
        var pause = function () { clearTimeout(timer); };
        el._restart = start;
        el._remove = remove;
        el.querySelector("button").addEventListener("click", remove);
        // Reading or tabbing to a toast keeps it on screen.
        el.addEventListener("mouseenter", pause);
        el.addEventListener("mouseleave", start);
        el.addEventListener("focusin", pause);
        el.addEventListener("focusout", start);

        var shown = $$(".toast:not(.is-leaving)", toastHost);
        while (shown.length >= MAX_TOASTS) shown.shift()._remove();
        toastHost.appendChild(el);
        raiseToasts();
        announce(toastLabels[type] + ": " + message, type === "error" || type === "warning");
        start();
        return el;
    }
    ["success", "info", "warning", "error"].forEach(function (t) { toast[t] = function (message) { return toast(message, t); }; });

    // ------------------------------------------------------------------ dialog (confirm / alert)
    // The one confirmation dialog of the application. Every "Are you sure?", unsaved-changes and
    // message box goes through Dialog.confirm / Dialog.alert, never window.confirm or window.alert.
    var Dialog = (function () {
        var el = doc.getElementById("confirm-dialog");
        if (!el) {
            el = doc.createElement("dialog");
            el.id = "confirm-dialog";
            el.className = "modal modal--confirm";
            el.setAttribute("aria-labelledby", "confirm-title");
            el.setAttribute("aria-describedby", "confirm-text");
            el.innerHTML = '<div class="modal__content"><div class="modal__head"><span class="modal__icon" data-confirm-icon></span><div>' +
                '<h2 class="modal__title" id="confirm-title" data-confirm-title></h2><p class="modal__sub" id="confirm-text" data-confirm-text></p></div></div>' +
                '<div class="modal__foot"><button class="btn btn--secondary" type="button" data-confirm-cancel>Cancel</button>' +
                '<button class="btn btn--primary" type="button" data-confirm-ok>OK</button></div></div>';
            doc.body.appendChild(el);
        }
        el.setAttribute("role", "alertdialog");
        var titleEl = el.querySelector("[data-confirm-title]");
        var textEl = el.querySelector("[data-confirm-text]");
        var okBtn = el.querySelector("[data-confirm-ok]");
        var cancelBtn = el.querySelector("[data-confirm-cancel]");
        var iconBox = el.querySelector("[data-confirm-icon]") || el.querySelector(".modal__icon");
        var tones = {
            danger: { icon: "alert-triangle", button: "danger" },
            warning: { icon: "alert-triangle", button: "primary" },
            info: { icon: "info", button: "primary" },
            success: { icon: "check-circle", button: "primary" },
        };
        var pending = null, returnFocus = null;

        function finish(value) {
            if (!pending) return;
            var resolve = pending;
            pending = null;
            if (el.open) el.close();
            if (returnFocus && doc.contains(returnFocus) && typeof returnFocus.focus === "function") {
                try { returnFocus.focus({ preventScroll: true }); } catch (e) { returnFocus.focus(); }
            }
            returnFocus = null;
            resolve(value);
        }

        /**
         * options: { title, message, confirmText, cancelText, tone: danger|warning|info|success,
         *            confirmTone: danger|primary, icon, focus: "confirm"|"cancel", alert: bool }
         * Resolves true (confirmed) or false (Cancel, Escape or a click outside).
         */
        function open(options) {
            var o = typeof options === "string" ? { message: options } : (options || {});
            if (pending) finish(false);
            var tone = tones[o.tone] ? o.tone : "danger";
            var buttonTone = o.confirmTone || tones[tone].button;
            titleEl.textContent = o.title || (o.alert ? "Notice" : "Are you sure?");
            textEl.textContent = o.message || "";
            textEl.hidden = !o.message;
            okBtn.textContent = o.confirmText || (o.alert ? "OK" : "Confirm");
            okBtn.className = "btn " + (buttonTone === "danger" ? "btn--danger" : "btn--primary");
            cancelBtn.textContent = o.cancelText || "Cancel";
            cancelBtn.hidden = !!o.alert;
            iconBox.className = "modal__icon modal__icon--" + tone;
            iconBox.innerHTML = icon(o.icon || tones[tone].icon, "icon--lg");
            el.classList.add("modal--confirm");
            returnFocus = doc.activeElement;
            var focusCancel = !o.alert && (o.focus ? o.focus === "cancel" : buttonTone === "danger");
            return new Promise(function (resolve) {
                pending = resolve;
                el.showModal();
                setTimeout(function () { (focusCancel ? cancelBtn : okBtn).focus(); }, 30);
            });
        }

        okBtn.addEventListener("click", function () { finish(true); });
        cancelBtn.addEventListener("click", function () { finish(false); });
        el.addEventListener("click", function (e) { if (e.target === el) finish(false); }); // backdrop
        // Escape. The close event is queued: ignore one that arrives after the dialog was opened again.
        el.addEventListener("close", function () { if (!el.open) finish(false); });

        return {
            confirm: open,
            alert: function (options) {
                var o = typeof options === "string" ? { message: options } : Object.assign({}, options);
                o.alert = true;
                if (!o.tone) o.tone = "info";
                return open(o).then(function () { });
            },
        };
    })();

    window.App = { toast: toast, confirm: Dialog.confirm, alert: Dialog.alert };

    // ------------------------------------------------------------------ unsaved changes
    // A POST form is "dirty" when its values differ from what they were when the user first touched it
    // (or when the server sent it back with validation errors). Pages can add their own sources
    // (App.unsaved.register), e.g. the attendance checklist's unsaved rows.
    // Inside the app (links, filters, date pickers, reloads after a save, closing a form dialog) the
    // user gets the THRYD dialog. Reload, closing the tab and external links can only use the browser's
    // own beforeunload prompt — browsers do not allow a custom dialog there.
    var Unsaved = (function () {
        var sources = [];
        var suspendedUntil = 0;
        var SKIP = "[data-no-unsaved], [data-confirm]";

        function tracked(form) {
            return !!form && form.nodeName === "FORM" && (form.getAttribute("method") || "get").toLowerCase() === "post" && !form.matches(SKIP);
        }
        function serialize(form) {
            var parts = [];
            new FormData(form).forEach(function (value, key) {
                if (key === "__RequestVerificationToken") return;
                parts.push(key + "=" + (typeof value === "string" ? value : value.name + ":" + value.size));
            });
            return parts.join("&");
        }
        // Snapshot just before the first interaction, so values filled in by scripts on load never count.
        function watch(e) {
            var t = e.target;
            var form = t && (t.form || (t.closest && t.closest("form")));
            if (tracked(form) && form._unsavedSnapshot == null) form._unsavedSnapshot = serialize(form);
        }
        doc.addEventListener("focusin", watch, true);
        doc.addEventListener("pointerdown", watch, true);

        function isDirty(form) {
            if (!tracked(form) || !doc.contains(form)) return false;
            if (form._unsavedForce) return true;
            return form._unsavedSnapshot != null && serialize(form) !== form._unsavedSnapshot;
        }
        function clean(form) { if (form) { form._unsavedSnapshot = null; form._unsavedForce = false; } }
        function force(form) { if (tracked(form)) form._unsavedForce = true; }

        /** Sentences describing what is unsaved; empty when nothing is. `except` is a form being submitted. */
        function describe(except) {
            var list = [];
            sources.forEach(function (fn) {
                try { var m = fn(); if (m) list.push(m); } catch (e) { /* a source must never block navigation */ }
            });
            var forms = Array.prototype.filter.call(doc.forms, function (f) { return f !== except && isDirty(f); });
            if (forms.length) list.push(list.length ? "Other changes on this page are not saved either." : "You have changes on this page that are not saved yet.");
            return list;
        }
        function suspend() { suspendedUntil = Date.now() + 4000; }
        function suspended() { return Date.now() < suspendedUntil; }

        /** Asks before leaving when something is unsaved. Resolves true when it is safe to go on. */
        function confirmLeave(except, options) {
            var o = options || {};
            if (suspended()) return Promise.resolve(true);
            var list = describe(except);
            if (list.length === 0) return Promise.resolve(true);
            return Dialog.confirm({
                title: o.title || "Leave without saving?",
                message: list.join(" ") + " " + (o.consequence || "If you leave this page now, these changes will be lost."),
                confirmText: o.confirmText || "Leave without saving",
                cancelText: o.cancelText || "Stay on page",
                tone: "warning", confirmTone: "danger", focus: "cancel",
            }).then(function (ok) { if (ok) suspend(); return ok; });
        }

        function navigate(url) {
            return confirmLeave().then(function (ok) { if (ok) window.location.href = url; return ok; });
        }

        window.addEventListener("beforeunload", function (e) {
            if (suspended() || describe().length === 0) return;
            e.preventDefault();
            e.returnValue = "";
        });

        return {
            register: function (fn) { sources.push(fn); },
            isDirty: isDirty, clean: clean, force: force, tracked: tracked,
            any: function (except) { return describe(except).length > 0; },
            confirmLeave: confirmLeave, navigate: navigate, suspend: suspend,
        };
    })();
    window.App.unsaved = Unsaved;
    window.App.navigate = Unsaved.navigate;

    // ------------------------------------------------------------------ validation messages
    // Forms checked by jQuery unobtrusive validation keep it. Every other form (HTML required, min, max,
    // pattern…) is checked here instead of with the browser's validation bubbles, and shows the same
    // inline message style next to the field.
    var Validate = (function () {
        var seq = 0;

        function messageFor(el) {
            var v = el.validity;
            var type = (el.type || "").toLowerCase();
            if (v.customError) return el.validationMessage;
            if (v.valueMissing) {
                if (el.getAttribute("data-msg-required")) return el.getAttribute("data-msg-required");
                if (type === "radio") return "Choose one option.";
                if (type === "checkbox") return "Tick this box to continue.";
                if (type === "file") return "Choose a file.";
                if (el.nodeName === "SELECT") return "Choose an option.";
                return "This field is required.";
            }
            if (v.typeMismatch) return type === "email" ? "Enter a valid email address." : type === "url" ? "Enter a valid web address." : "Enter a valid value.";
            if (v.tooShort) return "Use at least " + el.minLength + " characters.";
            if (v.tooLong) return "Use " + el.maxLength + " characters or fewer.";
            if (v.rangeUnderflow) return "Enter " + el.min + " or more.";
            if (v.rangeOverflow) return "Enter " + el.max + " or less.";
            if (v.patternMismatch) return el.getAttribute("title") || "Use the format shown.";
            if (v.stepMismatch || v.badInput) return "Enter a valid value.";
            return el.validationMessage || "Check this field.";
        }

        function group(el) {
            return el.type === "radio" ? el.closest("[role=radiogroup], fieldset, .radio-group") : null;
        }
        function members(el) {
            if (el.type !== "radio" || !el.form || !el.name) return [el];
            return $$('input[type=radio]', el.form).filter(function (r) { return r.name === el.name; });
        }

        function clearField(el) {
            members(el).forEach(function (m) {
                m.classList.remove("input-validation-error");
                m.removeAttribute("aria-invalid");
                if (m._appMsgId) {
                    var ids = (m.getAttribute("aria-describedby") || "").split(" ").filter(function (id) { return id && id !== m._appMsgId; });
                    if (ids.length) m.setAttribute("aria-describedby", ids.join(" ")); else m.removeAttribute("aria-describedby");
                    m._appMsgId = null;
                }
            });
            var g = group(el);
            if (g) g.classList.remove("is-invalid");
            var combo = el.closest(".combo");
            if (combo) combo.classList.remove("is-invalid");
            var msg = el._appMsg;
            if (msg) {
                if (msg._appCreated) msg.remove();
                else { msg.textContent = ""; msg.className = "field-validation-valid"; }
            }
            members(el).forEach(function (m) { m._appMsg = null; });
        }

        function clear(form) {
            $$("[aria-invalid=true]", form).forEach(clearField);
        }

        function mark(el, text) {
            var all = members(el);
            var form = el.form;
            var msg = form && el.name ? form.querySelector('[data-valmsg-for="' + (window.CSS && CSS.escape ? CSS.escape(el.name) : el.name) + '"]') : null;
            if (msg) {
                msg.className = "field-validation-error";
            } else {
                msg = doc.createElement("span");
                msg.className = "field-validation-error app-field-error";
                msg._appCreated = true;
                var anchor = group(el) || el.closest(".combo") || el.closest(".input-icon") || el;
                anchor.insertAdjacentElement("afterend", msg);
            }
            msg.textContent = text;
            msg.id = msg.id || "app-err-" + (++seq);
            all.forEach(function (m) {
                m.classList.add("input-validation-error");
                m.setAttribute("aria-invalid", "true");
                m._appMsg = msg;
                m._appMsgId = msg.id;
                var ids = (m.getAttribute("aria-describedby") || "").split(" ").filter(Boolean);
                if (ids.indexOf(msg.id) < 0) ids.push(msg.id);
                m.setAttribute("aria-describedby", ids.join(" "));
            });
            var g = group(el);
            if (g) g.classList.add("is-invalid");
            var combo = el.closest(".combo");
            if (combo) combo.classList.add("is-invalid");
        }

        /** Shows every invalid field of the form and moves focus to the first. Returns false when invalid. */
        function check(form) {
            clear(form);
            var seen = {};
            var invalid = $$("input, select, textarea", form).filter(function (el) {
                if (!el.willValidate || el.checkValidity()) return false;
                if (el.type === "radio") { if (seen[el.name]) return false; seen[el.name] = true; }
                return true;
            });
            invalid.forEach(function (el) { mark(el, messageFor(el)); });
            if (invalid.length === 0) return true;
            var first = invalid[0];
            var target = first.closest(".combo") ? first.closest(".combo").querySelector(".combo__button") : first;
            if (first.type === "radio") target = members(first).filter(function (r) { return !r.disabled; })[0] || first;
            target.scrollIntoView({ block: "center", behavior: "smooth" });
            try { target.focus({ preventScroll: true }); } catch (e) { target.focus(); }
            announce(invalid.length === 1 ? messageFor(first) : invalid.length + " fields need attention. " + messageFor(first), true);
            return false;
        }

        /** Forms without unobtrusive validation stop using the browser bubbles. */
        function adopt(form) {
            if (form._appValidate) return;
            var hasValidator = window.jQuery && jQuery(form).data("validator");
            if (hasValidator || form.hasAttribute("novalidate")) return;
            form.noValidate = true;
            form._appValidate = true;
        }

        // Capture phase: runs before every other submit handler, so an invalid form goes no further.
        doc.addEventListener("submit", function (e) {
            var form = e.target;
            if (!form._appValidate || (e.submitter && e.submitter.formNoValidate)) return;
            if (check(form)) return;
            e.preventDefault();
            e.stopImmediatePropagation();
        }, true);

        // A corrected field loses its message straight away.
        var revalidate = function (e) {
            var el = e.target;
            if (!el || el.getAttribute("aria-invalid") !== "true" || !el.form || !el.form._appValidate) return;
            if (el.checkValidity()) clearField(el);
            else if (el._appMsg) el._appMsg.textContent = messageFor(el);
        };
        doc.addEventListener("input", revalidate);
        doc.addEventListener("change", revalidate);

        return { adopt: adopt, check: check, clear: clear };
    })();

    // ------------------------------------------------------------------ skeleton loaders (shared)
    // Placeholders shaped like the content being loaded (css/skeleton.css). Used while a list reloads,
    // a modal loads, or a dependent field fetches its data; always removed when the request ends.
    var Skeleton = {
        line: function (width, cls) {
            return '<span class="sk sk-line ' + (cls || "") + '" style="width:' + width + '%"></span>';
        },
        person: function () {
            return '<span class="sk-row"><span class="sk sk-avatar"></span><span class="sk-stack">' +
                Skeleton.line(70) + Skeleton.line(45, "sk-line--sm") + "</span></span>";
        },
        /** Builds a skeleton that matches the container's current content (table rows, card grid or text). */
        forContent: function (root) {
            var widths = [82, 64, 74, 56, 90, 68];
            var table = root.querySelector("table");
            if (table && table.tHead && table.tHead.rows.length) {
                var cols = table.tHead.rows[0].cells.length;
                var rows = Math.min(10, Math.max(3, table.tBodies[0] ? table.tBodies[0].rows.length : 5));
                var body = "";
                for (var r = 0; r < rows; r++) {
                    body += "<tr>";
                    for (var c = 0; c < cols; c++) body += "<td>" + (c === 0 && cols > 2 ? Skeleton.person() : Skeleton.line(widths[(r + c) % widths.length])) + "</td>";
                    body += "</tr>";
                }
                return '<div class="sk-wrap table-wrap" aria-hidden="true"><table class="' + table.className + ' sk-table">' +
                    table.tHead.outerHTML + "<tbody>" + body + "</tbody></table></div>";
            }
            var grid = root.querySelector(".task-grid, .dept-grid, .member-grid, .class-grid, .position-grid, .duty-components, .hub, .bl-quarters");
            if (grid && grid.children.length) {
                var count = Math.min(12, Math.max(2, grid.children.length));
                var height = grid.children[0].offsetHeight;
                var cards = "";
                for (var i = 0; i < count; i++) {
                    cards += '<div class="sk-card"' + (height ? ' style="height:' + height + 'px"' : "") + ">" + Skeleton.person() +
                        Skeleton.line(widths[i % widths.length]) + Skeleton.line(widths[(i + 2) % widths.length] - 20) + "</div>";
                }
                return '<div class="sk-wrap ' + grid.className + '" aria-hidden="true">' + cards + "</div>";
            }
            var lines = "";
            for (var l = 0; l < 5; l++) lines += Skeleton.line(widths[l]);
            return '<div class="sk-wrap sk-stack" style="padding:18px" aria-hidden="true">' + lines + "</div>";
        },
        /** Swaps a list's content for its skeleton (same height, so nothing jumps). Returns the old HTML. */
        show: function (container) {
            var previous = container.innerHTML;
            container.style.minHeight = container.offsetHeight + "px";
            container.innerHTML = Skeleton.forContent(container) + '<span class="visually-hidden" role="status">Loading…</span>';
            container.classList.add("has-skeleton");
            return previous;
        },
        done: function (container) {
            container.classList.remove("has-skeleton");
            container.style.minHeight = "";
        },
        modal: function () {
            var field = function (w) { return '<span class="sk-stack" style="flex:none">' + Skeleton.line(w, "sk-line--sm") + '<span class="sk sk-field"></span></span>'; };
            return '<div class="sk-modal" aria-busy="true"><span class="visually-hidden" role="status">Loading…</span>' +
                '<span class="sk-row" aria-hidden="true"><span class="sk sk-icon"></span><span class="sk-stack">' + Skeleton.line(45, "sk-line--lg") + Skeleton.line(72, "sk-line--sm") + "</span></span>" +
                '<span aria-hidden="true" style="display:contents">' + field(22) + field(30) + field(18) + "</span>" +
                '<span class="sk-modal__foot" aria-hidden="true"><span class="sk sk-btn"></span><span class="sk sk-btn"></span></span></div>';
        },
        /** Marks a form control (dropdown, panel) as loading. */
        field: function (el, on) {
            if (!el) return;
            el.classList.toggle("is-skeleton", !!on);
            if (on) el.setAttribute("aria-busy", "true"); else el.removeAttribute("aria-busy");
        }
    };
    window.App.skeleton = Skeleton;

    // ------------------------------------------------------------------ enhancements applied to new HTML
    function enhance(root) {
        $$("select[data-searchable]", root).forEach(Combo.init);
        $$("[data-dropzone]", root).forEach(initDropzone);
        if (window.jQuery && jQuery.validator && jQuery.validator.unobtrusive) {
            $$("form", root).forEach(function (f) {
                jQuery(f).removeData("validator").removeData("unobtrusiveValidation");
                jQuery.validator.unobtrusive.parse(f);
            });
        }
        var forms = $$("form", root);
        if (root.nodeName === "FORM") forms.push(root);
        forms.forEach(function (f) {
            Validate.adopt(f);
            // A form the server sent back with errors still holds what the user typed: it is not saved.
            if (f.querySelector(".input-validation-error, .validation-summary-errors, .field-validation-error:not(:empty)")) Unsaved.force(f);
        });
        var autofocus = root.querySelector && root.querySelector("[data-autofocus]");
        if (autofocus && root !== doc) setTimeout(function () { autofocus.focus(); }, 30);
        // Lets page-specific scripts (operations.js) set up the new content.
        doc.dispatchEvent(new CustomEvent("app:enhance", { detail: root }));
    }

    // ------------------------------------------------------------------ modal
    var modal = doc.getElementById("app-modal");
    var modalUrl = null;

    function openModal(url) {
        if (!modal) { window.location.href = url; return; }
        modalUrl = url;
        modal.classList.remove("modal--wide");
        modal.innerHTML = Skeleton.modal();
        if (!modal.open) modal.showModal();
        request(url).then(function (r) {
            if (!r.ok) {
                closeModal();
                toast(r.json && r.data.message ? r.data.message : "This could not be opened. Refresh the page and try again.", "error");
                return;
            }
            fillModal(r.data);
        }).catch(function () { closeModal(); toast("No connection to the server.", "error"); });
    }

    function fillModal(html) {
        modal.innerHTML = html;
        if (modal.querySelector("[data-modal-wide]")) modal.classList.add("modal--wide");
        enhance(modal);
        var focus = modal.querySelector("[data-autofocus]") || modal.querySelector("input:not([type=hidden]), select, button");
        if (focus) setTimeout(function () { focus.focus(); }, 30);
    }

    function closeModal() { if (modal && modal.open) modal.close(); }

    function modalDirty() { return !!modal && modal.open && $$("form", modal).some(Unsaved.isDirty); }

    /** Cancel, Escape, a click outside or opening another dialog: never drops typed changes without asking. */
    function confirmDiscardModal() {
        if (!modalDirty()) return Promise.resolve(true);
        return Dialog.confirm({
            title: "Discard your changes?",
            message: "The changes in this form have not been saved. If you close it now, they will be lost.",
            confirmText: "Discard changes", cancelText: "Keep editing",
            tone: "warning", confirmTone: "danger", focus: "cancel",
        });
    }
    function requestCloseModal() {
        confirmDiscardModal().then(function (ok) { if (ok) closeModal(); });
    }

    if (modal) {
        modal.addEventListener("click", function (e) {
            if (e.target === modal) requestCloseModal(); // backdrop
            if (e.target.closest("[data-modal-close]")) { e.preventDefault(); requestCloseModal(); }
        });
        modal.addEventListener("keydown", function (e) {
            if (e.key !== "Escape" || e.defaultPrevented) return; // an open dropdown handles its own Escape
            e.preventDefault();
            requestCloseModal();
        });
        modal.addEventListener("cancel", function (e) {
            e.preventDefault();
            requestCloseModal();
        });
        modal.addEventListener("close", function () { modal.innerHTML = ""; });
    }

    doc.addEventListener("click", function (e) {
        var link = e.target.closest("[data-modal]");
        if (!link || e.ctrlKey || e.metaKey || e.shiftKey) return;
        e.preventDefault();
        var url = link.getAttribute("href") || link.getAttribute("data-modal");
        // "Add student" follows the class currently shown (the filter can change without a page reload).
        var context = link.getAttribute("data-class-context");
        var select = context && doc.querySelector(context);
        if (select) url = select.value ? "/classes/" + encodeURIComponent(select.value) + "/students/create" : "/students/create";
        confirmDiscardModal().then(function (ok) { if (ok) openModal(url); });
    });

    // ------------------------------------------------------------------ AJAX forms
    function submitAjax(form, submitter) {
        if (window.jQuery && jQuery(form).data("validator") && !jQuery(form).valid()) return;
        // One request at a time per form: a double click or Enter pressed twice must not save twice.
        if (form.dataset.submitting) return;
        form.dataset.submitting = "1";
        var data = new FormData(form);
        if (submitter && submitter.name) data.append(submitter.name, submitter.value);
        setBusy(submitter, true);
        request(form.action, { method: (form.method || "post").toUpperCase(), body: data })
            .then(function (r) {
                if (r.json) {
                    if (r.data.success) {
                        var reopen = submitter && submitter.getAttribute("data-reopen");
                        Unsaved.clean(form);
                        toast(r.data.message || "Saved.");
                        if (r.data.redirect) { Unsaved.navigate(r.data.redirect); return; }
                        refreshLists();
                        if (reopen && modal && modal.open) openModal(fillTemplate(reopen, form));
                        else closeModal();
                    } else {
                        toast(r.data.message || "This could not be saved.", "error");
                        if (r.status === 404) { closeModal(); refreshLists(); }
                    }
                } else if (r.status === 422 && modal && modal.contains(form)) {
                    fillModal(r.data); // the form again, with the server's validation messages (still unsaved)
                    $$("form", modal).forEach(Unsaved.force);
                    toast("Some fields need attention. Nothing was saved yet.", "warning");
                } else {
                    toast("Something went wrong (" + r.status + "). Please try again.", "error");
                }
            })
            .catch(function () { toast("No connection to the server. Nothing was saved.", "error"); })
            .finally(function () { delete form.dataset.submitting; setBusy(submitter, false); });
    }

    function fillTemplate(template, form) {
        return template.replace(/\{(\w+)\}/g, function (_, name) {
            var field = form.elements[name];
            return field ? encodeURIComponent(field.value) : "";
        });
    }

    function setBusy(button, on) {
        if (!button) return;
        button.classList.toggle("is-busy", on);
        button.disabled = on;
        if (on && !button.querySelector(".spinner")) {
            var s = doc.createElement("span");
            s.className = "spinner";
            s.setAttribute("aria-hidden", "true");
            button.insertBefore(s, button.firstChild);
        }
    }

    doc.addEventListener("submit", function (e) {
        var form = e.target;
        var submitter = e.submitter || form.querySelector("[type=submit]");
        var ajax = form.hasAttribute("data-ajax-form");
        // A plain form submit loads another page (list filters and invalid forms are already prevented).
        var navigates = !ajax && !e.defaultPrevented && (!form.target || form.target === "_self");
        var needsConfirm = form.hasAttribute("data-confirm") && !form.dataset.confirmed;
        var needsLeave = navigates && !form._leaveConfirmed && Unsaved.any(form);

        if (needsConfirm || needsLeave) {
            e.preventDefault();
            if (form._promptOpen) return; // a second change event while the question is already on screen
            form._promptOpen = true;
            (needsConfirm ? confirmDialog(form) : Promise.resolve(true))
                .then(function (ok) { return ok && navigates ? Unsaved.confirmLeave(form) : ok; })
                .then(function (ok) {
                    form._promptOpen = false;
                    if (!ok) { revertFilter(form); return; }
                    if (ajax) { submitAjax(form, submitter); return; }
                    if (needsConfirm) form.dataset.confirmed = "1";
                    form._leaveConfirmed = true;
                    setTimeout(function () { form._leaveConfirmed = false; }, 4000); // e.g. a download: the page stays
                    // Submit again through the browser, keeping the button that was pressed.
                    if (form.requestSubmit) {
                        if (submitter && submitter.form === form && !submitter.disabled) form.requestSubmit(submitter); else form.requestSubmit();
                    } else { setBusy(submitter, true); Unsaved.suspend(); form.submit(); }
                });
            return;
        }

        if (ajax) {
            e.preventDefault();
            submitAjax(form, submitter);
            return;
        }

        if (navigates) {
            // The form's own values are being sent: nothing is lost, so no browser prompt.
            Unsaved.suspend();
            if (form.dataset.confirmed || form.hasAttribute("data-loading")) setBusy(submitter, true);
        }
    });

    /** A filter (date, class…) whose page change was cancelled shows the current page's values again. */
    function revertFilter(form) {
        if ((form.getAttribute("method") || "get").toLowerCase() !== "get") return;
        form.reset();
        $$("select", form).forEach(function (s) { if (s._combo) s._combo.sync(); });
    }

    // ------------------------------------------------------------------ confirm dialog for data-confirm forms
    function confirmDialog(form) {
        return Dialog.confirm({
            title: form.getAttribute("data-confirm-title") || "Are you sure?",
            message: form.getAttribute("data-confirm"),
            confirmText: form.getAttribute("data-confirm-button") || "Delete",
            tone: form.getAttribute("data-confirm-tone") || "danger",
        });
    }

    // ------------------------------------------------------------------ AJAX lists
    function listUrl(form) {
        var params = new URLSearchParams();
        new FormData(form).forEach(function (value, key) { if (value !== "" && value != null) params.append(key, value); });
        var qs = params.toString();
        return form.getAttribute("action").split("?")[0] + (qs ? "?" + qs : "");
    }

    function loadList(container, url, push) {
        container.classList.add("is-loading");
        container.setAttribute("aria-busy", "true");
        // Skeleton only if the answer takes longer than a blink (no flicker on fast responses).
        var previous = null;
        var timer = setTimeout(function () { previous = Skeleton.show(container); }, 150);
        return request(url).then(function (r) {
            if (!r.ok || r.json) { Unsaved.navigate(url); return; }
            container.innerHTML = r.data;
            enhance(container);
            if (push) history.replaceState({ list: true }, "", url);
        }).catch(function () {
            // Never leave the skeleton: put the previous content back and say what happened.
            if (previous !== null) { container.innerHTML = previous; enhance(container); }
            toast("Could not load the list. Check the connection.", "error");
        }).finally(function () {
            clearTimeout(timer);
            Skeleton.done(container);
            container.classList.remove("is-loading");
            container.removeAttribute("aria-busy");
        });
    }

    function refreshLists() {
        // Pages without an AJAX list (profiles, boards) opt in to a full reload after a save.
        if (doc.querySelector("[data-refresh-on-save]")) { reloadWithToasts(); return; }
        $$("[data-ajax-list]").forEach(function (c) {
            // Pages whose totals sit outside the list (Classes) reload completely; open toasts survive the reload.
            if (c.hasAttribute("data-refresh-page")) { reloadWithToasts(); return; }
            loadList(c, window.location.pathname + window.location.search, false);
        });
    }

    function reloadWithToasts() {
        // Another form on the page may still hold unsaved changes: ask before the reload drops them.
        Unsaved.confirmLeave(null, {
            title: "Reload the page?",
            consequence: "The page reloads to show what was just saved, and those changes would be lost.",
            confirmText: "Reload page", cancelText: "Keep editing",
        }).then(function (ok) {
            if (!ok) { toast("Saved. Reload the page when you are done to see the latest records.", "info"); return; }
            var kept = $$(".toast:not(.is-leaving)").map(function (t) {
                var match = t.className.match(/toast--(\w+)/);
                return { m: t.querySelector(".toast__text").textContent, t: match ? match[1] : "success" };
            });
            try { sessionStorage.setItem("sm.toasts", JSON.stringify(kept)); } catch (e) { /* storage blocked */ }
            Unsaved.suspend();
            window.location.reload();
        });
    }

    $$("form[data-list-filter]").forEach(function (form) {
        var container = doc.querySelector(form.getAttribute("data-list-filter"));
        if (!container) return;
        var timer = null;
        var run = function () {
            var page = form.querySelector("[name=page]");
            if (page) page.value = "";
            loadList(container, listUrl(form), true);
        };
        form.addEventListener("submit", function (e) { e.preventDefault(); clearTimeout(timer); run(); });
        form.addEventListener("input", function (e) {
            if (e.target.matches("input[type=search], input[type=text]")) { clearTimeout(timer); timer = setTimeout(run, 320); }
        });
        form.addEventListener("change", function (e) {
            if (e.target.matches("select, input[type=date], input[type=checkbox]")) { clearTimeout(timer); run(); }
        });
    });

    doc.addEventListener("click", function (e) {
        var link = e.target.closest("[data-ajax-list] a[data-list-link]");
        if (!link || e.ctrlKey || e.metaKey) return;
        e.preventDefault();
        var container = link.closest("[data-ajax-list]");
        var url = link.getAttribute("href");
        syncFilterForm(url);
        loadList(container, url, true);
    });
    doc.addEventListener("change", function (e) {
        var size = e.target.closest("[data-ajax-list] select[data-list-pagesize]");
        if (!size) return;
        var container = size.closest("[data-ajax-list]");
        var url = new URL(window.location.href);
        url.searchParams.set("pageSize", size.value);
        url.searchParams.delete("page");
        $$("form[data-list-filter] [name=pageSize]").forEach(function (i) { i.value = size.value; });
        loadList(container, url.pathname + url.search, true);
    });

    // Keeps the filter form in step when a chip link inside the list clears a filter.
    function syncFilterForm(url) {
        var params = new URL(url, window.location.origin).searchParams;
        $$("form[data-list-filter]").forEach(function (form) {
            $$("input[name], select[name]", form).forEach(function (field) {
                if (field.type === "hidden" && field.name !== "sort" && field.name !== "dir" && field.name !== "pageSize") return;
                var v = params.get(field.name) || "";
                if (field.value !== v) {
                    field.value = v;
                    if (field._combo) field._combo.sync();
                }
            });
        });
    }

    // ------------------------------------------------------------------ searchable select
    var comboSeq = 0;
    var Combo = {
        init: function (select) {
            if (select._combo) return;
            var id = "combo-" + (++comboSeq);
            var wrap = doc.createElement("div");
            wrap.className = "combo";
            select.parentNode.insertBefore(wrap, select);
            wrap.appendChild(select);
            select.classList.add("combo__native");
            select.tabIndex = -1;
            select.setAttribute("aria-hidden", "true");

            var button = doc.createElement("button");
            button.type = "button";
            button.className = (select.className.indexOf("select") >= 0 ? "select " : "input ") + "combo__button";
            button.setAttribute("aria-haspopup", "listbox");
            button.setAttribute("aria-expanded", "false");
            var labelEl = select.id ? doc.querySelector('label[for="' + select.id + '"]') : null;
            if (labelEl) { labelEl.id = labelEl.id || id + "-label"; button.setAttribute("aria-labelledby", labelEl.id + " " + id + "-value"); labelEl.setAttribute("for", id + "-btn"); }
            else if (select.getAttribute("aria-label")) button.setAttribute("aria-label", select.getAttribute("aria-label"));
            button.id = id + "-btn";
            button.innerHTML = '<span class="combo__value" id="' + id + '-value"></span>';
            wrap.appendChild(button);

            var panel = null, list = null, search = null, active = -1, items = [];

            function options() {
                return Array.prototype.slice.call(select.options).map(function (o, i) {
                    return { index: i, value: o.value, text: o.text, note: o.getAttribute("data-note") || "", placeholder: o.value === "" };
                });
            }
            function sync() {
                var o = select.options[select.selectedIndex];
                var span = button.querySelector(".combo__value");
                var empty = !o || o.value === "";
                span.textContent = o ? o.text : "";
                span.classList.toggle("combo__placeholder", empty);
            }
            function render(filter) {
                var term = (filter || "").trim().toLowerCase();
                items = options().filter(function (o) {
                    if (o.placeholder && (select.hasAttribute("required") || select.hasAttribute("data-val-required"))) return false;
                    return !term || o.text.toLowerCase().indexOf(term) >= 0;
                });
                list.innerHTML = "";
                if (items.length === 0) {
                    var li = doc.createElement("li");
                    li.className = "combo__empty";
                    li.textContent = "No matches";
                    list.appendChild(li);
                }
                items.forEach(function (o, i) {
                    var li = doc.createElement("li");
                    li.className = "combo__option";
                    li.id = id + "-opt-" + i;
                    li.setAttribute("role", "option");
                    li.setAttribute("aria-selected", select.selectedIndex === o.index ? "true" : "false");
                    var t = doc.createElement("span");
                    t.textContent = o.text;
                    li.appendChild(t);
                    if (o.note) { var n = doc.createElement("span"); n.className = "combo__option-note"; n.textContent = o.note; li.appendChild(n); }
                    li.addEventListener("mousedown", function (ev) { ev.preventDefault(); choose(o); });
                    list.appendChild(li);
                });
                active = Math.max(0, items.findIndex(function (o) { return o.index === select.selectedIndex; }));
                highlight();
            }
            function highlight() {
                $$(".combo__option", list).forEach(function (li, i) { li.classList.toggle("is-active", i === active); });
                var cur = list.querySelector(".is-active");
                if (cur) { cur.scrollIntoView({ block: "nearest" }); search.setAttribute("aria-activedescendant", cur.id); }
            }
            function open() {
                if (panel) return;
                panel = doc.createElement("div");
                panel.className = "combo__panel";
                panel.innerHTML = '<input class="input combo__search" type="text" autocomplete="off" placeholder="Type to search…" role="combobox" aria-expanded="true" aria-controls="' + id + '-list" aria-label="Search options"><ul class="combo__list" role="listbox" id="' + id + '-list"></ul>';
                wrap.appendChild(panel);
                list = panel.querySelector("ul");
                search = panel.querySelector("input");
                render("");
                var rect = wrap.getBoundingClientRect();
                if (window.innerHeight - rect.bottom < 320 && rect.top > 320) panel.classList.add("is-up");
                button.setAttribute("aria-expanded", "true");
                search.focus();
                search.addEventListener("input", function () { render(search.value); });
                search.addEventListener("keydown", onKey);
                doc.addEventListener("mousedown", outside, true);
            }
            function close(focusButton) {
                if (!panel) return;
                panel.remove();
                panel = null;
                button.setAttribute("aria-expanded", "false");
                doc.removeEventListener("mousedown", outside, true);
                if (focusButton) button.focus();
            }
            function outside(ev) { if (!wrap.contains(ev.target)) close(false); }
            function choose(o) {
                var changed = select.selectedIndex !== o.index;
                select.selectedIndex = o.index;
                sync();
                close(true);
                if (changed) select.dispatchEvent(new Event("change", { bubbles: true }));
                validate();
            }
            function validate() {
                if (!window.jQuery) return;
                var $form = jQuery(select.form);
                if (!$form.data("validator")) return;
                jQuery(select).valid();
                var other = select.getAttribute("data-revalidate");
                if (other) {
                    var field = select.form.querySelector(other);
                    if (field && field.value) { jQuery(field).removeData("previousValue"); jQuery(field).valid(); }
                }
            }
            function onKey(ev) {
                if (ev.key === "ArrowDown") { ev.preventDefault(); active = Math.min(items.length - 1, active + 1); highlight(); }
                else if (ev.key === "ArrowUp") { ev.preventDefault(); active = Math.max(0, active - 1); highlight(); }
                else if (ev.key === "Enter") { ev.preventDefault(); if (items[active]) choose(items[active]); }
                else if (ev.key === "Escape") { ev.preventDefault(); ev.stopPropagation(); close(true); }
                else if (ev.key === "Tab") close(false);
            }

            button.addEventListener("click", function () { panel ? close(true) : open(); });
            button.addEventListener("keydown", function (ev) {
                if (ev.key === "ArrowDown" || ev.key === "ArrowUp" || (ev.key.length === 1 && !ev.ctrlKey && !ev.metaKey)) {
                    ev.preventDefault();
                    open();
                    if (ev.key.length === 1) { search.value = ev.key; render(ev.key); }
                }
            });
            // Validation focuses the invalid native select: send focus to the visible button.
            select.addEventListener("focus", function () { button.focus(); });
            select._combo = { sync: sync };
            sync();
        }
    };

    // ------------------------------------------------------------------ dropzone
    function initDropzone(zone) {
        var input = zone.querySelector("input[type=file]");
        var nameEl = zone.querySelector("[data-file-name]");
        if (!input) return;
        var show = function () {
            var f = input.files && input.files[0];
            zone.classList.toggle("has-file", !!f);
            if (f && nameEl) nameEl.textContent = f.name + " · " + Math.max(1, Math.round(f.size / 1024)) + " KB";
            var submit = zone.closest("form") && zone.closest("form").querySelector("[data-needs-file]");
            if (submit) submit.disabled = !f;
        };
        ["dragenter", "dragover"].forEach(function (t) { zone.addEventListener(t, function () { zone.classList.add("is-over"); }); });
        ["dragleave", "drop"].forEach(function (t) { zone.addEventListener(t, function () { zone.classList.remove("is-over"); }); });
        input.addEventListener("change", show);
        show();
    }

    // ------------------------------------------------------------------ import: column mapping
    var mapForm = doc.querySelector("[data-import-map]");
    if (mapForm) {
        $$("[data-refresh]", mapForm).forEach(function (sel) {
            sel.addEventListener("change", function () {
                mapForm.querySelector("[name=intent]").value = "refresh";
                mapForm.submit();
            });
        });
        var paint = function () {
            var used = {};
            $$("[data-map-field]", mapForm).forEach(function (sel) {
                if (sel.value !== "") used[sel.value] = sel.getAttribute("data-map-label");
            });
            $$("[data-col-tag]").forEach(function (tag) {
                var label = used[tag.getAttribute("data-col-tag")];
                tag.textContent = label || "Not imported";
                tag.className = "badge " + (label ? "badge--primary" : "badge--neutral");
            });
            $$("[data-col]").forEach(function (cell) {
                cell.style.opacity = used[cell.getAttribute("data-col")] ? "1" : "0.45";
            });
        };
        mapForm.addEventListener("change", function (e) { if (e.target.matches("[data-map-field]")) paint(); });
        paint();
    }

    // ------------------------------------------------------------------ leaving the page with unsaved changes
    // Registered last, so links handled in place (dialogs, list paging, downloads) are left alone.
    doc.addEventListener("click", function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        var link = e.target.closest("a[href]");
        if (!link || link.hasAttribute("download") || (link.target && link.target !== "_self")) return;
        var url;
        try { url = new URL(link.href, window.location.href); } catch (err) { return; }
        if (url.origin !== window.location.origin || !/^https?:$/.test(url.protocol)) return; // external: the browser asks
        if (url.pathname === window.location.pathname && url.search === window.location.search && url.hash) return; // same page
        if (!Unsaved.any()) return;
        e.preventDefault();
        Unsaved.navigate(url.href);
    });

    // ------------------------------------------------------------------ flash message from the server
    var flash = doc.getElementById("flash");
    if (flash) toast(flash.getAttribute("data-message"), flash.getAttribute("data-type"));

    // Toasts carried over a full reload by reloadWithToasts().
    try {
        var carried = JSON.parse(sessionStorage.getItem("sm.toasts") || "[]");
        sessionStorage.removeItem("sm.toasts");
        carried.forEach(function (k) { toast(k.m, k.t); });
    } catch (e) { /* storage blocked or invalid JSON */ }

    Theme.apply(Theme.get());
    enhance(doc);
})();
