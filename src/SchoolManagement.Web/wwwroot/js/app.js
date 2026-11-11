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
    var toastHost = doc.createElement("div");
    toastHost.className = "toasts";
    toastHost.setAttribute("role", "status");
    toastHost.setAttribute("aria-live", "polite");
    doc.body.appendChild(toastHost);
    var toastIcons = { success: "check-circle", error: "alert-circle", warning: "alert-triangle", info: "info" };

    function icon(name, cls) {
        return '<svg class="icon ' + (cls || "") + '" aria-hidden="true"><use href="#i-' + name + '"></use></svg>';
    }

    function toast(message, type) {
        type = type || "success";
        var el = doc.createElement("div");
        el.className = "toast toast--" + type;
        el.innerHTML = '<span class="toast__icon">' + icon(toastIcons[type] || "info") + '</span><span class="toast__text"></span>' +
            '<button class="icon-btn" type="button" aria-label="Dismiss">' + icon("x", "icon--sm") + "</button>";
        el.querySelector(".toast__text").textContent = message;
        var remove = function () { el.classList.add("is-leaving"); setTimeout(function () { el.remove(); }, 200); };
        el.querySelector("button").addEventListener("click", remove);
        toastHost.appendChild(el);
        setTimeout(remove, type === "error" ? 8000 : 5000);
    }
    window.App = { toast: toast };

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

    if (modal) {
        modal.addEventListener("click", function (e) {
            if (e.target === modal) closeModal(); // backdrop
            if (e.target.closest("[data-modal-close]")) { e.preventDefault(); closeModal(); }
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
        openModal(url);
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
                        toast(r.data.message || "Saved.");
                        if (r.data.redirect) { window.location.href = r.data.redirect; return; }
                        refreshLists();
                        if (reopen && modal && modal.open) openModal(fillTemplate(reopen, form));
                        else closeModal();
                    } else {
                        toast(r.data.message || "This could not be saved.", "error");
                        if (r.status === 404) { closeModal(); refreshLists(); }
                    }
                } else if (r.status === 422 && modal && modal.contains(form)) {
                    fillModal(r.data); // the form again, with the server's validation messages
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

        if (form.hasAttribute("data-confirm") && !form.dataset.confirmed) {
            e.preventDefault();
            confirmDialog(form).then(function (ok) {
                if (!ok) return;
                if (form.hasAttribute("data-ajax-form")) submitAjax(form, submitter);
                else { form.dataset.confirmed = "1"; setBusy(submitter, true); form.submit(); }
            });
            return;
        }

        if (form.hasAttribute("data-ajax-form")) {
            e.preventDefault();
            submitAjax(form, submitter);
            return;
        }

        if (form.hasAttribute("data-loading") && !e.defaultPrevented) {
            if (window.jQuery && jQuery(form).data("validator") && !jQuery(form).valid()) return;
            setBusy(submitter, true);
        }
    });

    // ------------------------------------------------------------------ confirm dialog
    var confirmEl = doc.getElementById("confirm-dialog");
    function confirmDialog(form) {
        return new Promise(function (resolve) {
            if (!confirmEl) { resolve(window.confirm(form.getAttribute("data-confirm"))); return; }
            var tone = form.getAttribute("data-confirm-tone") || "danger";
            confirmEl.querySelector("[data-confirm-title]").textContent = form.getAttribute("data-confirm-title") || "Are you sure?";
            confirmEl.querySelector("[data-confirm-text]").textContent = form.getAttribute("data-confirm");
            var ok = confirmEl.querySelector("[data-confirm-ok]");
            ok.textContent = form.getAttribute("data-confirm-button") || "Delete";
            ok.className = "btn " + (tone === "danger" ? "btn--danger" : "btn--primary");
            var iconBox = confirmEl.querySelector(".modal__icon");
            iconBox.className = "modal__icon " + (tone === "danger" ? "modal__icon--danger" : "modal__icon--warning");
            var done = function (value) {
                confirmEl.removeEventListener("close", onClose);
                ok.removeEventListener("click", onOk);
                if (confirmEl.open) confirmEl.close();
                resolve(value);
            };
            var onOk = function () { done(true); };
            var onClose = function () { done(false); };
            ok.addEventListener("click", onOk);
            confirmEl.addEventListener("close", onClose);
            confirmEl.showModal();
            setTimeout(function () { confirmEl.querySelector("[data-confirm-cancel]").focus(); }, 30);
        });
    }
    if (confirmEl) {
        confirmEl.addEventListener("click", function (e) {
            if (e.target === confirmEl || e.target.closest("[data-confirm-cancel]")) confirmEl.close();
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
            if (!r.ok || r.json) { window.location.href = url; return; }
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
        var kept = $$(".toast").map(function (t) {
            var match = t.className.match(/toast--(\w+)/);
            return { m: t.querySelector(".toast__text").textContent, t: match ? match[1] : "success" };
        });
        try { sessionStorage.setItem("sm.toasts", JSON.stringify(kept)); } catch (e) { /* storage blocked */ }
        window.location.reload();
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
